#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module VersionRegistryBaselineTests
#endif

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// 此 fixture 示範「宣告內建函數」與「在 node 上執行函數」的分界。
/// 模組初始化只取得 BaseCD；不建立 ActorSystem、不執行 quotation，也不連 DB。
/// 生產實作 DLL 的完整自動初始化另須獨立程序驗收，不能用本 fixture 冒充。
[<AutoOpen>]
module BaselineDefinitions =
    let baselineVersion = System.Version(1, 0, 0)
    let updatedVersion = System.Version(1, 0, 1)
    let baselineTarget = "ContractTests.Baseline.Executor"

    /// 第一次 input 準備一個接受 input 的函數；Invoke 會把同一 input 交給它。
    /// 執行使用 actual 裡面的 node，而不是 Versioned 建構子的 registry node。
    let baselineQuotation =
        <@ fun (prepared: FodexNode * string) ->
            fun (actual: FodexNode * string) ->
                if Object.ReferenceEquals(fst prepared, fst actual) && snd prepared = snd actual then
                    Ok ((fst actual).Name + ":" + snd actual)
                else
                    Error (InvalidOperationException("Both stages must receive the same input") :> exn) @>

    let baselineFunction = FeasibleFun<FodexNode * string, string, exn>(baselineQuotation)
    let baselineCatalog = VersionRegistry<FeasibleFun<FodexNode * string, string, exn>>.BaseCD

    // 自動註冊的是 actor-independent 定義；案例呼叫前不需 register node。
    do FodexFun<FodexNode * string, string, exn>(baselineCatalog, baselineTarget)
           .Register(baselineVersion, baselineFunction)

[<Tests>]
let tests =
    testSequenced <| testList "Registry baseline requirements" [
        testCase "REG-001 built-in definition is available without creating a node or calling register" <| fun () ->
            let declared = FodexFun<FodexNode * string, string, exn>(baselineCatalog, baselineTarget)
            Expect.isSome declared[baselineVersion] "AutoOpen fixture initialization declared its quotation"
            Expect.isTrue (Object.ReferenceEquals(declared[baselineVersion].Value, baselineFunction))
                "the stored object is the declared FeasibleFun, not an evaluated runtime resource"

        testCase "REG-002 node fallback owns both dictionary levels instead of aliasing BaseCD" <| fun () ->
            withNode <| fun first ->
                withNode <| fun second ->
                    let a = VersionRegistry<FeasibleFun<FodexNode * string, string, exn>>.RegistryFor first
                    let b = VersionRegistry<FeasibleFun<FodexNode * string, string, exn>>.RegistryFor second
                    Expect.isFalse (Object.ReferenceEquals(a, baselineCatalog)) "node must not own BaseCD itself"
                    Expect.isFalse (Object.ReferenceEquals(a, b)) "different nodes own different target tables"
                    Expect.isFalse (Object.ReferenceEquals(a[baselineTarget], baselineCatalog[baselineTarget]))
                        "copying only the outer table still leaks per-version mutations"
                    Expect.isFalse (Object.ReferenceEquals(a[baselineTarget], b[baselineTarget]))
                        "different nodes own different version tables"
                    Expect.isTrue (Object.ReferenceEquals(a.[baselineTarget].[baselineVersion].Value, baselineFunction))
                        "the actor-independent definition itself can be shared"

        testCase "REG-003 Register on one node cannot modify BaseCD or an existing sibling node" <| fun () ->
            withNode <| fun first ->
                withNode <| fun second ->
                    let a = FodexFun<FodexNode * string, string, exn>(first, baselineTarget)
                    let b = FodexFun<FodexNode * string, string, exn>(second, baselineTarget)
                    try
                        a.Register(updatedVersion, baselineFunction)
                        Expect.isSome a[updatedVersion] "writer sees its update"
                        Expect.isNone b[updatedVersion] "sibling does not change through a shared mutable reference"
                        Expect.isFalse (baselineCatalog[baselineTarget].ContainsKey updatedVersion)
                            "runtime registration never changes the compiled baseline"
                    finally
                        // 讓本案例即使在舊的 alias bug 上失敗，也不污染其他測試。
                        baselineCatalog[baselineTarget].TryRemove updatedVersion |> ignore

        testCase "REG-004 CleanVersioned cannot delete another node or baseline version" <| fun () ->
            withNode <| fun first ->
                withNode <| fun second ->
                    let a = VersionRegistry<FeasibleFun<FodexNode * string, string, exn>>(first)
                    let b = FodexFun<FodexNode * string, string, exn>(second, baselineTarget)
                    try
                        a.CleanVersioned(baselineTarget, fun () -> [|baselineVersion|])
                        Expect.isNone (a.GetVersioned(baselineTarget)[baselineVersion]) "selected node removed its entry"
                        Expect.isSome b[baselineVersion] "sibling remains usable"
                        Expect.isTrue (baselineCatalog[baselineTarget].ContainsKey baselineVersion) "baseline remains available"
                    finally
                        baselineCatalog.[baselineTarget].[baselineVersion] <-
                            { Version = baselineVersion; Value = baselineFunction }

        testCase "REG-005 registry selection and execution ActorSystem are independent" <| fun () ->
            withNode <| fun registryNode ->
                withNode <| fun executionNode ->
                    let selected = FodexFun<FodexNode * string, string, exn>(registryNode, baselineTarget)
                                   |> FodexFun.pin (Some "1.0.0") |> Option.get
                    selected.f.Value |> ignore
                    let input = executionNode, "work"
                    let prepared = selected.Invoke input
                    Expect.equal (prepared input |> expectOk) (executionNode.Name + ":work")
                        "both Invoke stages receive executionNode; registryNode is not captured by quotation"

        testCase "REG-006 a pinned definition does not depend on its registry node staying alive" <| fun () ->
            let selected =
                withNode <| fun registryNode ->
                    FodexFun<FodexNode * string, string, exn>(registryNode, baselineTarget)
                    |> FodexFun.pin (Some "1.0.0") |> Option.get
            withNode <| fun executionNode ->
                Expect.equal (FeasibleFun.invoke (executionNode, "later") selected |> expectOk)
                    (executionNode.Name + ":later") "no old ActorSystem lifetime captured in the definition"

        testCase "REG-007 concurrent first lookup publishes one node table while keeping baseline separate" <| fun () ->
            withNode <| fun node ->
                let reads =
                    Array.init 16 (fun _ -> Task.Run(fun () ->
                        VersionRegistry<FeasibleFun<FodexNode * string, string, exn>>.RegistryFor node))
                    |> Task.WhenAll
                    |> fun work -> work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                for table in reads do
                    Expect.isTrue (Object.ReferenceEquals(table, reads[0])) "CWT publishes one table for this node"
                Expect.isFalse (Object.ReferenceEquals(reads[0], baselineCatalog)) "published table is a node-owned copy"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "VersionRegistryBaselineTests failed: %d" testExitCode
#endif
