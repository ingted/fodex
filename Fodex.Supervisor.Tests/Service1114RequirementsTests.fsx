#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module Service1114RequirementsTests
#endif

open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// P0 先確認現行 entry 宣告的 quotation 真正拒絕新控制請求。
/// 未接通 wire 之前不能宣稱 SQL cache 原子性或 model pair 已有功能驗證。
/// P2 必須接續 C04 的 event/KV rollback，以及 M04 的非法 effort 零副作用測試。
let parseThroughRegistry request =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "first-use declarations"
    let operation =
        FodexFun<string,ServiceRequestV100,exn>(
            VersionRegistry<FeasibleFun<string,ServiceRequestV100,exn>>.BaseCD,"Service.ParseRequest")
        |> FodexFun.pin None |> Option.get
    operation.f.Value |> ignore
    FeasibleFun.invoke (ServiceProtocolFodexFunV100.encode request) operation

// 此值故意不加 [<Tests>]：P1 尚未實作，不能讓既有611項或VS adapter誤報整輪完成。
// 檔案仍 Compile 於原測試專案；獨立 FSI 會明確執行並回報目前的紅測試。
let pendingTests=testList "1114 service requirements P0" [
    testCase "C04 prerequisite cache mutation reaches control admission before SQL atomicity" <| fun () ->
        let request={ServiceProtocolFodexFunV100.request "cache" Guid.Empty with
                        text=JsonSerializer.Serialize {|action="c";key="review";value="原文\n第二行"|}}
        match parseThroughRegistry request with
        | Ok actual -> Expect.equal actual.text request.text "value preserved without a model prompt"
        | Error error -> failtestf "cache atomic command cannot be admitted: %s" error.Message

    testCase "M04 prerequisite model and effort travel in one control operation" <| fun () ->
        let request={ServiceProtocolFodexFunV100.request "set_model" (Guid.NewGuid()) with
                        text=JsonSerializer.Serialize {|model="test-model";effort="high"|}}
        match parseThroughRegistry request with
        | Ok actual -> Expect.equal actual.text request.text "model and effort are one proposal"
        | Error error -> failtestf "atomic model pair cannot be admitted: %s" error.Message
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] pendingTests<>0 then failwith "1114 service P0 requirements are not implemented yet"
#endif
