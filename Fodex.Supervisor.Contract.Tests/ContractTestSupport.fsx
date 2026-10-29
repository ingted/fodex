#if INTERACTIVE
// 先 build 本測試專案的 Debug 輸出，再於 FSI #load 任一 *Tests.fsx。
// 多數依賴引用 compiled tests 的 DLL；ObjectPool 用固定版本 NuGet #r 補齊 FSI 的載入環境。
// 不引用 Contract.Tests.dll：測試程式本身仍從目前的 .fsx 編譯/執行。
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#r "Akka.dll"
#r "Akka.Logger.NLog.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "FAkka.FSharp.Quotations.Evaluator.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
// Akka 反射建立的 serializer 依賴 ObjectPool。FSI 不會載入 testhost 的 ASP.NET shared framework。
#r "nuget: Microsoft.Extensions.ObjectPool, 10.0.14"
#r "Fodex.Supervisor.Contracts.dll"
#else
module ContractTestSupport
#endif

open System
open Akka.Actor
open Akka.Configuration
open Expecto
open LoggerUtil
open Fodex.Supervisor.Contracts.CoreMod

// 只省略測試輸出，不改 runPinned 的成功/錯誤行為。
let context =
    let logger =
        { new UniLogger() with
            member _.Info _ = ()
            member _.Debug _ = ()
            member _.Warn _ = ()
            member _.Error _ = ()
            member _.infof f = Printf.kprintf ignore f
            member _.debugf f = Printf.kprintf ignore f
            member _.warnf f = Printf.kprintf ignore f
            member _.errorf f = Printf.kprintf ignore f
            member _.pInfof f = Printf.kprintf ignore f
            member _.pDebugf f = Printf.kprintf ignore f
            member _.pWarnf f = Printf.kprintf ignore f
            member _.pErrorf f = Printf.kprintf ignore f
            member _.fInfof f = Printf.kprintf (fun s -> raise (Exception s)) f
            member _.fDebugf f = Printf.kprintf (fun s -> raise (Exception s)) f
            member _.fWarnf f = Printf.kprintf (fun s -> raise (Exception s)) f
            member _.fErrorf f = Printf.kprintf (fun s -> raise (Exception s)) f
            member _.pfErrorf f = Printf.kprintf (fun s -> raise (Exception s)) f }
    { logger = logger; usage = [ "ContractTests" ]; caller = [] }

/// 例：withNode (fun node -> ReActLoop<string,string>(node, node.DeadLetters, "", context) ...)。
/// 每個案例有獨立 registry 分區；finally 確保 assertion 失敗也會終止 node。
let withNode action =
    let config = ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF")
    let node = ActorSystem.Create("contract-" + Guid.NewGuid().ToString("N"), config)
    try action node
    finally node.Terminate().GetAwaiter().GetResult() |> ignore

/// 測試失敗直接列出原始 exception；不把 Error 當作預期值吞掉。
let expectOk (result: Result<'T, exn>) =
    match result with
    | Ok value -> value
    | Error error -> failtestf "Unexpected Error: %O" error

let session: ReActParticipantSessionId = Guid.Parse("fc000000-0000-0000-0000-000000000001"), None
let wrapper: ReActParticipant = 1L, session
let native: ReActParticipant = 2L, session
let wrapperEndpoint = ReActEndpoint.Participant wrapper
let nativeEndpoint = ReActEndpoint.Participant native

/// 同文字、不同事件 id 的兩個 edges，用來揭露把訊息錯誤合併的實作。
let eventEdge id : ReActDataStream<string> =
    nativeEndpoint, wrapperEndpoint, AgentCot (id, 1L, Message "same text")

/// 測試 fixture 而非 production writer；只建立原生 FGL graph，不假裝有 durable commit。
let graphWith ids : WrappedReActDataFlow<string> =
    WrappedReActDataFlow<string>(
        [ nativeEndpoint, (); wrapperEndpoint, () ],
        ids |> List.map eventEdge)

let createLoop node arguments =
    ReActLoop<string,string>(node, node.DeadLetters, arguments, context)
