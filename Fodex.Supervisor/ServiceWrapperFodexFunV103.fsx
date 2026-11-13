#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.IO
open System.Net
open System.Threading.Tasks
open Argu
open Akka.Actor
open LoggerUtil
open FodexLoggerAdapter
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod


/// Async SQL history is an implementation seam; the public Contract/wire remain unchanged.
type ServiceWrapperV103 = IAgent<string,obj> * (unit -> Task<Result<ReActDataStream<string> array,exn>>) * (unit -> Task)
type ServiceCreateV103 = FeasibleT<ServiceCreateInputV100,Task<Result<ServiceWrapperV103,exn>>,exn>
type ServiceSupervisorInputV102 = ActorSystem * UniContext * ServiceCreateV103 * bool * bool

/// Failed creation retains the exact owner's cleanup callback so Close can retry.
/// The callback is local-only; wire failures never serialize this exception object.
type ServiceCreateCleanupExceptionV103(error:exn,cleanupError:exn,retryCleanup:unit -> Task) =
    inherit Exception("Wrapper creation and cleanup failed",AggregateException(error,cleanupError))
    member _.RetryCleanup = retryCleanup

type ServiceCreationMessageV102 = AsyncServiceCreatedV102 of Result<ServiceWrapperV103,exn>

[<AutoOpen>]
module ServiceWrapperFodexFunV103 =
    let version=System.Version(1,0,3)
    let target="Service.CreateWrapper"
    let required=function Ok value -> value | Error error -> raise error

    /// 每個 root wrapper 自己的 main hub 使用獨立 loopback port；service MCP endpoint 是另外一個界面。
    /// 這個 hub 仍只供該 root native 操作自己的 subs，不拿來當外部 caller 的 wrapper service。
    let freeUrl () =
        use listener=new Sockets.TcpListener(IPAddress.Loopback,0)
        listener.Start()
        sprintf "http://127.0.0.1:%d" ((listener.LocalEndpoint :?> IPEndPoint).Port)

    let create ((node,parent,cs,defaults,(sid,argv,sink)):ServiceWrapperCreationV100) =
        Ok(task {
            let mutable owned:FodexAgent<string,obj> option=None
            try
                let overrideOptions=ConsolePolicyFodexFunV405.parser.ParseCommandLine(argv,raiseOnUsage=false)
                if overrideOptions.IsUsageRequested then invalidArg "args" "Create expects native configuration, not CLI help"
                for option in overrideOptions.GetAllResults() do
                    match option with
                    | ConsoleArgumentV405.Mainagent_Type _ | ConsoleArgumentV405.Native_Adapter _ | ConsoleArgumentV405.Agy_Exe _
                    | ConsoleArgumentV405.Model _ | ConsoleArgumentV405.Effort _ | ConsoleArgumentV405.Fast _ | ConsoleArgumentV405.Memory _
                    | ConsoleArgumentV405.Subagent_Type _ | ConsoleArgumentV405.Subagent_Mode _
                    | ConsoleArgumentV405.Sub_Codex_Model _ | ConsoleArgumentV405.Sub_Codex_Effort _
                    | ConsoleArgumentV405.Sub_Agy_Model _ | ConsoleArgumentV405.Sub_Agy_Effort _ | ConsoleArgumentV405.Working_Directory _ -> ()
                    | _ -> invalidArg "args" "Only native/participant configuration may be overridden; database, network and output are owned by the service"
                let parse=FodexFunBinding.pin<_,ParseResults<ConsoleArgumentV405>> node (Some "4.0.5") ConsolePolicyFodexFunV405.targetParse
                let defaultsText=ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments(defaults.GetAllResults())
                                 |> Array.map System.Text.Json.JsonSerializer.Serialize<string> |> String.concat " "
                let settings=parse(parent,defaultsText,argv) |> required
                let folder=Path.Combine(settings.GetResult ConsoleArgumentV405.Output_Directory,"wrappers",sid.ToString("N"))
                Directory.CreateDirectory folder |> ignore
                let logger=NLog.LogManager.GetLogger("Fodex.Service.Wrapper").WithProperty("traceFile",Path.Combine(folder,"io.jsonl"))
                let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Service.Wrapper")
                ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
                let catalog=FodexFunBinding.pin<_,System.Text.Json.JsonElement array> node (Some "4.0.6") FodexAgentFodexFunV406.targetCatalog
                                (node,NativeAgentV200.executable(),settings.GetResult ConsoleArgumentV405.Working_Directory) |> required
                let url=freeUrl()
                let transport=if settings.GetResult ConsoleArgumentV405.Native_Adapter="app-server" then Mcp
                              else ShellClient(typeof<ConsoleArgumentV405>.Assembly.Location,url)
                let session=sid,Some(IPAddress.Parse(settings.GetResult ConsoleArgumentV405.Bind_Ip))
                let arguments=FodexFunBinding.pin<_,string> node (Some "4.0.5") ConsolePolicyFodexFunV405.targetArguments settings |> required
                let connectOutput hub =
                    // Raw stdio 留在獨立 output hub／NLog trace；不轉成 ReActEvent，不混進 service 的 event stream。
                    FodexFunBinding.pin<ActorSystem*UniContext*IActorRef*(NativeOutputBatchV300->unit),IActorRef>
                        node (Some "3.0.1") NativeOutputFodexFunV301.targetPrinter (node,ctx,hub,ignore) |> required |> ignore
                let factory=FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node (Some "4.0.8") FodexAgentFodexFunV408.target
                                (node,cs,session,catalog,transport,url,connectOutput,sink) |> required
                owned<-Some factory
                let loop=factory.CreateReActLoop(node,arguments,ctx) |> required
                let wrapper=factory.CreateWrapper(node,arguments,ctx) |> required
                // Resolve the typed registry partition inside the LongRunning bootstrap.
                // A first receive must not synchronously load quotation SQL in the actor worker.
                let collectHistory=loop.PinCollectAsync(Some "3.0.1")
                let! started = wrapper.PinStartAsync(node,Some "3.0.1")()
                started |> required |> ignore
                // 重連歷史讀 SQL；不依賴可能被 compact/prune 的 graph，也不另養一份無界 output queue。
                let collect ()=collectHistory session
                let dispose ()=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask()
                return Ok(wrapper,collect,dispose)
            with error ->
                match owned with
                | Some factory ->
                    let retry () = (box factory :?> IAsyncDisposable).DisposeAsync().AsTask()
                    try
                        do! retry()
                        return Error error
                    with cleanup -> return Error(ServiceCreateCleanupExceptionV103(error,cleanup,retry) :> exn)
                | None -> return Error error })

    let quotation:Expr<ServiceWrapperCreationV100 -> FeasibleT<_,Task<Result<ServiceWrapperV103,exn>>,exn>> = <@ fun _ -> fun input -> create input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
