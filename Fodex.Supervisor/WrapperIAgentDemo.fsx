#I @"bin\Debug\net10.0"
#r "Akka.dll"
#r "Argu.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"

// 必須在 Contract 載入後再載入：Contract 也有全域 PL module，
// Visual Studio FSI 全選執行時，最後載入的才是 defaultArgumentsText 要用的五參數 parser。
#load @"C:\Users\Administrator\.codex\lib\ParseLine.fsx"

open System
open System.IO
open System.Runtime.Loader
open Akka.Actor
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let buildOutputDirectory =
    Path.Combine(__SOURCE_DIRECTORY__, "bin", "Debug", "net10.0")
    |> Path.GetFullPath

let coreRuntimeDirectory =
    Path.GetDirectoryName(typeof<obj>.Assembly.Location)

let aspNetRuntimeDirectory =
    Path.Combine(
        Directory.GetParent(coreRuntimeDirectory).Parent.FullName,
        "Microsoft.AspNetCore.App",
        Path.GetFileName coreRuntimeDirectory
    )

let runtimeLoadContext =
    AssemblyLoadContext.GetLoadContext(typeof<ActorSystem>.Assembly)

runtimeLoadContext.add_Resolving(
    Func<
        AssemblyLoadContext,
        Reflection.AssemblyName,
        Reflection.Assembly
    >(fun context assemblyName ->
        [|
            Path.Combine(buildOutputDirectory, assemblyName.Name + ".dll")
            Path.Combine(aspNetRuntimeDirectory, assemblyName.Name + ".dll")
        |]
        |> Array.tryFind File.Exists
        |> Option.map context.LoadFromAssemblyPath
        |> Option.defaultValue null)
)

// 在 Visual Studio FSI 執行本檔時，只需要修改這個字串。
// 外部 argv 僅供 automation 覆寫；參數仍由 FAkka.Argu 的 SupervisorArgs.parse 處理。
let defaultArgumentsText =
    """--mode host --working-directory "F:\\fodex" --native-runtime app-server --runtime-version 1.0.0 --sandbox readOnly --approval-policy never --timeout-seconds 180"""

let defaultArguments =
    PL.parseLine [| ' ' |] (Some '"') None true defaultArgumentsText

let automationArguments =
    fsi.CommandLineArgs
    |> Array.skip 1

let arguments =
    if Array.isEmpty automationArguments then
        defaultArguments
    else
        automationArguments

let require label = function
    | Ok value -> value
    | Error (error: exn) ->
        raise (
            InvalidOperationException(
                sprintf "%s failed: %s" label error.Message,
                error
            )
        )

let options =
    SupervisorArgs.parse arguments
    |> require "SupervisorArgs.parse"

let context =
    LoggingRuntime.createContext ()
    |> require "LoggingRuntime.createContext"

let node =
    ActorSystem.Create(
        sprintf "fodex-wrapper-demo-%s" (Guid.NewGuid().ToString("N"))
    )

FodexFunRegistrationV100.registerAll<string, string>
    (node, options, context)
|> require "register v1.0.0"

let wrapper: IAgent<string, string> =
    FodexAgent<string, string>.createWrapper(
        node,
        options.ContractArguments,
        context
    )
    |> require "create wrapper"

let reActLoop: ReActLoop<string, string> =
    FodexAgent<string, string>.createReActLoop(
        node,
        options.ContractArguments,
        context
    )
    |> require "create ReActLoop"

let wrapperSessionId =
    Application.wrapperSession wrapper
    |> require "get wrapper session"

let mainParticipant: IAgent<string, string> =
    wrapper.AcquireParticipant(
        (1L, wrapperSessionId),
        Application.participantArguments options.NativeRuntime
    )
    |> require "acquire main participant"

wrapper.Start()
|> require "start wrapper"
|> ignore

let sendGate = obj ()
let mutable stopped = false

// IAgent.Send 是 actor message delivery：Ok 1 表示已接受一筆訊息，並不是模型輸出。
// 這個函數刻意保留 raw 行為，方便在 FSI 直接觀察 Contract 的 IAgent surface。
let sendRawToWrapper (text: string) =
    if stopped then
        Error (InvalidOperationException("wrapper demo is stopped") :> exn)
    elif String.IsNullOrWhiteSpace text then
        Error (ArgumentException("text must not be empty") :> exn)
    else
        wrapper.Send text

// 完整一回合必須經過 ReActLoop；模型文字會即時寫到 FSI console。
// 回傳 Ok 0 表示該回合正常結束，durable ReActDataStream 已先於輸出寫入 JSONL。
let send (text: string) =
    lock sendGate (fun () ->
        if stopped then
            Error (InvalidOperationException("wrapper demo is stopped") :> exn)
        elif String.IsNullOrWhiteSpace text then
            Error (ArgumentException("text must not be empty") :> exn)
        else
            Application.executeTurn
                options
                text
                wrapper
                mainParticipant
                reActLoop
                wrapperSessionId)

let getWrapperSessionId () = wrapper.GetSessionId()
let getCodexSessionId () = mainParticipant.GetSessionId()

let stop () =
    lock sendGate (fun () ->
        if not stopped then
            mainParticipant.Stop() |> ignore
            wrapper.Stop() |> ignore
            node.Terminate().GetAwaiter().GetResult()
            |> ignore
            stopped <- true)

printfn "wrapper is ready: IAgent<string, string>"
printfn "Full turn: send \"只回覆 WRAPPER_DEMO_OK\""
printfn "Raw actor delivery only: sendRawToWrapper \"hello\""
printfn "Cleanup: stop ()"
