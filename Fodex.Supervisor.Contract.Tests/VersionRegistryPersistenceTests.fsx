#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module VersionRegistryPersistenceTests
#endif

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// 這組測試驗證 Contract 的發布時序；SQL adapter 的實際 transaction/quotation 另有 integration tests。
/// 使用 int 讓 store 與 registry 的行為清楚可見，不把 serializer 的錯誤混進這裡。
/// 每例都還原 Persistence 與 BaseCD；不留下其他測試會繼承的設定。
let withStore action =
    let target = "ContractTests.Persistence." + Guid.NewGuid().ToString("N")
    let version = System.Version(1, 0, 0)
    let baseline = VersionRegistry<int>.BaseCD
    let versions = ConcurrentDictionary<System.Version, VersionedTarget<int>>()
    versions[version] <- { Version = version; Value = 10 }
    baseline[target] <- versions
    let previous = VersionRegistry<int>.Persistence
    let persisted = ConcurrentDictionary<string * System.Version, int option>()
    let reads, writes = ref 0, ref 0
    let rejectRead, rejectWrite = ref false, ref false
    let load (_: FodexNode) =
        Interlocked.Increment reads |> ignore
        if rejectRead.Value then Error (InvalidOperationException("fixture read rejected") :> exn)
        else Ok (persisted.ToArray() |> Array.map(fun pair -> fst pair.Key, snd pair.Key, pair.Value))
    let save (_: FodexNode, changes: (string * System.Version * int option) array) =
        Interlocked.Increment writes |> ignore
        if rejectWrite.Value then Error (InvalidOperationException("fixture write rejected") :> exn)
        else
            for name, revision, value in changes do persisted[(name, revision)] <- value
            Ok ()
    VersionRegistry<int>.Persistence <- load, save
    try action target version persisted reads writes rejectRead rejectWrite
    finally
        VersionRegistry<int>.Persistence <- previous
        baseline.TryRemove target |> ignore

[<Tests>]
let tests =
    testSequenced <| testList "Registry persistence requirements" [
        testCase "DB-001 persisted version overrides baseline and only missing keys fall back" <| fun () ->
            withStore <| fun target version stored _ _ _ _ ->
                let second = System.Version(1, 0, 1)
                let third = System.Version(1, 0, 2)
                VersionRegistry<int>.BaseCD.[target].[second] <- { Version = second; Value = 11 }
                stored[(target, version)] <- Some 20
                stored[(target, third)] <- Some 30
                withNode <| fun node ->
                    let selected = Versioned<int>(node, target)
                    Expect.equal selected[version] (Some 20) "same key uses persisted value"
                    Expect.equal selected[second] (Some 11) "missing persisted version falls back individually"
                    Expect.equal selected[third] (Some 30) "DB-only version is retained"
                    Expect.equal (VersionRegistry<int>.BaseCD.[target].[version].Value) 10 "load never mutates baseline"

        testCase "DB-004 Register persists before publishing and a later node reads the change" <| fun () ->
            withStore <| fun target version stored _ writes _ _ ->
                withNode <| fun node ->
                    let selected = Versioned<int>(node, target)
                    selected.Register(version, 20)
                    Expect.equal stored[(target, version)] (Some 20) "write-through received the new value"
                    Expect.equal selected[version] (Some 20) "committed value is now published"
                    Expect.equal writes.Value 1 "one update, one persistence operation"
                    withNode <| fun restarted ->
                        Expect.equal (Versioned<int>(restarted, target)[version]) (Some 20) "new node loads persisted override"

        testCase "DB-005 failed Register leaves the node and baseline unchanged" <| fun () ->
            withStore <| fun target version stored _ writes _ rejectWrite ->
                withNode <| fun node ->
                    let selected = Versioned<int>(node, target)
                    Expect.equal selected[version] (Some 10) "initialize before fault injection"
                    rejectWrite.Value <- true
                    Expect.throws (fun () -> selected.Register(version, 99)) "failure must reach the caller"
                    Expect.equal selected[version] (Some 10) "uncommitted value is never published"
                    Expect.isEmpty stored "failed write is not persisted"
                    Expect.equal writes.Value 1 "failure is not automatically retried"
                    Expect.equal (VersionRegistry<int>.BaseCD.[target].[version].Value) 10 "baseline stays compiled value"

        testCase "DB-007 deleting a baseline version persists its absence across node recreation" <| fun () ->
            withStore <| fun target version stored _ _ _ _ ->
                withNode <| fun node ->
                    let registry = VersionRegistry<int>(node)
                    registry.CleanVersioned(target, fun () -> [|version|])
                    Expect.equal stored[(target, version)] None "deletion is durable, not a missing DB row"
                    Expect.isNone (registry.GetVersioned(target)[version]) "current node removed the version"
                withNode <| fun restarted ->
                    Expect.isNone (Versioned<int>(restarted, target)[version]) "fallback must not resurrect a deleted version"
                    Expect.equal (VersionRegistry<int>.BaseCD.[target].[version].Value) 10 "compiled declaration is unchanged"

        testCase "DB-005B failed Upsert publishes none of the requested versions" <| fun () ->
            withStore <| fun target version stored _ writes _ rejectWrite ->
                withNode <| fun node ->
                    let registry = VersionRegistry<int>(node)
                    let selected = registry.GetVersioned target
                    let incoming = Versioned<int>(ConcurrentDictionary(), target)
                    let second = System.Version(2, 0, 0)
                    incoming.Register(version, 20)
                    incoming.Register(second, 30)
                    rejectWrite.Value <- true
                    Expect.throws (fun () -> registry.UpsertVersioned(target, incoming)) "one batch failure"
                    Expect.equal selected[version] (Some 10) "old value retained"
                    Expect.isNone selected[second] "new version not partially published"
                    Expect.equal writes.Value 1 "one persistence operation for the whole batch"
                    Expect.isEmpty stored "no partial durable update"

        testCase "DB-006 read failure is not missing data and does not poison future initialization" <| fun () ->
            withStore <| fun target version stored reads _ rejectRead _ ->
                withNode <| fun node ->
                    rejectRead.Value <- true
                    Expect.throws (fun () -> VersionRegistry<int>.RegistryFor node |> ignore) "no silent baseline fallback"
                    rejectRead.Value <- false
                    stored[(target, version)] <- Some 42
                    Expect.equal (Versioned<int>(node, target)[version]) (Some 42) "same node can retry after storage recovers"
                    Expect.equal reads.Value 2 "failed initialization was not cached"

        testCase "DB-008 concurrent initialization only reads and publishes one node dictionary" <| fun () ->
            withStore <| fun target version stored reads writes _ _ ->
                stored[(target, version)] <- Some 42
                withNode <| fun node ->
                    let results =
                        Array.init 16 (fun _ -> Task.Run(fun () -> VersionRegistry<int>.RegistryFor node))
                        |> Task.WhenAll
                        |> fun work -> work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                    Expect.isGreaterThanOrEqual reads.Value 1 "factory may be evaluated multiple times"
                    Expect.equal writes.Value 0 "loading never writes defaults back to DB"
                    for result in results do
                        Expect.isTrue (Object.ReferenceEquals(results[0], result)) "one published dictionary"
                        Expect.equal (result.[target].[version].Value) 42 "fully loaded state"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "VersionRegistryPersistenceTests failed: %d" testExitCode
#endif
