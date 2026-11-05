#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module IAgentRequestFodexFunV401Tests
#endif

open System
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

let version = System.Version(4,0,1)
let wait (work:Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()

/// 只 evaluate 共用內建宣告，不以 fixture 取代 Request/Send quotation。
/// 舊版測試會自行設定 persistence；本組不變更它們的全域儲存設定。
let operation node =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtin declarations"
    let value = IAgentFodexFun<string,Task<Result<string,exn>>>(node,targetIAgentRequest)[version]
                |> Option.defaultWith(fun () -> failtest "IAgentRequest 4.0.1 is not declared")
    value.f.Value |> ignore
    value

/// 真正的 Request/Send operation 對 fixture actor 發訊息。
/// actor 只替代 command 執行，避免這些案例啟動 Codex/agy 或操作 production wrapper。
let withAgent timeout onRequest action =
    let config = ConfigurationFactory.ParseString(sprintf "fodex.agent.request-timeout=%dms\nakka.loglevel=OFF\nakka.stdout-loglevel=OFF" timeout)
    let node = ActorSystem.Create("request-test-"+Guid.NewGuid().ToString("N"),config)
    try
        let fn = operation node
        let received = Channel.CreateUnbounded<string>()
        let reference = spawn node "wrapper" <| fun (mailbox:Actor<obj>) ->
            let rec receive () = actor {
                let! message = mailbox.Receive()
                match message with
                | :? WrapperInputV300 as input ->
                    match input with
                    | WrapperRequest(text,reply) ->
                        received.Writer.TryWrite text |> ignore
                        onRequest text reply
                    | _ -> ()
                | _ -> ()
                return! receive () }
            receive ()
        let agent = IAgentFodexFunV200.create<string,obj> node (Agent.Wrapper reference) "--request 4.0.1" ctx
        action node agent fn received.Reader
    finally
        node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore

[<Tests>]
let tests = testList "IAgent Request V401" [
    testCase "eval Invoke and the public Request both use the registered Send path" <| fun () ->
        withAgent 5000 (fun text reply -> reply(Ok("reply:"+text))) <| fun node agent fn received ->
            let prompt = "/wrapper_append 第一行\n第二行"
            let direct = FeasibleFun.invoke (node,agent.Instance,prompt) fn |> required |> wait |> required
            Expect.equal direct ("reply:"+prompt) "quotation preserves the original command"
            Expect.equal (received.ReadAsync().AsTask() |> wait) prompt "Send reached the actor"
            Expect.equal (agent.Request "/wrapper_preview" |> wait |> required) "reply:/wrapper_preview" "public member hides reply plumbing"
            Expect.equal (received.ReadAsync().AsTask() |> wait) "/wrapper_preview" "member delivered once"

    testCase "an empty successful reply remains different from operation failure" <| fun () ->
        withAgent 5000 (fun _ reply -> reply(Ok "")) <| fun _ agent _ _ ->
            Expect.equal (agent.Request "/wrapper_preview" |> wait |> required) "" "valid empty draft"
        withAgent 5000 (fun _ reply -> reply(Error(InvalidOperationException "command rejected"))) <| fun _ agent _ _ ->
            match agent.Request "/wrapper_update(9) text" |> wait with
            | Error error -> Expect.equal error.Message "command rejected" "preserves the wrapper error"
            | Ok value -> failtestf "Expected Error, got %s" value

    testCase "timeout is bounded never resends and cannot be changed by a late reply" <| fun () ->
        let callback = TaskCompletionSource<Result<string,exn> -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        withAgent 100 (fun _ reply -> callback.TrySetResult reply |> ignore) <| fun _ agent _ received ->
            let response = agent.Request "/list_participant"
            match response |> wait with
            | Error (:? TimeoutException) -> ()
            | other -> failtestf "Expected timeout Error, got %A" other
            Expect.equal (received.ReadAsync().AsTask() |> wait) "/list_participant" "one delivery"
            (callback.Task |> wait)(Ok "late reply")
            Expect.isError (response |> wait) "late completion cannot turn timeout into success"
            Expect.isFalse (fst(received.TryRead())) "timeout did not retry the command"

    testCase "native handles reject wrapper commands without sending them" <| fun () ->
        withAgent 5000 (fun _ reply -> reply(Ok "unexpected")) <| fun node agent fn received ->
            let reference = match agent.Instance with Agent.Wrapper reference -> reference | _ -> failtest "fixture wrapper"
            match FeasibleFun.invoke (node,Agent.Native reference,"/wrapper_preview") fn with
            | Error (:? NotSupportedException) -> ()
            | other -> failtestf "Expected unsupported native Request, got %A" other
            Expect.isFalse (fst(received.TryRead())) "no wrapper command leaked to a native"

    testCase "Request quotation survives SQL serialization and runs with a later execution node" <| fun () ->
        withDatabase <| fun cs ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtin declarations"
            let baseRegistry = FodexFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                VersionRegistry<FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>>.BaseCD,targetIAgentRequest)
            let original = baseRegistry[version] |> Option.defaultWith(fun () -> failtest "missing Request quotation")
            QuotationStore.write cs [|targetIAgentRequest,version,Some original|] |> required
            let _,_,stored = QuotationStore.load<IAgentFunInput<string>,Task<Result<string,exn>>,exn> cs |> required |> Array.exactlyOne
            let restored = stored.Value
            restored.f.Value |> ignore
            withAgent 5000 (fun text reply -> reply(Ok text)) <| fun node agent _ _ ->
                let result = FeasibleFun.invoke (node,agent.Instance,"roundtrip") restored |> required |> wait |> required
                Expect.equal result "roundtrip" "no actor/node was captured in the stored Expr"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "IAgentRequestFodexFunV401Tests failed: %d" testExitCode
#endif
