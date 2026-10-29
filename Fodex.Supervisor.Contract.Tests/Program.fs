module Fodex.Supervisor.Contract.Tests.Program

open Expecto

// VSTest 直接探索各 .fsx 的 [<Tests>]，不需要執行 main。
// 明確的 EntryPoint 讓 F# 產生正確的 assembly initialization；不要用手動反射初始化取代。
// 這個入口也可供無參數的 dotnet run；單檔 FSI 執行路徑在各 *Tests.fsx 的 INTERACTIVE 區塊。
[<EntryPoint>]
let main _ =
    testList "Contract" [
        FodexFunTests.tests
        ReActDataStreamTests.tests
        ReActLoopRouteReActDataStreamTests.tests
        ReActLoopCollectTests.tests
        ReActLoopReadDataFlowTests.tests
    ]
    |> runTestsWithCLIArgs [] [||]
