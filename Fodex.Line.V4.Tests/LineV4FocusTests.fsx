#if INTERACTIVE
#load "LineV4TestSupport.fsx"
#else
module LineV4FocusTests
#endif
open System
open Expecto
open LineV4Focus
open LineV4StoreAsync
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4TestSupport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

let reference group=LineV4SharedCore.conversationReference("test|group|"+group)
let admitWhoami cs (store:OrderedStore) group sequence=
    let h,i,raw=eventRequest store group (Some "authorized-id") sequence "**whoami"
    let admission=Admission(store,settings cs,Guid.NewGuid(),"authorized-id")
    admission.AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore

[<Tests>]
let tests=testSequenced <| testList "LINE v4 focus" [
    testCase "V06 V07 startup restores valid choice and does not guess among groups" <| fun () -> withDatabase(fun cs ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        for group in ["a";"b"] do admitWhoami cs store group 1L
        let focus=FocusStore(cs,instanceScope "test" store.StorageIdentity+"/new-instance")
        focus.RestoreAsync([|reference "a";reference "b"|],ct) |> finish
        Expect.isNone (focus.FreezeAsync ct |> wait) "no global latest-group guess"
        focus.RestoreAsync([|reference "a"|],ct) |> finish
        let first=focus.FreezeAsync ct |> wait |> Option.get
        Expect.equal first.Conversation (reference "a") "unique roster fallback"
        let restarted=FocusStore(cs,instanceScope "test" store.StorageIdentity+"/new-instance")
        restarted.RestoreAsync([|reference "a";reference "b"|],ct) |> finish
        Expect.equal (restarted.FreezeAsync ct |> wait |> Option.get).WrapperId first.WrapperId "persisted choice has priority"
        restarted.SelectAsync(reference "b",ct) |> finish
        let next=restarted.FreezeAsync ct |> wait |> Option.get
        Expect.notEqual next.WrapperId first.WrapperId "explicit conversation switch"
        Expect.equal first.Conversation (reference "a") "earlier submission snapshot is immutable")
    testCase "V08 V10 ordinary group traffic preserves focus and explicit setagent changes it after commit" <| fun () -> withDatabase(fun cs ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        admitWhoami cs store "a" 1L
        let focus=FocusStore(cs,instanceScope "test" store.StorageIdentity)
        let original=focus.FreezeAsync ct |> wait |> Option.get
        admitWhoami cs store "b" 1L
        Expect.equal (focus.FreezeAsync ct |> wait |> Option.get).WrapperId original.WrapperId "ordinary traffic does not steal focus"
        scalar cs "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Ready';SELECT 1" |> ignore
        let h,i,raw=eventRequest store "b" (Some "authorized-id") 2L "**setagent main"
        Admission(store,settings cs,Guid.NewGuid(),"authorized-id").AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore
        Expect.equal (focus.FreezeAsync ct |> wait |> Option.get).Conversation (reference "b") "selection/focus commit together")
    testCase "ambiguous existing roots fail without silently creating a replacement" <| fun () -> withDatabase(fun cs ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        admitWhoami cs store "a" 1L
        scalar cs "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='MigrationAmbiguous';SELECT 1" |> ignore
        let h,i,raw=eventRequest store "a" (Some "authorized-id") 2L "**whoami"
        match Admission(store,settings cs,Guid.NewGuid(),"authorized-id").AcceptAsync(h,i,"test",raw,ct) |> wait with
        | G.AdmissionReply.Rejected rejection -> Expect.stringContains rejection.DisplayError "no valid default" "specific business diagnostic"
        | other -> failtestf "Expected a durable migration rejection: %A" other
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions" |> Convert.ToInt32) 1 "no replacement WSID")
    testCase "focus actor snapshots stay immutable when a later conversation is selected" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        for group in ["a";"b"] do admitWhoami cs store group 1L
        let persisted=FocusStore(cs,instanceScope "test" store.StorageIdentity)
        let actor=LineV4Focus.start node persisted
        persisted.SelectAsync(reference "a",ct) |> finish
        let first=LineV4Focus.freezeAsync actor ct |> wait |> Option.get
        persisted.SelectAsync(reference "b",ct) |> finish
        let second=LineV4Focus.freezeAsync actor ct |> wait |> Option.get
        Expect.equal first.Conversation (reference "a") "accepted target is a value, never a mutable focus pointer"
        Expect.equal second.Conversation (reference "b") "subsequent submission uses committed focus"
        Expect.isGreaterThan second.FocusRevision first.FocusRevision "durable revision advances"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4FocusTests failed"
#endif
