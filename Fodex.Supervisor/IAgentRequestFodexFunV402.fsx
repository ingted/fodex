#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Argu
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 工具 adapter 的傳輸語法；一般使用者 prompt 不必知道此 DU。
/// Argu 的 argv 以 JSON 陣列運送，避免 instruction 經 shell quoting 後損壞。
/// JSON 由 .NET 產生，MCP 呼叫者只提供一般 who/instruct 字串。
module HubRequestV402 =
    type Argument =
        | Order of who:string * instruction:string
        | Receive
        | Status
        | List
        | Control of command:string
        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Order _ -> "Dispatch once to an existing who."
                | Receive -> "Read completed results without waiting or consuming automatic delivery."
                | Status -> "Read hub counters."
                | List -> "List subagents and their actual who/N."
                | Control _ -> "Control owned subagents through the wrapper command parser."

module IAgentRequestFodexFunV402 =
    open HubRequestV402
    let version = System.Version(4,0,2)
    let parser = ArgumentParser.Create<HubRequestV402.Argument>(programName="/hub")

    /// 例：format (Order("sub-3","review first line\nthen second line"))。
    /// 只有 /hub 前綴由此 protocol 接管；其餘文字完整交給原有 wrapper command handler。
    let format operation =
        // PrintCommandLineArguments 已經產出正確 token 邊界，不重新串接與拆分它。
        // 例如中文、多行、開頭引號與 Windows path 都保持原字元。
        parser.PrintCommandLineArguments [operation]
        |> JsonSerializer.Serialize<string array>
        |> fun arguments -> "/hub "+arguments

    let request ((node,instance,text):IAgentFunInput<string>) =
        try
            match instance with
            | Agent.Native _ -> Error(NotSupportedException "Request handles wrapper commands, not native generation" :> exn)
            | Agent.Wrapper _ ->
                let reply = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                let complete result = reply.TrySetResult result |> ignore
                let message =
                    if text.StartsWith("/hub ",StringComparison.Ordinal) then
                        // 這是工具傳輸 envelope，不是另一個 argument parser；DU 仍由 Argu 驗證。
                        let argv = JsonSerializer.Deserialize<string array>(text.Substring 5)
                        if isNull argv then invalidArg "input" "Expected an argv array"
                        match parser.ParseCommandLine(argv,raiseOnUsage=false).GetAllResults() with
                        | [Order(who,instruction)] -> box(MainHubOrder(who,instruction,complete))
                        | [Receive] -> box(MainHubReceive complete)
                        | [Status] -> box(MainHubStatus complete)
                        | [List] -> box(MainHubList complete)
                        | [Control command] -> box(MainHubControl(command,complete))
                        | _ -> invalidArg "input" "Expected exactly one /hub operation"
                    else box(WrapperRequest(text,complete))
                let timeout = node.Settings.Config.GetTimeSpan("fodex.agent.request-timeout",TimeSpan.FromSeconds 30.)
                if timeout<=TimeSpan.Zero then invalidArg "request-timeout" "Expected a positive timeout"
                let send = FodexFunBinding.pin<IAgentFunInput<obj>,int> node (Some "2.0.0") targetIAgentSend
                send(node,instance,message) |> Result.map(fun _ -> task {
                    try return! reply.Task.WaitAsync timeout
                    with error -> return Error error })
        with error -> Error error

    let quotation : Expr<IAgentFunInput<string> -> FeasibleT<_,Task<Result<string,exn>>,exn>> =
        <@ fun _ -> fun input -> request input @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare targetIAgentRequest version quotation |]
