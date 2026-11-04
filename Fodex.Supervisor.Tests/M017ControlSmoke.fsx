// 真實 generation/cancel/COT/compact/restart + remote owner 驗收。
// 使用 M017 的完整 argv，--interactive false；live smoke 不是一般 Test Explorer 自動執行項。
#load "../Fodex.Supervisor/WrapperIAgentDemo.M017.fsx"
open System
open System.Diagnostics
open System.Threading.Tasks
open Akka.Actor
open WrapperIAgentDemo.M017
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromMinutes 5.).GetAwaiter().GetResult()
let command text=request text |> wait |> required
let graph()=reActLoop.ReadDataFlow session |> required
let main()=ReActTopologyFodexFunV300.main(graph(),(1L,session)) |> required
let sid p=(reActLoop.ResolveParticipant p |> required).GetSessionId() |> required
let lastId()=(graph()).GetEdges() |> Array.map ReActJsonV300.streamIdOf |> Array.max
let until name predicate =
    let timer=Stopwatch.StartNew()
    while not(predicate()) && timer.Elapsed<TimeSpan.FromMinutes 4. do Task.Delay(200).GetAwaiter().GetResult()
    if not(predicate()) then failwith("Timeout: "+name)
let completed mark =
    (graph()).GetEdges() |> Array.exists(function
        | ReActEndpoint.Participant(1L,_),ReActEndpoint.Human,AgentEnd(id,_,_) when id>mark -> true
        | _ -> false)
let run text =
    let mark=lastId()
    command text |> ignore
    until "main terminal" (fun () -> completed mark)
let active()=(reActLoop.GetStatus session |> required).ActiveParticipantTurnCount>0

try
    run "只回覆 M017_CONTROL_READY，不使用工具。"
    let original=main()
    let originalSid=sid original
    let mark=lastId()
    command "請用 shell 執行 powershell -NoProfile -Command \"Start-Sleep -Seconds 15\"，完成後只回覆 M017_CONTINUED。不要做其他工作。" |> ignore
    until "active generation" active
    command(sprintf "/effort(%d) medium" (fst original)) |> printfn "CONTROL_NUMBERED=%s"
    until "resumed main terminal" (fun () -> completed mark && not(active()))
    if sid original<>originalSid then failwith "Numbered settings changed logical Codex session"
    printfn "CONTROL_SAME_SID=PASS %A" originalSid
    // 取消確認後才回 command result；下一個普通 prompt 必須仍可使用原 sid。
    command "請用 shell 執行 powershell -NoProfile -Command \"Start-Sleep -Seconds 15\"，然後回覆 CANCEL_TEST。" |> ignore
    until "active cancellation target" active
    command(sprintf "/cancel(%d)" (fst original)) |> printfn "CONTROL_CANCEL=%s"
    if active() then failwith "Cancel acknowledged before terminal confirmation"
    run "只回覆 M017_AFTER_CANCEL，不使用工具。"
    if sid original<>originalSid then failwith "Cancel lost session identity"
    command "/effort low" |> ignore
    let mark=lastId()
    command "/wrapper_cot 請只整理目前驗證到的控制行為，以 M017_COT 開頭用一句話摘要，不要使用工具。" |> printfn "CONTROL_COT=%s"
    until "COT followed by main" (fun () -> completed mark && not(active()))
    let cot=ReActTopologyFodexFunV300.participants(graph()) |> Array.filter(fun (_,info) -> info.Name="cot")
    if cot.Length=0 || cot |> Array.exists(fun (_,info) -> info.SessionId=Some originalSid) then failwith "COT reused main session"
    printfn "CONTROL_COT_ISOLATED=PASS"
    command "/wrapper_compact" |> printfn "CONTROL_COMPACT=%s"
    let compacted=main()
    if compacted=original || sid compacted=originalSid then failwith "Compact did not install a fresh main"
    let compactSid=sid compacted
    command "/wrapper_restart" |> printfn "CONTROL_RESTART=%s"
    let restarted=main()
    if restarted=compacted || sid restarted=compactSid then failwith "Restart did not install a fresh main"
    run "只回覆 M017_FRESH_MAIN，不使用工具。"
    use client=ActorSystem.Create("m017-real-client-"+Guid.NewGuid().ToString("N"),RemoteFodexFunV209.config "127.0.0.1" 0)
    let address=Akka.Serialization.Serialization.SerializedActorPath remoteOwner
    let remote=client.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
    RemoteControlFodexFunV300.register client
    let remoteCall=ReActLoopHandlerV300.bind<RemoteCommandV300,Task<Result<string,exn>>> client RemoteControlFodexFunV300.target
    let listed=remoteCall(remote,"/list_participant",TimeSpan.FromSeconds 20.) |> required |> wait |> required
    if not(listed.Contains "Retired") then failwith "Remote owner did not return retained topology"
    client.Terminate().GetAwaiter().GetResult() |> ignore
    printfn "CONTROL_REMOTE_REAL_WRAPPER=PASS"
    printfn "CONTROL_ALL=PASS"
finally stop()
