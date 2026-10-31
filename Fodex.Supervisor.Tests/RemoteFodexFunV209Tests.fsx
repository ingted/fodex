#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll"
#else
module RemoteFodexFunV209Tests
#endif

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

let withNodes action =
    let suffix = Guid.NewGuid().ToString("N")
    let owner = RemoteFodexFunV209.createNode ("remote-owner-"+suffix) "127.0.0.1" 0
    let client = RemoteFodexFunV209.createNode ("remote-client-"+suffix) "127.0.0.1" 0
    try action owner client
    finally
        client.Terminate().Wait(TimeSpan.FromSeconds 10.) |> ignore
        owner.Terminate().Wait(TimeSpan.FromSeconds 10.) |> ignore

let pin client =
    RemoteFodexFunV209.register client
    let operation = SubagentFodexFunV206.bind<RemoteResolveInputV209,IActorRef> client RemoteFodexFunV209.targetResolve (Some "2.0.9")
    operation.f.Value |> ignore
    operation

[<Tests>]
let tests = testList "Remote FodexFun V209" [
    testCase "Quotation rejects local/missing versions; actual TCP ref stays remote" <| fun () ->
        withNodes <| fun owner client ->
            let actorRef = spawn owner "probe" <| fun mailbox ->
                let rec loop () = actor { let! msg = mailbox.Receive()
                                          mailbox.Sender().Tell(msg,mailbox.Self)
                                          return! loop() }
                loop()
            let operation = pin client
            let ownerAddress = (owner :?> ExtendedActorSystem).Provider.DefaultAddress
            let address = actorRef.Path.ToStringWithAddress ownerAddress
            let remote = FeasibleFun.invoke (client,address) operation |> expectOk
            Expect.equal remote.Path.Address ownerAddress "actual remote ref"
            Expect.isFalse remote.Path.Address.HasLocalScope "not local fallback"
            Expect.isError (FeasibleFun.invoke (client,"akka://local/user/probe") operation) "local forbidden"
            Expect.isError (FeasibleFun.invoke (owner,address) operation) "same-node forbidden"
            Expect.throws (fun () -> SubagentFodexFunV206.bind<RemoteResolveInputV209,IActorRef> client RemoteFodexFunV209.targetResolve (Some "2.0.99") |> ignore) "pin enforced"
    testCase "Remote IAgent Send/GetSession/Acquire and remote ReActLoop typed history round-trip over TCP" <| fun () ->
        withDatabase <| fun cs ->
            withNodes <| fun owner client ->
                let directory = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V209")
                Directory.CreateDirectory directory |> ignore
                let ownerCtx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(directory,owner.Name+".jsonl")))), "test")
                let clientCtx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(directory,client.Name+".jsonl")))), "test")
                let received = ConcurrentQueue<string>()
                let normalOutput = ConcurrentQueue<ReActDataStream<string>>()
                let args = "--model gpt-6.1-sol --effort high --wrapperagent 2.0.8 --reactloophandler 2.0.3"
                let factory (loop: ReActLoop<string,obj>) participant nativeArgs =
                    let native = spawn owner "native-fixture" <| fun (mailbox: Actor<obj>) ->
                        let rec receive () = actor {
                            let! message = mailbox.Receive()
                            match message with
                            | :? NativeOwnershipCallV209 ->
                                mailbox.Sender().Tell(NativeOwnershipFodexFunV209.inspect owner mailbox.Self None (Some "owner-native-sid"))
                            | :? AgentCall as call ->
                                match call with
                                | StartAgent | StopAgent -> mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                                | ReadSessionId -> mailbox.Sender().Tell(Ok(NativeCodexSessionId "owner-native-sid") : Result<AgentSessionId,exn>)
                                | _ -> ()
                            | :? AgentInput as input ->
                                match input with
                                | GetCapabilities respond -> respond(Ok [|CanStartTurn|])
                                | RunTurn(_,text,cause,_) ->
                                    received.Enqueue(string text)
                                    loop.RouteReActDataStream(endpoint participant,endpoint wrapper,AgentWork(0L,cause,MessageCompleted("remote-message",string text))) |> expectOk |> ignore
                                    loop.RouteReActDataStream(endpoint participant,endpoint wrapper,AgentEnd(0L,cause,Message(string text))) |> expectOk |> ignore
                                | _ -> ()
                            | _ -> ()
                            return! receive() }
                        receive()
                    Ok(IAgentFodexFunV200.create<string,obj> owner (Native native) nativeArgs ownerCtx)
                FodexFunRegistrationV208.registerFunctions owner |> expectOk
                FodexFunRegistrationV208.registerObjects owner cs session args ownerCtx normalOutput.Enqueue factory |> expectOk
                let ownerWrapper = FodexAgent<string,obj>.createWrapper(owner,args,ownerCtx) |> expectOk
                let ownerLoop = FodexAgent<string,obj>.createReActLoop(owner,args,ownerCtx) |> expectOk
                ownerWrapper.Start() |> expectOk |> ignore
                try
                    // client 只註冊 functions + 遠端 refs，沒有 registerObjects、native factory、process。
                    FodexFunRegistrationV208.registerFunctions client |> expectOk
                    NativeOwnershipFodexFunV209.register client
                    let operation = pin client
                    let ownerAddress = (owner :?> ExtendedActorSystem).Provider.DefaultAddress
                    let wrapperAddress = (IAgentFodexFunV200.actorRef ownerWrapper.Instance).Path.ToStringWithAddress ownerAddress
                    let handlerAddress = ownerLoop.ReActLoopHandler.Path.ToStringWithAddress ownerAddress
                    let wrapperRef = FeasibleFun.invoke (client,wrapperAddress) operation |> expectOk
                    let handlerRef = FeasibleFun.invoke (client,handlerAddress) operation |> expectOk
                    Versioned<Agent>(client,targetWrapperAgent).Register(System.Version(2,0,8),Wrapper wrapperRef)
                    Versioned<IActorRef>(client,targetReActLoopHandler).Register(System.Version(2,0,3),handlerRef)
                    let remoteWrapper = FodexAgent<string,obj>.createWrapper(client,args,clientCtx) |> expectOk
                    let remoteLoop = FodexAgent<string,obj>.createReActLoop(client,args,clientCtx) |> expectOk
                    Expect.equal (remoteWrapper.GetSessionId() |> expectOk) (WrapperSessionId session) "actual remote request/reply"
                    let native = remoteWrapper.AcquireParticipant(main,args) |> expectOk
                    Expect.equal (native.GetSessionId() |> expectOk) (NativeCodexSessionId "owner-native-sid") "child ref survives serialization"
                    Expect.equal (IAgentFodexFunV200.actorRef native.Instance).Path.Address ownerAddress "child still belongs to owner"
                    match native.PinNativeOwnership(client,Some "2.0.9")() |> expectOk with
                    | NativeOwnership(address,path,pid,_,childPid,sid) ->
                        Expect.equal address (ownerAddress.ToString()) "resource proof comes from owner"
                        Expect.stringContains path owner.Name "native owner path"
                        Expect.equal pid Environment.ProcessId "two-node fixture shares one real process"
                        Expect.equal (childPid,sid) (None,Some "owner-native-sid") "fixture does not pretend to launch Codex"
                    remoteWrapper.Send(box "REMOTE_ECHO") |> expectOk |> ignore
                    Expect.isTrue (SpinWait.SpinUntil((fun () -> normalOutput.ToArray() |> Array.exists (fun (_,_,e) -> match e with AgentEnd _ -> true | _ -> false)),TimeSpan.FromSeconds 10.)) "owner completed actual work"
                    Expect.equal (received.ToArray()) [|"REMOTE_ECHO"|] "owner received exactly once"
                    let history = remoteLoop.Collect session |> expectOk
                    let graph = remoteLoop.ReadDataFlow session |> expectOk
                    Expect.equal (graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray) history "typed FGL/history roundtrip"
                    let userOutput = history |> Array.filter (fun (_,target,_) -> target=ReActEndpoint.Human)
                    Expect.equal userOutput (normalOutput.ToArray()) "normal owner sink matches remote read"
                finally ownerWrapper.Stop() |> expectOk |> ignore
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "RemoteFodexFunV209Tests: %d" code
#endif
