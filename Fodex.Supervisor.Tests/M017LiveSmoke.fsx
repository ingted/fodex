// Automation harness：以 argv 覆寫 M017 的完整 Argu defaults，必須 --interactive false。
// 不改 demo 原檔；真的走 wrapper IAgent/SQL/graph/Proc Supervisor/Codex。
#load "../Fodex.Supervisor/WrapperIAgentDemo.M017.fsx"
open System
open System.Diagnostics
open System.Threading.Tasks
open WrapperIAgentDemo.M017
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromMinutes 4.).GetAwaiter().GetResult()
let edges()=(reActLoop.ReadDataFlow session |> required).GetEdges()
let terminalAfter watermark =
    edges() |> Array.choose(function
        | ReActEndpoint.Participant(1L,_),ReActEndpoint.Human,AgentEnd(id,_,Message text) when id>watermark -> Some(id,text)
        | _ -> None)
    |> Array.sortBy fst
let watermark()=edges() |> Array.map ReActJsonV300.streamIdOf |> Array.max
let awaitTerminal mark =
    let time=Stopwatch.StartNew()
    let mutable result=terminalAfter mark
    while result.Length=0 && time.Elapsed<TimeSpan.FromMinutes 4. do
        Task.Delay(200).GetAwaiter().GetResult()
        result<-terminalAfter mark
    if result.Length=0 then failwith "Timed out waiting for root AgentEnd"
    printfn "SMOKE_TURN_SECONDS=%.3f" time.Elapsed.TotalSeconds
    result[result.Length-1] |> snd
let command text=request text |> wait |> required
let round text =
    let mark=watermark()
    command text |> ignore
    awaitTerminal mark

try
    printfn "SMOKE_INITIAL=%s" (command "/list_participant")
    let first=round "只回覆 FODEX_M017_OK，不使用任何工具。"
    if not(first.Contains "FODEX_M017_OK") then failwith "Main ECHO failed"
    printfn "SMOKE_ECHO=PASS"
    command "/wrapper_append first" |> ignore
    command "/wrapper_append second" |> ignore
    let preview=command "/wrapper_preview"
    if preview<>"first\nsecond" then failwithf "Draft preview mismatch: %s" preview
    command "/wrapper_clearAll" |> ignore
    printfn "SMOKE_DRAFT=PASS"
    printfn "SMOKE_NEXT_SETTINGS=%s" (command "/effort low")
    let second=round "只回覆 FODEX_M017_NEXT，不使用任何工具。"
    if not(second.Contains "FODEX_M017_NEXT") then failwith "Next settings ECHO failed"
    printfn "SMOKE_SETTINGS=PASS"
    let mark=watermark()
    command "/subagent(3) 只確認收到清單，先不要派工。" |> ignore
    awaitTerminal mark |> ignore
    printfn "SMOKE_PARTICIPANTS=%s" (command "/list_participant")
    let receive count =
        use timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes 4.)
        let results=ResizeArray<HubResult>()
        while results.Count<count do results.AddRange(hub.Receive timeout.Token |> wait)
        results.ToArray()
    let orderRound token =
        [|1..3|] |> Array.map(fun i -> hub.Order(sprintf "sub-%d" i,sprintf "只回覆 %s_%d，不使用任何工具。" token i)) |> Task.WhenAll |> wait |> ignore
        let results=receive 3
        for result in results do
            if result.Error.IsSome || result.SessionId.IsNone || not(result.Output.Contains token) then failwithf "Sub order failed: %A" result
        results |> Array.map(fun result -> result.Who,result.SessionId.Value) |> Map.ofArray
    let before=orderRound "M017_ROUND1"
    let after=orderRound "M017_ROUND2"
    if before<>after then failwith "Same worker did not retain same sid"
    printfn "SMOKE_SUBAGENTS=PASS %A" after
    // 指定 wrapper N 時由 Main chain 找 native；raw N 則直接進其 exec actor。
    let sub,info=ReActTopologyFodexFunV300.participants(reActLoop.ReadDataFlow session |> required) |> Array.find(fun (_,info) -> info.Name="sub-1")
    let waitActive () =
        let timer=Stopwatch.StartNew()
        while (reActLoop.GetStatus session |> required).ActiveParticipantTurnCount=0 && timer.Elapsed<TimeSpan.FromSeconds 20. do Task.Delay(100).GetAwaiter().GetResult()
        if (reActLoop.GetStatus session |> required).ActiveParticipantTurnCount=0 then failwith "Sub did not enter active turn"
    hub.Order("sub-1","用 shell 執行 powershell -NoProfile -Command \"Start-Sleep -Seconds 10\"，然後只回覆 M017_SUB_CONTINUED，不做其他工作。") |> wait |> ignore
    waitActive()
    command(sprintf "/effort(%d) medium" info.Number) |> printfn "SMOKE_SUB_SETTINGS=%s"
    let resumed=receive 1 |> Array.exactlyOne
    if resumed.Error.IsSome || resumed.SessionId<>Some before["sub-1"] then failwithf "Sub settings lost order or sid: %A" resumed
    printfn "SMOKE_SUB_RESUME=PASS %A" resumed.SessionId
    hub.Order("sub-1","用 shell 等待 15 秒後回覆 CANCEL_THIS。") |> wait |> ignore
    hub.Order("sub-1","只回覆 M017_QUEUED_SURVIVES，不使用工具。") |> wait |> ignore
    waitActive()
    command(sprintf "/cancel(%d)" (fst sub)) |> printfn "SMOKE_SUB_CANCEL=%s"
    let cancelled=receive 2
    if (cancelled |> Array.filter(fun result -> result.Error.IsSome) |> Array.length)<>1 ||
       not(cancelled |> Array.exists(fun result -> result.Output.Contains "M017_QUEUED_SURVIVES" && result.SessionId=Some before["sub-1"])) then
        failwithf "Cancel failed to preserve queued work: %A" cancelled
    printfn "SMOKE_SUB_QUEUE=PASS"
    let accepted,_,failedBefore,_=hub.Status() |> wait
    let dispatched=round "請用剛收到的派工介面，讓 sub-1、sub-2、sub-3 各回覆一次 M017_TOOL_OK。請先派完三個 order，再收齊本輪全部結果。最後只回覆你收齊幾筆及各 who 的 sid。不要自行建 wrapper 或 process。"
    let acceptedAfter,completed,failed,_=hub.Status() |> wait
    if acceptedAfter<>accepted+3 || completed<>acceptedAfter || failed<>failedBefore then failwithf "Main did not dispatch and receive three actual orders: %d/%d/%d" acceptedAfter completed failed
    printfn "SMOKE_MAIN_DISPATCH=PASS %s" dispatched
    command "/stop sub(3)" |> printfn "SMOKE_STOP=%s"
    printfn "SMOKE_ALL=PASS"
finally stop()
