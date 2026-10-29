#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module FodexFunTests
#endif

open System
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts

// 此函數是測試 quotation factory；不是未經 registry 的 production operation。
let valueFunction text =
    FeasibleFun<string,string,exn>(<@ fun prepared -> fun actual -> Ok (text + prepared + actual) @>)

[<Tests>]
let tests =
    testList "FodexFun contract" [
        testCase "eval then send the same input to both stages" <| fun () ->
            let calls = ResizeArray<string>()
            let fn =
                FeasibleFun<string,string,exn>(
                    <@ fun prepared ->
                        calls.Add("prepare:" + prepared)
                        fun actual ->
                            calls.Add("invoke:" + actual)
                            Ok (prepared + actual) @>)
            // Evaluate quotation 本身不執行兩層函數；invoke 才依定義傳兩次同一 input。
            fn.f.Value |> ignore
            Expect.isEmpty calls "eval is not invocation"
            Expect.equal (FeasibleFun.invoke "A" fn |> expectOk) "AA" "ROP result"
            Expect.sequenceEqual calls ["prepare:A"; "invoke:A"] "same input, correct order"

        testCase "startup pins maximum System.Version, not lexical order" <| fun () ->
            withNode <| fun node ->
                let registry = FodexFun<string,string,exn>(node, "test-value")
                registry.Register(System.Version "1.9.0", valueFunction "old:")
                registry.Register(System.Version "1.10.0", valueFunction "current:")
                let pinned = FodexFun.pin None registry |> Option.get
                registry.Register(System.Version "2.0.0", valueFunction "future:")
                Expect.equal (FeasibleFun.invoke "x" pinned |> expectOk) "current:xx" "pin remains stable"
                Expect.equal (FeasibleFun.invoke "x" (FodexFun.pin None registry |> Option.get) |> expectOk)
                    "future:xx" "new resolution sees hot-loaded version"

        testCase "same version overwrite changes new lookup, not old pin" <| fun () ->
            withNode <| fun node ->
                let registry = FodexFun<string,string,exn>(node, "test-value")
                registry.Register(System.Version "1.0.0", valueFunction "before:")
                let pinned = FodexFun.pin (Some "1.0.0") registry |> Option.get
                registry.Register(System.Version "1.0.0", valueFunction "after:")
                Expect.equal (FeasibleFun.invoke "x" pinned |> expectOk) "before:xx" "old pin"
                Expect.equal (FeasibleFun.invoke "x" (registry[System.Version "1.0.0"] |> Option.get) |> expectOk)
                    "after:xx" "Register overwrites"

        testCase "explicit missing or malformed version never falls back" <| fun () ->
            withNode <| fun node ->
                let registry = FodexFun<string,string,exn>(node, "test-value")
                registry.Register(System.Version "1.0.0", valueFunction "present:")
                Expect.isNone (FodexFun.pin (Some "9.0.0") registry) "missing version"
                Expect.isNone (FodexFun.pin (Some "not-a-version") registry) "malformed version"

        testCase "same target and version are isolated across ActorSystems" <| fun () ->
            withNode <| fun first ->
                withNode <| fun second ->
                    let a = FodexFun<string,string,exn>(first, "same-target")
                    let b = FodexFun<string,string,exn>(second, "same-target")
                    a.Register(System.Version "1.0.0", valueFunction "A:")
                    Expect.isNone b.Instance "no cross-node entry"
                    b.Register(System.Version "1.0.0", valueFunction "B:")
                    Expect.equal (FeasibleFun.invoke "x" (a.Instance |> Option.get) |> expectOk) "A:xx" "first node"
                    Expect.equal (FeasibleFun.invoke "x" (b.Instance |> Option.get) |> expectOk) "B:xx" "second node"
    ]

#if INTERACTIVE
// 不接自製 argv parser；載入本檔即執行本組測試。失敗會拋例外，automation 不會誤判成功。
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "FodexFunTests failed: %d" testExitCode
#endif
