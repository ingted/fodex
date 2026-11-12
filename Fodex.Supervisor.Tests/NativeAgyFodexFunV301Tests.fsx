#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "nuget: FAkka.Proc.Supervisor, 1.572.12"
#else
module NativeAgyFodexFunV301Tests
#endif

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Akka.Proc.Supervisor
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

/// 只替代程序邊界，不替代 agy actor、ReActLoop、SQL 或成功/失敗判斷。
/// node 是 quotation 的明確 input；測試資料不捕捉到 quotation，也不跨 node 共用。
let launches = ConcurrentDictionary<ActorSystem,Channel<string list * ProcLocalIo>>()

let startFixture ((node,executable,directory,args,io):NativeProcessStartV301) : Result<ProcSupervisorV100.StartState,exn> =
    launches[node].Writer.TryWrite(args,io.Value) |> ignore
    Ok { Node=node; Registry=None; Supervisor=None
         Spec={procId="agy-output-fixture";fileName=executable;args=args;workingDir=Some directory
               role=None;probeMessage=None;probeCron=None;probeIntervalMs=None} }

let output sid status text =
    System.Text.Json.JsonSerializer.Serialize {| conversation_id=sid; status=status; response=text |}

/// 例：case (Some "saved-A") [{"conversation_id":"other-B",...}]。
/// 真正 raw factory 4.0.6 建 NativeAgyV301；callback 由測試手動送出，沒有 OS/model 程序。
/// 每個案例只有自己產生的 SQL DB，結束時沿用既有 fixture 清理。
let withAgy initial action = withDatabase(fun cs -> withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "all builtin quotations initialized"
    let channel=Channel.CreateUnbounded<string list * ProcLocalIo>()
    launches[node]<-channel
    try
        let start:Expr<NativeProcessStartV301 -> FeasibleT<_,ProcSupervisorV100.StartState,exn>> =
            <@ fun _ -> fun input -> startFixture input @>
        let stop:Expr<ProcSupervisorV100.StartState -> FeasibleT<_,unit,exn>> =
            <@ fun _ -> fun _ -> Ok() @>
        FodexFunRegistry.register node NativeProcessFodexFunV301.targetStart (System.Version(3,0,1)) start
        FodexFunRegistry.register node NativeProcessFodexFunV301.targetStop (System.Version(3,0,1)) stop
        let session=Guid.NewGuid(),None
        let loop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "3.0.1" node FodexFunRegistrationV301.targetLoop
                     (node,cs,session,ctx,ignore) |> required
        let owner=1L,session
        // Root 只是 raw 結果的接收者；Upsert 會詢問 capabilities，不能指向無人回覆的 DeadLetters。
        let wrapper={new IAgent<string,obj>(node,Agent.Wrapper node.DeadLetters,"",ctx) with
                         member _.AcquireParticipant=fun _ -> Error(NotSupportedException "Result receiver fixture")
                         member _.Send=fun message ->
                             match message with
                             | :? AgentInput as (GetCapabilities reply) -> reply(Ok [||])
                             | _ -> ()
                             Ok 0}
        loop.UpsertParticipant(owner,wrapper) |> required
        let participant=loop.PinParticipantReservation(Some "3.0.0")
                            (owner,"agy-test",ReActParticipantKind.RawExec,None,None) |> required
        initial |> Option.iter(fun sid -> ReActDataStoreV200.saveNativeSession cs participant sid |> required)
        let args="--native-adapter agy --model test --effort high" +
                 (initial |> Option.map(fun sid -> " --resume-session "+sid) |> Option.defaultValue "")
        let actor=evaluated<RawCreationV400,IActorRef> "4.0.6" node FodexFunRegistrationV300.targetRaw
                      (node,loop,cs,ctx,models,(participant,args)) |> required
        let turn text code =
            let expected=ReActDataStoreV200.readNativeSession cs participant |> required
            let result=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            actor.Tell(RawOrder(owner,Guid.NewGuid(),"fixture prompt",CancellationToken.None,result))
            let actualArgs,io=read channel
            Expect.contains actualArgs "--output-format" "adapter requests a specific format"
            Expect.contains actualArgs "json" "plain text is not the requested format"
            expected |> Option.iter(fun sid ->
                Expect.contains actualArgs "--conversation" "resume must be explicit"
                Expect.contains actualArgs sid "resume uses the existing SID")
            io.OnEvent(ProcLocalIoEvent.Stdout(1L,text))
            io.OnEvent(ProcLocalIoEvent.Exited(1L,code,None))
            // 等同一 mailbox 消化完 terminal；修前換 SID 會令 observation 拋例外、actor restart，
            // raw completion 因此遺失。先核 SQL 的不變量，不能把這個 timeout 當作正常 Error。
            let barrier=TaskCompletionSource<Result<ReActParticipantCapability array,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            actor.Tell(GetCapabilities(fun value -> barrier.TrySetResult value |> ignore))
            barrier.Task |> wait |> required |> ignore
            expected |> Option.iter(fun sid ->
                Expect.equal (ReActDataStoreV200.readNativeSession cs participant |> required) (Some sid)
                    "a terminal cannot replace the resume SID, even if actor observation rejects it")
            result.Task |> wait
        let history ()=loop.Collect session |> required
        let saved ()=ReActDataStoreV200.readNativeSession cs participant |> required
        action turn history saved
    finally
        launches.TryRemove node |> ignore))

let expectFailed result history =
    Expect.isError result "invalid terminal must not complete successfully"
    let events=history() |> Array.map(fun (_,_,event) -> event)
    Expect.isFalse (events |> Array.exists(function AgentWork(_,_,MessageCompleted _) -> true | _ -> false))
        "failure must not publish a successful generated message"
    Expect.isTrue (events |> Array.exists(function StdErr _ -> true | _ -> false)) "failure remains visible"
    Expect.equal (events |> Array.filter(function AgentEnd _ -> true | _ -> false) |> Array.length) 1
        "a failed turn must emit exactly one terminal so the wrapper can finish waiting"

[<Tests>]
let tests=testList "Native agy terminal V301" [
    testCase "malformed declared JSON cannot become successful plain text" <| fun () ->
        withAgy None (fun turn history saved ->
            expectFailed (turn "{\"response\":" 0) history
            Expect.isNone (saved()) "no invented session")
    testCase "plain text is rejected when the adapter explicitly requested JSON" <| fun () ->
        withAgy None (fun turn history saved ->
            expectFailed (turn "ordinary stdout, not a JSON envelope" 0) history
            Expect.isNone (saved()) "no session from plain text")
    testCase "empty stdout cannot complete a successful turn" <| fun () ->
        withAgy None (fun turn history saved ->
            expectFailed (turn "" 0) history
            Expect.isNone (saved()) "empty output cannot invent a session")
    testCase "a successful first turn needs a nonempty conversation id" <| fun () ->
        withAgy None (fun turn history saved ->
            expectFailed (turn "{\"status\":\"SUCCESS\",\"response\":\"answer\"}" 0) history
            Expect.isNone (saved()) "missing SID must not be committed")
    testCase "blank conversation id cannot complete a successful turn" <| fun () ->
        withAgy None (fun turn history saved ->
            expectFailed (turn (output "   " "SUCCESS" "answer") 0) history
            Expect.isNone (saved()) "blank SID must not be committed")
    // CLI help 未定其他欄位為必填；這次不能順帶移除 V300 的相容預設。
    testCase "missing status retains the existing SUCCESS default" <| fun () ->
        withAgy None (fun turn _ saved ->
            Expect.equal (turn "{\"conversation_id\":\"new-A\",\"response\":\"answer\"}" 0 |> required)
                ("new-A","answer") "existing status fallback"
            Expect.equal (saved()) (Some "new-A") "valid SID saved")
    testCase "missing response retains the original JSON response fallback" <| fun () ->
        withAgy None (fun turn _ saved ->
            let envelope="{\"conversation_id\":\"new-A\",\"status\":\"SUCCESS\"}"
            Expect.equal (turn envelope 0 |> required) ("new-A",envelope) "existing response fallback"
            Expect.equal (saved()) (Some "new-A") "valid SID saved")
    testCase "a session-store rejection cannot be ignored as a successful terminal" <| fun () ->
        withAgy None (fun turn history saved ->
            // 真正 saveNativeSession 的 200 字元上限回 Error；沒有替代 SQL writer。
            expectFailed (turn (output (String.replicate 201 "A") "SUCCESS" "answer") 0) history
            Expect.isNone (saved()) "persistence must succeed before publishing success")
    testCase "resume rejects a different SID before updating the persisted mapping" <| fun () ->
        withAgy (Some "saved-A") (fun turn history saved ->
            expectFailed (turn (output "other-B" "SUCCESS" "answer") 0) history
            Expect.equal (saved()) (Some "saved-A") "resume preserves the original conversation")
    testCase "a failure envelope cannot replace the existing SID" <| fun () ->
        withAgy (Some "saved-A") (fun turn history saved ->
            expectFailed (turn (output "other-B" "FAILURE" "failed") 0) history
            Expect.equal (saved()) (Some "saved-A") "validate before committing SID")
    testCase "valid first turn saves its SID and resumes that SID on the next turn" <| fun () ->
        withAgy None (fun turn history saved ->
            Expect.equal (turn (output "new-A" "SUCCESS" "one") 0 |> required) ("new-A","one") "first result"
            Expect.equal (saved()) (Some "new-A") "first SID saved"
            Expect.equal (turn (output "new-A" "SUCCESS" "two") 0 |> required) ("new-A","two") "second result"
            let completed=history() |> Array.choose(function _,_,AgentWork(_,_,MessageCompleted(_,text)) -> Some text | _ -> None)
            Expect.equal completed [|"one";"two"|] "exactly one completion per valid turn")
    testCase "valid explicit resume keeps the original conversation" <| fun () ->
        withAgy (Some "saved-A") (fun turn _ saved ->
            Expect.equal (turn (output "saved-A" "SUCCESS" "answer") 0 |> required) ("saved-A","answer") "resume result"
            Expect.equal (saved()) (Some "saved-A") "same SID")
    testCase "an empty response is allowed when the success envelope and SID are valid" <| fun () ->
        withAgy (Some "saved-A") (fun turn _ saved ->
            Expect.equal (turn (output "saved-A" "SUCCESS" "") 0 |> required) ("saved-A","") "empty response is still a string"
            Expect.equal (saved()) (Some "saved-A") "same SID")
    testCase "nonzero process exit rejects even a success-shaped envelope" <| fun () ->
        withAgy (Some "saved-A") (fun turn history saved ->
            expectFailed (turn (output "other-B" "SUCCESS" "answer") 1) history
            Expect.equal (saved()) (Some "saved-A") "process failure cannot change SID")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAgyFodexFunV301Tests failed"
#endif
