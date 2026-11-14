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


/// Service內部保留root自己的loop/session；wire仍只傳JSON，不傳這些resources。
type ServiceWrapperV105=ServiceWrapperV103*ReActLoop<string,obj>*ReActParticipantSessionId
type ServiceCreateV105=FeasibleT<ServiceCreateInputV100,Task<Result<ServiceWrapperV105,exn>>,exn>
type ServiceSupervisorInputV104=ActorSystem*UniContext*ServiceCreateV105*bool*bool*ParseResults<ConsoleArgumentV405>*ReActLoop<string,obj>
type ServiceCreationMessageV105=AsyncServiceCreatedV105 of Result<ServiceWrapperV105,exn>
type ServiceControlMessageV104=ServiceControlCompletedV104 of ServiceRequestV100*IActorRef*Result<System.Text.Json.JsonElement,exn>

/// Async SQL history is an implementation seam; the public Contract/wire remain unchanged.
[<AutoOpen>]
module ServiceWrapperFodexFunV105 =
    let version=System.Version(1,0,5)
    let target="Service.CreateWrapper"
    let required=function Ok value -> value | Error error -> raise error

    /// 每個 root wrapper 自己的 main hub 使用獨立 loopback port；service MCP endpoint 是另外一個界面。
    /// 這個 hub 仍只供該 root native 操作自己的 subs，不拿來當外部 caller 的 wrapper service。
    let freeUrl () =
        use listener=new Sockets.TcpListener(IPAddress.Loopback,0)
        listener.Start()
        sprintf "http://127.0.0.1:%d" ((listener.LocalEndpoint :?> IPEndPoint).Port)

    let create ((node,parent,cs,defaults,(sid,argv,sink)):ServiceWrapperCreationV100) =
        Ok(Task.Run(fun () -> task {
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
                // 與新startup policy共用PL相容quoting：Windows反斜線與字面Unicode都保留。
                let defaultsText=ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments(defaults.GetAllResults())
                                 |> ConsolePolicyFodexFunV410.quoteTokens
                let settings=parse(parent,defaultsText,argv) |> required
                let folder=Path.Combine(settings.GetResult ConsoleArgumentV405.Output_Directory,"wrappers",sid.ToString("N"))
                Directory.CreateDirectory folder |> ignore
                let logger=NLog.LogManager.GetLogger("Fodex.Service.Wrapper").WithProperty("traceFile",Path.Combine(folder,"io.jsonl"))
                let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Service.Wrapper")
                ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
                // 不查舊全域catalog；native/明確models操作使用新的provider-scoped目錄。
                let catalog=Array.empty<System.Text.Json.JsonElement>
                let url=freeUrl()
                let transport=if settings.GetResult ConsoleArgumentV405.Native_Adapter="app-server" then Mcp
                              else ShellClient(typeof<ConsoleArgumentV405>.Assembly.Location,url)
                let session=sid,Some(IPAddress.Parse(settings.GetResult ConsoleArgumentV405.Bind_Ip))
                let arguments=FodexFunBinding.pin<_,string> node (Some "4.0.5") ConsolePolicyFodexFunV405.targetArguments settings |> required
                let connectOutput hub =
                    // Raw stdio 留在獨立 output hub／NLog trace；不轉成 ReActEvent，不混進 service 的 event stream。
                    FodexFunBinding.pin<ActorSystem*UniContext*IActorRef*(NativeOutputBatchV300->unit),IActorRef>
                        node (Some "3.0.1") NativeOutputFodexFunV301.targetPrinter (node,ctx,hub,ignore) |> required |> ignore
                let factory=FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node (Some "4.1.0") FodexAgentFodexFunV410.target
                                (node,cs,session,catalog,transport,url,connectOutput,sink) |> required
                owned<-Some factory
                let loop=factory.CreateReActLoop(node,arguments,ctx) |> required
                let wrapper=factory.CreateWrapper(node,arguments,ctx) |> required
                // 冷建立也保留Main的邏輯編號，讓set_model有唯一目標；不是建立native資源。
                // 已恢復的Main及其參數不可被新預設值覆蓋；首次send才由wrapper建立IAgent/process。
                let graph=loop.ReadDataFlow session |> required
                let main=ReActTopologyFodexFunV301.relations graph |> Array.tryPick(function
                    | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Main when parent=(1L,session) -> Some child
                    | _ -> None)
                if main.IsNone then
                    let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"main",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
                    let nativeText=WrapperAgentV409.nativeArguments ctx arguments "main" None None
                    FodexFunBinding.pin<string*ReActParticipantSessionId*ReActParticipant*string,unit>
                        node (Some "4.0.2") ParticipantConfigurationFodexFunV402.targetSave (cs,session,participant,nativeText) |> required
                // Resolve the typed registry partition inside the LongRunning bootstrap.
                // A first receive must not synchronously load quotation SQL in the actor worker.
                let collectHistory=loop.PinCollectAsync(Some "3.0.1")
                // create/attach只建立邏輯wrapper與loop；首次send或需要native的控制才Start。
                // 重連歷史讀 SQL；不依賴可能被 compact/prune 的 graph，也不另養一份無界 output queue。
                let collect ()=collectHistory session
                let dispose ()=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask()
                return Ok((wrapper,collect,dispose),loop,session)
            with error ->
                match owned with
                | Some factory ->
                    let retry () = (box factory :?> IAsyncDisposable).DisposeAsync().AsTask()
                    try
                        do! retry()
                        return Error error
                    with cleanup -> return Error(ServiceCreateCleanupExceptionV103(error,cleanup,retry) :> exn)
                | None -> return Error error }))

    let quotation:Expr<ServiceWrapperCreationV100 -> FeasibleT<_,Task<Result<ServiceWrapperV105,exn>>,exn>> = <@ fun _ -> fun input -> create input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
