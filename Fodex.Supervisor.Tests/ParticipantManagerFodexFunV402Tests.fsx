#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ParticipantManagerFodexFunV402Tests
#endif

open System
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let withManager action = withDatabase(fun cs -> withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations"
    let session = Guid.NewGuid(),None
    let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.2" node FodexFunRegistrationV301.targetLoop
                    (node,cs,session,ctx,ignore) |> required
    let hub = Hub.create node (node,ctx,Map.empty) |> required
    let arguments = "--native-adapter exec --model test --effort high --sub-codex-model test --sub-agy-model test"
    let manager = evaluated<ParticipantManagerInputV402,IActorRef> "4.0.2" node "Participant.Manager.Create"
                    (node,loop,cs,session,arguments,ctx,models,ShellClient("test.dll","http://127.0.0.1:1"),hub) |> required
    evaluated<ReActLoopFunInput<IActorRef * IActorRef>,Task<Result<unit,exn>>> "4.0.2" node ParticipantOperationsFodexFunV402.targetInstall
        (node,loop.ReActLoopHandler,(manager,hub)) |> required |> wait |> required
    let wrapper = evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.2" node FodexFunRegistrationV300.targetWrapper
                    (node,loop,cs,ctx,models,session,(1L,session),arguments,ShellClient("test.dll","http://127.0.0.1:1")) |> required
    loop.UpsertParticipant((1L,session),wrapper) |> required
    wrapper.Start() |> required |> ignore
    try action node session loop hub
    finally wrapper.Stop() |> required |> ignore))

let info (loop:ReActLoop<string,obj>) session participant =
    match (loop.ReadDataFlow session |> required).GetLabel(ReActEndpoint.Participant participant) with
    | ReActNode.Participant value -> value
    | _ -> failtest "missing graph metadata"

let requireAll work =
    work |> wait |> required |> Array.iter(fun (_,result) -> result |> required)

[<Tests>]
let tests = testSequenced <| testList "Participant manager V402" [
    testCase "four provider/mode batches coexist and stop/start retain their who and identities" <| fun () ->
        withManager <| fun node session loop hub ->
            let root = 1L,session
            let children = [|
                for kind in [ReActParticipantKind.Wrapper;ReActParticipantKind.RawExec] do
                    for adapter in ["agy";"exec"] do
                        yield! loop.CreateParticipants(root,1,kind,"--native-adapter "+adapter+" --model test --effort high") |> wait |> required
            |]
            Expect.equal children.Length 4 "four batches are additive"
            Expect.equal (children |> Array.distinct |> Array.length) 4 "no duplicate logical participant"
            let before = children |> Array.map(fun p -> p,(info loop session p).Name)
            let _,_,_,ready = Hub.status node hub |> required |> wait
            Expect.equal ready.Length 4 "all four may receive work"
            loop.ControlParticipants(root,StopParticipant,children) |> requireAll
            let _,_,_,stopped = Hub.status node hub |> required |> wait
            Expect.isEmpty stopped "stopped participants are not dispatchable"
            loop.ControlParticipants(root,StartParticipant,children) |> requireAll
            for participant,name in before do
                let current = info loop session participant
                Expect.equal current.Name name "Start does not invent another who"
                Expect.equal current.Status ReActParticipantStatus.Idle "same identity ready"
            let _,_,_,readyAgain = Hub.status node hub |> required |> wait
            Expect.equal readyAgain.Length 4 "all restored"

    testCase "retirement preserves history and never recycles a participant number" <| fun () ->
        withManager <| fun _ session loop _ ->
            let root = 1L,session
            let first = loop.CreateParticipants(root,1,ReActParticipantKind.RawExec,"--native-adapter agy") |> wait |> required |> Array.exactlyOne
            loop.ControlParticipants(root,RetireParticipant,[|first;first|]) |> requireAll
            Expect.equal (info loop session first).Status ReActParticipantStatus.Retired "history still has the retired identity"
            let restart = loop.ControlParticipants(root,StartParticipant,[|first|]) |> wait |> required
            Expect.isError (snd restart[0]) "removed identities cannot silently revive"
            let next = loop.CreateParticipants(root,1,ReActParticipantKind.RawExec,"--native-adapter exec") |> wait |> required |> Array.exactlyOne
            Expect.isGreaterThan (fst next) (fst first) "monotonic N across retire/create"

    testCase "invalid provider and invalid owner cause no resource allocation" <| fun () ->
        withManager <| fun _ session loop _ ->
            let root = 1L,session
            let before = loop.ListParticipants session |> required
            Expect.isError (loop.CreateParticipants(root,1,ReActParticipantKind.RawExec,"--native-adapter unknown") |> wait) "provider fails before reserve"
            Expect.isError (loop.CreateParticipants((999L,session),1,ReActParticipantKind.RawExec,"--native-adapter agy") |> wait) "owner must belong to the graph"
            Expect.equal (loop.ListParticipants session |> required) before "no leaked identities for rejected requests"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ParticipantManagerFodexFunV402Tests failed: %d" testExitCode
#endif
