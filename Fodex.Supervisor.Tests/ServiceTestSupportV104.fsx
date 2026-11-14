#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceTestSupportV104
#endif
open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let options folder =
    FodexFunctions.initialize()
    ConsolePolicyFodexFunV403.pinBase<_,Argu.ParseResults<ConsoleArgumentV405>> "4.0.5" "Console.ParseStartup"
        (ctx,ConsolePolicyFodexFunV405.defaults,[|"--mainagent-type";"codex";"--model";"known";"--port";"0";"--output-directory";folder|]) |> required
let ask (root:IActorRef) request =
    root.Ask<string>(ServiceProtocolFodexFunV101.encode request,TimeSpan.FromSeconds 15.) |> wait
let catalog:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(Fresh(DateTimeOffset.UtcNow,[|
        JsonSerializer.SerializeToElement {|id="known";model="known";supportedReasoningEfforts=[|{|reasoningEffort="low"|};{|reasoningEffort="high"|}|]|}|])))) @>

/// SQL/loop/owner/service均是真實程式；只替代外部模型目錄，不產生LLM回合。
let withService action=withDatabase(fun cs -> withNode(fun node ->
    let folder=Path.Combine("E:/deletable/fodex/1114-service-104",Guid.NewGuid().ToString("N"))
    let defaults=options folder
    FodexFunRegistry.register node NativeModelDirectoryFodexFunV100.target (System.Version(1,0,0)) catalog
    let makeLoop=evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.5" node FodexFunRegistrationV301.targetLoop
    let cacheLoop=makeLoop((node,cs,cacheSession,ctx,ignore),"--reactloopsharedcache 4.0.5") |> required
    let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV105,exn>>> "1.0.5" node ServiceWrapperFodexFunV105.target
    let root=evaluated<ServiceSupervisorInputV104,IActorRef> "1.0.4" node ServiceSupervisorFodexFunV104.target
                (node,ctx,(fun input -> create(node,ctx,cs,defaults,input)),true,false,defaults,cacheLoop) |> required
    try action node cs root folder
    finally
        ask root (ServiceProtocolFodexFunV101.request "shutdown" Guid.Empty) |> ignore
        let stop=SharedCacheFodexFunV100.request "shutdown" Guid.Empty None
        cacheLoop.ReActLoopHandler.Ask<string>(SharedCacheFodexFunV100.encode stop,TimeSpan.FromSeconds 10.) |> wait |> ignore))

