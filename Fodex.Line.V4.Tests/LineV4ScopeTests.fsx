#if INTERACTIVE
#load "LineV4TestSupport.fsx"
#else
module LineV4ScopeTests
#endif
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Data.SqlClient
open LineV4StoreAsync
open LineV4OrderedOutput
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4Transport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module RN=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

open LineV4TestSupport

[<Tests>]
let tests=testSequenced <| testList "LINE v4 scope" [
    testCase "V01 group, user and room use authoritative distinct conversation keys" <| fun () ->
        let hashes=[|for source in ["group";"room";"user"] -> RN.conversationKeyHash "test" source "same"|]
        Expect.equal (Array.distinct hashes).Length 3 "source type is part of authority"
    testCase "V04 V05 unauthorized or absent stable user id saves terminal Ignore with no roots or work" <| fun () -> withDatabase(fun cs ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        let admission=Admission(store,settings cs,Guid.NewGuid(),"authorized-id")
        for sequence,sender in [1L,Some "NorthPole";2L,None;3L,Some "other-id"] do
            let h,i,raw=eventRequest store "group-a" sender sequence "**newagent codex \"must not exist\""
            let receipt=match admission.AcceptAsync(h,i,"test",raw,ct) |> wait with G.AdmissionReply.Admitted value -> value | value -> failtestf "Expected admitted Ignore: %A" value
            let read:G.ResultRead={RequestOriginRef=G.OriginRef.Receipt receipt.ReceiptId;BindingDigest=Some receipt.BindingDigest}
            let result=store.ReadResultAsync(h,read,ct) |> wait
            match result with
            | G.ResultReply.Ready value ->
                Expect.equal value.Output.Disposition (G.ResultDisposition.Ignore "sender-not-authorized") "silent decision"
                Expect.equal value.Output.Outcome G.WorkOutcome.Succeeded "authorization policy completed"
                Expect.equal value.Output.ByteLength 0L "no reply text"
                Expect.isNone value.Content "Ignore has no InlineMessages"
                Expect.isNone value.Output.DisplayScope.Root "no root authority"
            | value -> failtestf "Expected ready Ignore: %A" value
            Expect.equal (admission.AcceptAsync(h,i,"test",raw,ct) |> wait) (G.AdmissionReply.AlreadyAdmitted receipt) "idempotent receipt"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions" |> Convert.ToInt32) 0 "no wrappers"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3OrderedAdmissions WHERE State<>'Terminal'" |> Convert.ToInt32) 0 "no queued worker"
        let h,i,raw=eventRequest store "group-a" (Some "authorized-id") 4L "/next"
        let receipt=match admission.AcceptAsync(h,i,"test",raw,ct) |> wait with G.AdmissionReply.Admitted value -> value | other -> failtestf "%A" other
        match store.ReadResultAsync(h,{RequestOriginRef=G.OriginRef.Receipt receipt.ReceiptId;BindingDigest=Some receipt.BindingDigest},ct) |> wait with
        | G.ResultReply.Ready value ->
            match value.Content with
            | Some(G.OutputContent.InlineMessages body) -> Expect.isFalse (body.Contains "未授權") "later authorized next cannot recover an unauthorized refusal message"
            | _ -> failtest "authorized next should return the normal empty-result notice"
        | other -> failtestf "%A" other)
    testCase "V02 equal main topics in different groups keep separate wrappers and selections" <| fun () -> withDatabase(fun cs ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        let admission=Admission(store,settings cs,Guid.NewGuid(),"authorized-id")
        for group in ["group-a";"group-b"] do
            let h,i,raw=eventRequest store group (Some "authorized-id") 1L "**whoami"
            match admission.AcceptAsync(h,i,"test",raw,ct) |> wait with G.AdmissionReply.Admitted _ -> () | value -> failtestf "%A" value
        Expect.equal (scalar cs "SELECT COUNT(DISTINCT WrapperId) FROM dbo.LineFodexV3Sessions" |> Convert.ToInt32) 2 "one main per group"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3Selections" |> Convert.ToInt32) 2 "selections remain conversation scoped")
    testCase "V03 another group's SID and prefix cannot select or target its wrapper" <| fun () -> withDatabase(fun cs ->
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        store.InitializeAsync ct |> finish
        let admission=Admission(store,settings cs,Guid.NewGuid(),"authorized-id")
        for group in ["group-a";"group-b"] do
            let h,i,raw=eventRequest store group (Some "authorized-id") 1L "**whoami"
            admission.AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore
        let roster group=store.BaseStore.ListRootsAsync(LineV4SharedCore.conversationReference("test|group|"+group),ct) |> wait |> Array.exactlyOne
        let foreign=roster "group-b"
        let local=roster "group-a"
        scalar cs "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Ready';SELECT 1" |> ignore
        for sequence,command in [2L,"**setagent "+foreign.Mapping.WrapperId.ToString("D");3L,"**["+foreign.Mapping.WrapperId.ToString("N").Substring(0,8)+"] foreign prompt"] do
            let h,i,raw=eventRequest store "group-a" (Some "authorized-id") sequence command
            match admission.AcceptAsync(h,i,"test",raw,ct) |> wait with
            | G.AdmissionReply.Rejected _ -> ()
            | other -> failtestf "Cross-group selector must reject: %A" other
        Expect.equal (roster "group-a").Mapping.WrapperId local.Mapping.WrapperId "local WSID remains stable"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3OrderedAdmissions WHERE State<>'Terminal'" |> Convert.ToInt32) 0 "rejected input creates no model work")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4ScopeTests failed"
#endif
