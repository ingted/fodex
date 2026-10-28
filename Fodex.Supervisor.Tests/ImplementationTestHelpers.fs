namespace Fodex.Supervisor.Tests

open System
open System.IO
open Akka.Actor
open Expecto
open FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type RecordingActorRef() =
    inherit ActorRefBase()

    let messages = ResizeArray<obj>()

    override _.Path = ActorRefs.Nobody.Path

    override _.TellInternal(message: obj, _sender: IActorRef) =
        messages.Add message

    member _.Messages = messages.ToArray()

module ImplementationTestHelpers =
    let storeRoot =
        Path.Combine(
            Path.GetTempPath(),
            "Fodex.Supervisor.Tests",
            Guid.NewGuid().ToString("N")
        )
        |> Path.GetFullPath

    let configureStoreRoot () =
        Environment.SetEnvironmentVariable("FODEX_REACT_STORE_ROOT", storeRoot)

    let context =
        let logger =
            {
                new UniLogger() with
                    member _.Info _ = ()
                    member _.Debug _ = ()
                    member _.Warn _ = ()
                    member _.Error _ = ()
                    member _.infof format = Printf.kprintf ignore format
                    member _.debugf format = Printf.kprintf ignore format
                    member _.warnf format = Printf.kprintf ignore format
                    member _.errorf format = Printf.kprintf ignore format
                    member _.pInfof format = Printf.kprintf ignore format
                    member _.pDebugf format = Printf.kprintf ignore format
                    member _.pWarnf format = Printf.kprintf ignore format
                    member _.pErrorf format = Printf.kprintf ignore format
                    member _.fInfof format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.fDebugf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.fWarnf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.fErrorf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.pfErrorf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
            }

        {
            logger = logger
            usage = [ "Fodex.Supervisor.Tests" ]
            caller = []
        }

    let expectOk label (result: Result<'Value, exn>) =
        match result with
        | Ok value -> value
        | Error error -> failtestf "%s returned Error: %s" label error.Message

    let runQuotation
        (input: 'Input)
        (quotation: Expr<'Input -> FeasibleT<'Input, 'Success, exn>>)
        : FeasibleFun<'Input, 'Success, exn>
            * Result<'Success, exn> =
        let feasibleFun = FeasibleFun<'Input, 'Success, exn>(quotation)
        Expect.isFalse feasibleFun.f.IsValueCreated
            "quotation must remain lazy until the FodexFun is invoked"
        let result = FeasibleFun.invoke input feasibleFun
        Expect.isTrue feasibleFun.f.IsValueCreated
            "invocation must evaluate the quotation"
        feasibleFun, result

    let invokeQuotation label input quotation =
        runQuotation input quotation
        |> snd
        |> expectOk label

    let withNode label action =
        let node =
            ActorSystem.Create(
                sprintf
                    "fodex-%s-%s"
                    label
                    (Guid.NewGuid().ToString("N"))
            )

        try
            action node
        finally
            node.Terminate().GetAwaiter().GetResult()
            |> ignore

    let createAgent
        (node: FodexNode)
        (instance: Agent)
        (sentMessages: ResizeArray<string>)
        : IAgent<string, string> =
        {
            new IAgent<string, string>(node, instance, "", context) with
                member _.Send =
                    fun message ->
                        sentMessages.Add message
                        Ok 1

                member _.AcquireParticipant =
                    fun _ ->
                        Error (
                            InvalidOperationException(
                                "test agent does not acquire participants"
                            ) :> exn
                        )
        }

    let removeStoreRoot () =
        let allowedParent =
            Path.Combine(Path.GetTempPath(), "Fodex.Supervisor.Tests")
            |> Path.GetFullPath

        let fullRoot = Path.GetFullPath storeRoot
        let allowedPrefix =
            allowedParent.TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        if
            fullRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists fullRoot
        then
            Directory.Delete(fullRoot, true)

