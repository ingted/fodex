// Isolated real SQL fixtures; supplied completed content is not native/LINE E2E evidence.
#if INTERACTIVE
#load "LineV4DeliveryTestSupport.fsx"
#else
module LineV4DeliverySqlTests
#endif
open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Expecto
open LineV4StoreAsync
open LineV4StoreContract
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4OrderedOutput
open LineV4CliTranscript
open LineV4TestSupport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module RN=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

open LineV4DeliveryTestSupport

[<Tests>]
let tests=testSequenced <| testList "LINE v4 CLI delivery SQL" [
    testCase "V23 V27 V28 next claims immutable pages, retries reuse output, and only Accepted delivers" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,admission=setup cs
        // Deliver the initial whoami query through its real carrier before testing CLI pending.
        let h0,r0=next store admission 2L
        acknowledge store h0 r0 G.SubjectState.Accepted 1L
        let expected=[|for n in 1..7 -> sprintf "CLI %d 中文🙂\n完整內容" n|]
        let _,_=addCompleted node cs store expected
        let h,first=next store admission 3L
        Expect.equal (texts first) expected[0..3] "bounded first page preserves exact text"
        let _,retry=next store admission 4L
        Expect.equal retry.Output.OutputRef first.Output.OutputRef "no duplicate immutable output"
        acknowledge store h first G.SubjectState.Unknown 1L
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV4CliReplyItems WHERE DeliverState='Unknown'" |> Convert.ToInt32) 4 "unknown retains original claim"
        acknowledge store h first G.SubjectState.Accepted 2L
        let h2,second=next store admission 5L
        Expect.equal (texts second) expected[4..] "excess remains pending without truncation"
        acknowledge store h2 second G.SubjectState.Accepted 1L
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV4CliReplyItems WHERE DeliverState='Delivered'" |> Convert.ToInt32) 7 "only accepted pages delivered"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3OrderedAdmissions WHERE State<>'Terminal'" |> Convert.ToInt32) 0 "next creates no native work"
        Expect.equal (scalar cs "SELECT CASE WHEN OBJECT_ID('dbo.FodexServiceOperationV1') IS NULL THEN 0 ELSE 1 END" |> Convert.ToInt32) 0 "no service/model operation"))
    testCase "V24 V26 normal output and CLI claim roll back together; replay preserves bytes" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,admission=setup cs
        let _,_=addCompleted node cs store [|"[CLI] earlier 中文🙂"|]
        let h,i,raw=eventRequest store "group-a" (Some "authorized-id") 2L "normal LINE prompt"
        admission.AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore
        let saved=store.UseTransactionAsync(ct,fun db -> findAdmission db ct h) |> wait |> Option.get
        let binding=saved.Binding |> Option.get
        let reference:G.ResultRef={GeneratorProviderId="fodex-line-v3";StorageIdentity=store.StorageIdentity;OriginRef=saved.Origin;OutputOrdinal=0;ResultVersion=1L}
        let normal=output reference binding.DisplayScope G.WorkOutcome.Succeeded G.ResultDisposition.Reply (textPages G.defaultLimits "current LINE answer" |> Array.head)
        Expect.throws (fun () -> store.CompleteAsync(saved,[|normal|],(fun _ -> Task.FromException<unit>(InvalidOperationException "injected before commit")),ct) |> finish) "injected rollback"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV4CliReplyItems WHERE ClaimedResultKey IS NULL" |> Convert.ToInt32) 1 "claim rolled back"
        store.CompleteAsync(saved,[|normal|],(fun _ -> Task.FromResult()),ct) |> finish
        let read:G.ResultRead={RequestOriginRef=saved.Origin;BindingDigest=Some(G.bindingDigest binding |> required)}
        let value=match store.ReadResultAsync(h,read,ct) |> wait with G.ResultReply.Ready value -> value | other -> failtestf "%A" other
        Expect.equal (texts value) [|"[CLI] earlier 中文🙂";"current LINE answer"|] "normal answer retains space"
        store.CompleteAsync(saved,[|normal|],(fun _ -> Task.FromResult()),ct) |> finish
        Expect.equal (store.ReadResultAsync(h,read,ct) |> wait) (G.ResultReply.Ready value) "same output bytes on C retry"))
    testCase "V34 operation id cannot change input or frozen target" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,_=setup cs
        let transcript,turn=addCompleted node cs store [|"fixture completed"|]
        transcript.PrepareAsync(turn,ct) |> finish
        Expect.throws (fun () -> transcript.PrepareAsync({turn with InputText="different"},ct) |> finish) "same id different body"
        Expect.throws (fun () -> transcript.PrepareAsync({turn with Generation=turn.Generation+1L},ct) |> finish) "same id different generation"))
    testCase "V25 concurrent normal completion and next claim each CLI fragment once across independent stores" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let first,admission=setup cs
        let h0,r0=next first admission 2L
        acknowledge first h0 r0 G.SubjectState.Accepted 1L
        let _,_=addCompleted node cs first [|"concurrent-cli-one";"concurrent-cli-two"|]
        let h,i,raw=eventRequest first "group-a" (Some "authorized-id") 3L "ordinary LINE input"
        admission.AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore
        let saved=first.UseTransactionAsync(ct,fun db -> findAdmission db ct h) |> wait |> Option.get
        let binding=saved.Binding |> Option.get
        let reference:G.ResultRef={GeneratorProviderId="fodex-line-v3";StorageIdentity=first.StorageIdentity;OriginRef=saved.Origin;OutputOrdinal=0;ResultVersion=1L}
        let normal=output reference binding.DisplayScope G.WorkOutcome.Succeeded G.ResultDisposition.Reply (textPages G.defaultLimits "normal-completion" |> Array.head)
        let second=OrderedStore(AsyncStore cs,"fodex-line-v3")
        second.InitializeAsync ct |> finish
        let nextAdmission=Admission(second,settings cs,Guid.NewGuid(),"authorized-id")
        let completing=Task.Run(Func<Task>(fun () -> first.CompleteAsync(saved,[|normal|],(fun _ -> Task.FromResult()),ct) :> Task))
        let collecting=Task.Run(Func<G.Header*G.ReadyResult>(fun () -> next second nextAdmission 4L))
        Task.WhenAll([|completing :> Task;collecting :> Task|]) |> finish
        let bodies=first.UseTransactionAsync(ct,fun db -> Sql.rows db ct "SELECT Content FROM dbo.LineFodexV3OrderedOutputs" [] (fun r -> G.strictUtf8.GetString(r.GetFieldValue<byte[]>(0)))) |> wait
        for text in ["concurrent-cli-one";"concurrent-cli-two"] do
            Expect.equal (bodies |> Array.sumBy(fun body ->
                use json=JsonDocument.Parse body
                json.RootElement.EnumerateArray() |> Seq.filter(fun item -> item.GetProperty("text").GetString()=text) |> Seq.length)) 1 "one persisted output contains each CLI fragment"
        Expect.equal (scalar cs "SELECT COUNT(DISTINCT ClaimedResultKey) FROM dbo.LineFodexV4CliReplyItems" |> Convert.ToInt32) 1 "both fragments bind to one winning immutable output"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV4CliReplyItems WHERE DeliverState='Delivered'" |> Convert.ToInt32) 0 "neither SQL completion nor console marks delivery"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4DeliverySqlTests failed"
#endif
