// M018 direct adapter. Existing V300 composition is reused without changing its source/API.
// This file owns no COMM mailbox, participant, bootstrap prompt or receive loop.
#load "LineReferencesV301.fsx"
#load "LineDirectV301Core.fsx"
open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type Settings =
    { ConnectionString: string
      WorkingDirectory: string
      Model: string
      Effort: string
      OutputDirectory: string }

type Runtime =
    { WrapperSessionId: Guid
      RequestAsync: Guid * string * CancellationToken -> Task<string>
      IsRetired: unit -> bool
      DisposeAsync: unit -> Task }

let required = function Ok value -> value | Error(ex:exn) -> raise ex
let freePort () =
    use listener = new TcpListener(IPAddress.Loopback,0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

let start (settings: Settings) = task {
    let logicalSid = Guid.NewGuid()
    let output = Path.Combine(Path.GetFullPath settings.OutputDirectory,logicalSid.ToString("N"))
    Directory.CreateDirectory output |> ignore
    let logger = NLog.LogManager.GetLogger("Fodex.Line.V301").WithProperty("traceFile",Path.Combine(output,"io.jsonl"))
    let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Line.V301")
    ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
    let node = RemoteControlFodexFunV300.createNode ("fodex-line-v301-"+logicalSid.ToString("N")) "127.0.0.1" (freePort())
    try
        NativeModelCatalogFodexFunV300.register node
        let models = NativeModelCatalogFodexFunV300.load node (NativeAgentV200.executable()) settings.WorkingDirectory |> required
        let arguments =
            SupervisorArgs.format [ SupervisorArgument.Model settings.Model;
                                    SupervisorArgument.Effort settings.Effort;
                                    SupervisorArgument.Native_Adapter "app-server";
                                    SupervisorArgument.Working_Directory settings.WorkingDirectory;
                                    SupervisorArgument.Sandbox "danger-full-access" ]
        let hubUrl = sprintf "http://127.0.0.1:%d" (freePort())
        // The existing M018 hub is Fodex's own order hub, not a COMM transport.
        let session = logicalSid,Some IPAddress.Loopback
        let! wrapper,loop,_,_,cleanup = WrapperCompositionV301.start node settings.ConnectionString session arguments ctx models Mcp hubUrl (fun _ -> ())
        let submit (text,callback: Result<string,exn> -> unit) =
            let edge = ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(0L,HumanPrompt text)
            match loop.PinEventRecorder(Some "3.0.0") edge with
            | Error error -> Error error
            | Ok saved ->
                let completed = function
                    | Ok (_nativeSid,finalText) -> callback(Ok finalText)
                    | Error error -> callback(Error error)
                wrapper.Send(box(WrapperOrder(text,ReActJsonV300.streamIdOf saved,CancellationToken.None,completed))) |> Result.map ignore
        let cancel () = task {
            let acknowledgement = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let callback (result: Result<string,exn>) =
                match result with
                | Ok _ -> acknowledgement.TrySetResult() |> ignore
                | Error error -> acknowledgement.TrySetException error |> ignore
            wrapper.Send(box(WrapperRequest("/cancel",callback))) |> required |> ignore
            return! acknowledgement.Task
        }
        let requester = LineDirectV301Core.createRequester submit cancel
        return
            { WrapperSessionId=logicalSid; RequestAsync=requester.RequestAsync; IsRetired=requester.IsRetired
              DisposeAsync=fun () -> task {
                  requester.Retire()
                  try do! cleanup()
                  finally node.Terminate().GetAwaiter().GetResult() |> ignore } :> Task }
    with error ->
        do! node.Terminate()
        return raise error
}
