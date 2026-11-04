#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#r "nuget: FAkka.FsPickler.Json, 10.2.2"
#load "../Fodex.Supervisor/QuotationStore.fsx"
#else
module QuotationStoreTests
#endif

open System
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

type Input = FodexNode * string

/// 只捕獲可序列化的字串常數；ActorSystem 留在函數 input，不藏在 quotation 的 Value。
let definition suffix =
    FeasibleFun<Input,string,exn>(
        <@ fun (_: Input) -> fun (node, text) -> Ok (node.Name + ":" + text + suffix) @>)

let invoke node text (fn: FeasibleFun<Input,string,exn>) =
    fn.f.Value |> ignore
    FeasibleFun.invoke (node, text) fn |> required

/// 隔離的 schema/連線只供這個案例；原 Persistence 設定與 BaseCD 在 finally 還原。
let withRegistry action =
    withDatabase <| fun cs ->
        let previous = VersionRegistry<FeasibleFun<Input,string,exn>>.Persistence
        let target = "SqlQuotation." + Guid.NewGuid().ToString("N")
        let version = System.Version(1, 0, 0)
        let baseline = VersionRegistry<FeasibleFun<Input,string,exn>>.BaseCD
        FodexFun<Input,string,exn>(baseline, target).Register(version, definition ":base")
        QuotationStore.configure<Input,string,exn> (fun _ -> cs)
        try
            QuotationStore.initialize cs |> required
            action cs target version
        finally
            VersionRegistry<FeasibleFun<Input,string,exn>>.Persistence <- previous
            baseline.TryRemove target |> ignore

/// SQL 文字全是測試內的固定故障注入，不接受使用者輸入或 connection secret 作為 SQL 片段。
let execute cs text =
    use connection = new SqlConnection(cs)
    connection.Open()
    use command = new SqlCommand(text, connection)
    command.ExecuteNonQuery() |> ignore

[<Tests>]
let tests =
    testSequenced <| testList "SQL quotation requirements" [
        testCase "DB-011 unconfigured storage permits baseline reads but rejects durable updates" <| fun () ->
            let previous=VersionRegistry<FeasibleFun<Input,string,exn>>.Persistence
            let target="SqlQuotation.ReadOnly."+Guid.NewGuid().ToString("N")
            let version=System.Version(1,0,0)
            let baseline=VersionRegistry<FeasibleFun<Input,string,exn>>.BaseCD
            FodexFun<Input,string,exn>(baseline,target).Register(version,definition ":base")
            try
                QuotationStore.configureOptional<Input,string,exn> (fun _ -> None)
                withNode <| fun node ->
                    let versions=FodexFun<Input,string,exn>(node,target)
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name+":x:base") "baseline remains usable"
                    Expect.throws (fun () -> versions.Register(version,definition ":lost")) "a no-store write must not appear successful"
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name+":x:base") "failed update is not published"
            finally
                VersionRegistry<FeasibleFun<Input,string,exn>>.Persistence<-previous
                baseline.TryRemove target |> ignore

        testCase "DB-001 SQL overrides each matching key and preserves other baseline versions" <| fun () ->
            withRegistry <| fun cs target version ->
                let fallbackVersion = System.Version(1, 0, 1)
                let dbOnlyVersion = System.Version(1, 0, 2)
                FodexFun<Input,string,exn>(VersionRegistry<FeasibleFun<Input,string,exn>>.BaseCD, target)
                    .Register(fallbackVersion, definition ":fallback")
                QuotationStore.write<Input,string,exn> cs
                    [|target, version, Some(definition ":db"); target, dbOnlyVersion, Some(definition ":db-only")|] |> required
                withNode <| fun node ->
                    let versions = FodexFun<Input,string,exn>(node, target)
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name + ":x:db") "DB wins"
                    Expect.equal (invoke node "x" versions[fallbackVersion].Value) (node.Name + ":x:fallback") "per-key fallback"
                    Expect.equal (invoke node "x" versions[dbOnlyVersion].Value) (node.Name + ":x:db-only") "DB-only definition"

        testCase "DB-004 same-version overwrite persists a new quotation without replacing an old pin" <| fun () ->
            withRegistry <| fun cs target version ->
                withNode <| fun node ->
                    let versions = FodexFun<Input,string,exn>(node, target)
                    let pinned = versions[version].Value
                    versions.Register(version, definition ":updated")
                    Expect.equal (invoke node "x" pinned) (node.Name + ":x:base") "old pin is stable"
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name + ":x:updated") "new lookup sees commit"
                withNode <| fun restarted ->
                    let loaded = FodexFun<Input,string,exn>(restarted, target).[version].Value
                    Expect.equal (invoke restarted "x" loaded) (restarted.Name + ":x:updated") "recreation uses SQL quotation"

        testCase "DB-007 SQL deletion prevents baseline resurrection after recreation" <| fun () ->
            withRegistry <| fun cs target version ->
                withNode <| fun node ->
                    VersionRegistry<FeasibleFun<Input,string,exn>>(node).CleanVersioned(target, fun () -> [|version|])
                let persisted = QuotationStore.load<Input,string,exn> cs |> required
                Expect.isTrue (persisted |> Array.exists(fun (name,v,value) -> name=target && v=version && value.IsNone))
                    "DB retains deletion, not a missing row"
                withNode <| fun restarted ->
                    Expect.isNone (FodexFun<Input,string,exn>(restarted, target).[version]) "no fallback resurrection"

        testCase "DB-003 equal names and versions of different signatures remain separate" <| fun () ->
            withRegistry <| fun cs target version ->
                QuotationStore.write<Input,string,exn> cs [|target,version,Some(definition ":text")|] |> required
                let number = FeasibleFun<int,int,exn>(<@ fun _ -> fun value -> Ok(value + 7) @>)
                QuotationStore.write<int,int,exn> cs [|target,version,Some number|] |> required
                let numbers = QuotationStore.load<int,int,exn> cs |> required
                Expect.equal numbers.Length 1 "only the requested function signature"
                let _,_,readNumber = numbers[0]
                readNumber.Value.f.Value |> ignore
                Expect.equal (FeasibleFun.invoke 5 readNumber.Value |> required) 12 "typed quotation roundtrip"
                Expect.equal (QuotationStore.load<Input,string,exn> cs |> required |> Array.length) 1 "text definition retained"

        testCase "DB-005 a rejected SQL write never publishes its value to the node" <| fun () ->
            withRegistry <| fun cs target version ->
                withNode <| fun node ->
                    let versions = FodexFun<Input,string,exn>(node, target)
                    versions.Register(version, definition ":committed")
                    execute cs "CREATE TRIGGER RejectQuotationWrite ON dbo.FodexFunctionVersion INSTEAD OF INSERT, UPDATE AS THROW 51001, 'fixture rejection', 1;"
                    Expect.throws (fun () -> versions.Register(version, definition ":uncommitted")) "SQL error reaches caller"
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name + ":x:committed") "memory unchanged"
                    let _,_,stored = QuotationStore.load<Input,string,exn> cs |> required |> Array.exactlyOne
                    Expect.equal (invoke node "x" stored.Value) (node.Name + ":x:committed") "SQL unchanged"

        testCase "DB-005B a later SQL row failure rolls back the whole Upsert and publishes none" <| fun () ->
            withRegistry <| fun cs target version ->
                withNode <| fun node ->
                    let registry = VersionRegistry<FeasibleFun<Input,string,exn>>(node)
                    let versions = registry.GetVersioned target
                    versions.Register(version, definition ":committed")
                    execute cs "ALTER TABLE dbo.FodexFunctionVersion ADD CONSTRAINT RejectSecondVersion CHECK ([Version] <> '2.0.0');"
                    let second = System.Version(2,0,0)
                    let incoming = FodexFun<Input,string,exn>(Collections.Concurrent.ConcurrentDictionary(), target)
                    incoming.Register(version, definition ":partial")
                    incoming.Register(second, definition ":rejected")
                    Expect.throws (fun () -> registry.UpsertVersioned(target, incoming)) "atomic batch failure"
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name + ":x:committed") "no partial memory update"
                    Expect.isNone versions[second] "second version not published"
                    let _,_,stored = QuotationStore.load<Input,string,exn> cs |> required |> Array.exactlyOne
                    Expect.equal (invoke node "x" stored.Value) (node.Name + ":x:committed") "first SQL update was rolled back"

        testCase "DB-006 damaged SQL quotation is an error and the same node may retry after repair" <| fun () ->
            withRegistry <| fun cs target version ->
                QuotationStore.write<Input,string,exn> cs [|target,version,Some(definition ":valid")|] |> required
                execute cs "UPDATE dbo.FodexFunctionVersion SET Quotation=N'broken quotation';"
                withNode <| fun node ->
                    Expect.throws (fun () -> FodexFun<Input,string,exn>(node,target) |> ignore) "no silent fallback"
                    QuotationStore.write<Input,string,exn> cs [|target,version,Some(definition ":repaired")|] |> required
                    let loaded = FodexFun<Input,string,exn>(node,target).[version].Value
                    Expect.equal (invoke node "x" loaded) (node.Name + ":x:repaired") "failed initialization was not cached"

        testCase "DB-009 quotation capturing a live ActorSystem is rejected before persistence" <| fun () ->
            withRegistry <| fun cs target version ->
                withNode <| fun node ->
                    let versions = FodexFun<Input,string,exn>(node,target)
                    let invalid = FeasibleFun<Input,string,exn>(<@ fun _ -> fun _ -> Ok node.Name @>)
                    Expect.throws (fun () -> versions.Register(version, invalid)) "ActorSystem must be an execution input"
                    Expect.equal (invoke node "x" versions[version].Value) (node.Name + ":x:base") "old definition retained"
                    Expect.isEmpty (QuotationStore.load<Input,string,exn> cs |> required) "no invalid quotation stored"

        testCase "DB-008 concurrent same-node writes publish the same last value that SQL committed" <| fun () ->
            withRegistry <| fun cs target version ->
                withNode <| fun node ->
                    let versions = FodexFun<Input,string,exn>(node,target)
                    Array.init 8 (fun n -> Task.Run(fun () -> versions.Register(version, definition (":"+string n))))
                    |> Task.WhenAll
                    |> fun work -> work.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()
                    let _,_,stored = QuotationStore.load<Input,string,exn> cs |> required |> Array.exactlyOne
                    Expect.equal (invoke node "x" versions[version].Value) (invoke node "x" stored.Value)
                        "save and local publish use the same ordering"

        testCase "DB-010 definitions can be loaded and evaluated before an execution node exists" <| fun () ->
            withRegistry <| fun cs target version ->
                QuotationStore.write<Input,string,exn> cs [|target,version,Some(definition ":offline")|] |> required
                let _,_,stored = QuotationStore.load<Input,string,exn> cs |> required |> Array.exactlyOne
                stored.Value.f.Value |> ignore
                withNode <| fun node ->
                    Expect.equal (invoke node "x" stored.Value) (node.Name + ":x:offline") "execution node is supplied later"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "QuotationStoreTests failed: %d" testExitCode
#endif
