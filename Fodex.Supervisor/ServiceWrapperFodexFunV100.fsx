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

type ServiceWrapperCreationV100 = ActorSystem * UniContext * string * ParseResults<ConsoleArgumentV405> * ServiceCreateInputV100

module ServiceWrapperFodexFunV100 =
    let version=System.Version(1,0,0)
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
                let defaultsText=ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments(defaults.GetAllResults())
                                 |> Array.map System.Text.Json.JsonSerializer.Serialize<string> |> String.concat " "
                let settings=parse(parent,defaultsText,argv) |> required
                let folder=Path.Combine(settings.GetResult ConsoleArgumentV405.Output_Directory,"wrappers",sid.ToString("N"))
                Directory.CreateDirectory folder |> ignore
                let logger=NLog.LogManager.GetLogger("Fodex.Service.Wrapper").WithProperty("traceFile",Path.Combine(folder,"io.jsonl"))
                let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Service.Wrapper")
                ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
                let catalog=FodexFunBinding.pin<_,System.Text.Json.JsonElement array> node (Some "4.0.2") FodexAgentFodexFunV402.targetCatalog
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
                let factory=FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node (Some "4.0.5") FodexAgentFodexFunV405.target
                                (node,cs,session,catalog,transport,url,connectOutput,sink) |> required
                owned<-Some factory
                let loop=factory.CreateReActLoop(node,arguments,ctx) |> required
                let wrapper=factory.CreateWrapper(node,arguments,ctx) |> required
                wrapper.Start() |> required |> ignore
                // 重連歷史讀 SQL；不依賴可能被 compact/prune 的 graph，也不另養一份無界 output queue。
                let collect ()=loop.Collect session |> Result.map Seq.toArray
                let dispose ()=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask()
                return Ok(wrapper,collect,dispose)
            with error ->
                match owned with
                | Some factory ->
                    try do! (box factory :?> IAsyncDisposable).DisposeAsync().AsTask()
                    with cleanup -> parent.logger.Error("Service create cleanup: "+cleanup.Message)
                | None -> ()
                return Error error }))

    let quotation:Expr<ServiceWrapperCreationV100 -> FeasibleT<_,Task<Result<ServiceWrapperV100,exn>>,exn>> = <@ fun _ -> fun input -> create input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
