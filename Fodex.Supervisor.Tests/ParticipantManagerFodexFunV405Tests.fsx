#if INTERACTIVE
#load "NativeOwnerTestSupportV409.fsx"
#else
module ParticipantManagerFodexFunV405Tests
#endif
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
open NativeOwnerTestSupportV409

let createRawWithAdapter adapter (node:FodexNode) (loop:ReActLoop<string,obj>) session owner=
    let created=loop.CreateParticipants(owner,1,ReActParticipantKind.RawExec,"--native-adapter "+adapter+" --model known --effort high") |> wait |> required
    let participant=Array.exactlyOne created
    let info=match (loop.ReadDataFlow session |> required).GetLabel(ReActEndpoint.Participant participant) with ReActNode.Participant info -> info | _ -> failtest "raw metadata missing"
    let actor=node.ActorSelection(info.ActorAddress |> Option.get).ResolveOne(TimeSpan.FromSeconds 5.) |> wait
    participant,actor
let createRaw node loop session owner=createRawWithAdapter "exec" node loop session owner
let order (actor:IActorRef) owner text=
    let completed=TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    actor.Tell(RawOrder(owner,Guid.NewGuid(),text,CancellationToken.None,completed),ActorRefs.NoSender)
    completed.Task.WaitAsync(TimeSpan.FromSeconds 15.)

[<Tests>]
let tests=testSequenced <| testList "Participant manager model V405" [
    testCase "raw model control changes only selected raw leaf and preserves its native SID" <| fun () -> withOwner(fun node _ loop session owner _ _ folder _ ->
        let participant,actor=createRaw node loop session owner
        let sid,text=order actor owner "first raw order" |> wait |> required
        Expect.equal text "FIXTURE_OK" "actual raw exec terminal"
        let changed=loop.SetNativeModel(participant,"known","low") |> required |> wait |> required
        Expect.equal changed.Number (fst participant) "raw target, not main or N=2"
        let sameSid,next=order actor owner "second raw order" |> wait |> required
        Expect.equal (sameSid,next) (sid,"FIXTURE_OK") "resume retains actual raw native SID"
        let mainAgent=loop.ResolveParticipant(main loop session) |> required
        Expect.equal ((settings mainAgent |> fst).Effort) "high" "main settings remain independent"
        use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        Expect.equal (receipt.RootElement.GetProperty("resumeSid").GetString()) sid "actual CLI argv resumes raw SID"
        Expect.stringContains (receipt.RootElement.GetProperty("argv").GetRawText()) "low" "new effort reaches actual exec launch")
    testCase "active raw model change preserves original order completion and resumes its SID" <| fun () -> withOwner(fun node _ loop session owner _ _ folder _ ->
        let participant,actor=createRaw node loop session owner
        let sid,_=order actor owner "learn raw SID" |> wait |> required
        File.Delete(Path.Combine(folder,"started.ready"))
        let original=order actor owner "WAIT"
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        loop.SetNativeModel(participant,"known","low") |> required |> wait |> required |> ignore
        Expect.equal (original |> wait |> required) (sid,"FIXTURE_OK") "cancel-for-model does not cancel the hub caller's original order"
        use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        Expect.equal (receipt.RootElement.GetProperty("resumeSid").GetString()) sid "continuation is not a fork")
    testCase "raw AGY rejects first no-SID update then preserves known SID and original order on update" <| fun () -> withOwner(fun node _ loop session owner _ _ folder _ ->
        let participant,actor=createRawWithAdapter "agy" node loop session owner
        let first=order actor owner "WAIT"
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        Expect.isError (loop.SetNativeModel(participant,"known","low") |> required |> wait) "first raw AGY is preserved until actual terminal SID"
        Expect.isFalse first.IsCompleted "not silently cancelled or completed"
        File.WriteAllText(Path.Combine(folder,"release"),"",Text.UTF8Encoding(false))
        let sid,_=first |> wait |> required
        File.Delete(Path.Combine(folder,"release"))
        File.Delete(Path.Combine(folder,"started.ready"))
        let original=order actor owner "WAIT"
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        loop.SetNativeModel(participant,"known","low") |> required |> wait |> required |> ignore
        Expect.equal (original |> wait |> required) (sid,"FIXTURE_OK") "known-SID raw AGY retains original completion"
        use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        Expect.equal (receipt.RootElement.GetProperty("resumeSid").GetString()) sid "actual AGY CLI resume SID"
        Expect.equal (receipt.RootElement.GetProperty("effort").GetString()) "low" "new effort used by resumed process")
    testCase "raw Stop cancels in-flight catalog validation before applying any late settings" <| fun () -> withOwner(fun node _ loop session owner _ _ folder _ ->
        let participant,actor=createRaw node loop session owner
        let held=TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        heldCatalogs[node.Name]<-held;enteredCatalogs[node.Name]<-entered
        let changing=loop.SetNativeModel(participant,"known","low") |> required
        entered.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit
        let stopped=loop.ControlParticipants(owner,StopParticipant,[|participant|]).WaitAsync(TimeSpan.FromSeconds 10.) |> wait |> required
        Expect.isTrue (stopped |> Array.forall(fun (_,result) -> Result.isOk result)) "same owner gate completes Stop after cancelled validation"
        Expect.isError (changing.WaitAsync(TimeSpan.FromSeconds 5.) |> wait) "cancelled proposal was refused"
        held.TrySetResult(Ok(freshCatalog())) |> ignore
        let observed=TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        actor.Tell(ReadNativeSettings(fun result -> observed.TrySetResult result |> ignore),ActorRefs.NoSender)
        let settings,active=observed.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required
        Expect.equal settings.Effort "high" "late catalog completion cannot apply old control"
        Expect.isFalse active "raw remains stopped"
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "control did not create a native session")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantManagerFodexFunV405Tests failed"
#endif
