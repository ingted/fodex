# 06. Expecto 純函數測試

這批測試只驗證 quotation、`FeasibleFun` 與 `FodexFun`，不建立 actor、不啟動 process。

```fsharp
module FodexFunTests

open System
open Expecto
open FSharp.Quotations
open Fodex.Supervisor.Contracts.AliasMod
open Fodex.Supervisor.Contracts.Concepts

let expectOk label result =
    match result with
    | Ok value -> value
    | Error error ->
        failtestf "%s returned Error: %A" label error

let twiceInputQuotation:
    Expr<int -> FeasibleT<int, int, string>> =
    <@
        fun outerInput ->
            fun innerInput ->
                Ok (outerInput * 100 + innerInput)
    @>

let addQuotation amount:
    Expr<int -> FeasibleT<int, int, string>> =
    <@
        fun outerInput ->
            let preparedAmount = outerInput + amount

            fun innerInput ->
                Ok (preparedAmount + innerInput)
    @>

let newRegistry target =
    let registry =
        CD<
            TargetName,
            CD<
                Version,
                VersionedTarget<FeasibleFun<int, int, string>>
            >
        >()

    FodexFun<int, int, string>(registry, target)

[<Tests>]
let tests =
    testList "FeasibleFun and FodexFun" [
        testCase "invoke sends the same input through both stages" <| fun _ ->
            let feasibleFun =
                FeasibleFun<int, int, string>(twiceInputQuotation)

            let actual =
                FeasibleFun.invoke 7 feasibleFun
                |> expectOk "twice input"

            Expect.equal actual 707
                "outer and inner functions must receive the same input"

        testCase "quotation evaluation stays lazy" <| fun _ ->
            let feasibleFun =
                FeasibleFun<int, int, string>(twiceInputQuotation)

            Expect.isFalse feasibleFun.f.IsValueCreated
                "construction must not evaluate the quotation"

            FeasibleFun.invoke 1 feasibleFun |> ignore

            Expect.isTrue feasibleFun.f.IsValueCreated
                "first invocation evaluates the quotation"

        testCase "exact version resolves the registered FeasibleFun" <| fun _ ->
            let registry = newRegistry "Exact"
            let version = Version(1, 0, 0)
            let feasibleFun =
                FeasibleFun<int, int, string>(addQuotation 10)

            registry.Register(version, feasibleFun)

            let resolved =
                registry[version]
                |> Option.defaultWith (fun () -> failtest "version missing")

            let actual =
                FeasibleFun.invoke 2 resolved
                |> expectOk "exact"

            Expect.equal actual 14
                "outer prepares 12 and inner receives 2"

        testCase "Instance selects the greatest System.Version" <| fun _ ->
            let registry = newRegistry "Latest"

            registry.Register(
                Version(1, 0, 0),
                FeasibleFun<int, int, string>(addQuotation 10)
            )

            registry.Register(
                Version(2, 0, 0),
                FeasibleFun<int, int, string>(addQuotation 20)
            )

            let latest =
                registry.Instance
                |> Option.defaultWith (fun () -> failtest "latest missing")

            let actual =
                FeasibleFun.invoke 2 latest
                |> expectOk "latest"

            Expect.equal actual 24
                "version 2.0.0 must be selected"

        testCase "same version registration overwrites registry only" <| fun _ ->
            let registry = newRegistry "Overwrite"
            let version = Version(1, 0, 0)
            let original =
                FeasibleFun<int, int, string>(addQuotation 10)

            registry.Register(version, original)
            let pinned = registry[version] |> Option.get

            registry.Register(
                version,
                FeasibleFun<int, int, string>(addQuotation 30)
            )

            let current = registry[version] |> Option.get

            let pinnedValue =
                FeasibleFun.invoke 2 pinned
                |> expectOk "pinned"

            let currentValue =
                FeasibleFun.invoke 2 current
                |> expectOk "current"

            Expect.equal pinnedValue 14
                "an already pinned FeasibleFun keeps its original behavior"

            Expect.equal currentValue 34
                "new resolution receives the overwritten version"
    ]
```

這些測試刻意禁止以下捷徑：

```fsharp
quotation |> QuotationEvaluator.Evaluate
```

測試必須經過：

```text
quotation -> FeasibleFun -> FodexFun -> resolve/pin -> FeasibleFun.invoke
```
