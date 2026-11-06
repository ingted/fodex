#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module WrapperCompositionV302Tests
#endif
open System
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

// 歷史 V302 quotation 的回歸案例仍保留；已移除的 FodexEntry 不再是 Library 入口。
let createVersioned version input =
    try
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declare builtins"
        let _,node,_,_,_,_,_,_,_,_ = input
        FodexFunBinding.pin<WrapperCompositionInput,Task<WrapperCompositionOutput>> node version WrapperCompositionV302.target input
    with error -> Error error

[<Tests>]
let tests=testList "V302 wrapper entry quotation" [
    testCase "missing explicit version returns Error before creating resources" <| fun () -> withNode(fun node ->
        let result=createVersioned (Some "99.0.0")
                       (ignore,node,"must not connect",(Guid.NewGuid(),None),arguments,ctx,models,Mcp,"http://127.0.0.1:1",ignore)
        Expect.isError result "public entry respects version selection and Result boundary")
    testCase "single host entry builds through registry and exposes actor control without starting a Codex turn" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        use port=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0)
        port.Start()
        let url=sprintf "http://127.0.0.1:%d" ((port.LocalEndpoint :?> System.Net.IPEndPoint).Port)
        port.Stop()
        let wrapper,loop,hub,remote,cleanup=
            createVersioned (Some "3.0.2") (ignore,node,cs,session,arguments.Replace("app-server","exec"),ctx,models,Mcp,url,ignore) |> required |> wait
        try
            let accepted,finished,failed,_=Hub.status node hub |> required |> wait
            Expect.equal (accepted,finished,failed) (0,0,0) "creation does not dispatch work"
            let request=evaluated<RemoteCommandV300,Task<Result<string,exn>>> "3.0.0" node RemoteControlFodexFunV300.target
            let text=request(remote,"/list_participant",TimeSpan.FromSeconds 10.) |> required |> wait |> required
            Expect.stringContains text "main-wrapper" "control goes through wrapper IAgent"
            let graph=loop.ReadDataFlow session |> required
            Expect.isFalse (graph.GetEdges() |> Array.exists(function _,_,AgentWork(_,_,ParticipantInput _) -> true | _ -> false)) "startup never injects a native prompt"
            Expect.stringContains (wrapperCall wrapper MainHubList) "participants" "hub attached before returning"
        finally cleanup() |> wait))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "WrapperCompositionV302Tests failed"
#endif
