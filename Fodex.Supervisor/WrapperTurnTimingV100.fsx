namespace Fodex.Supervisor

open LoggerUtil

open System.Diagnostics
open Fodex.Supervisor.Contracts.CoreMod

/// Demo 的展示／計時小工具，不新增 registry target、FodexFun 或 participant 行為。
module WrapperTurnTimingV100 =
    /// MessageCompleted 是「單則訊息完成」，不是整個 turn 完成。
    /// AgentWork 是一般工作；AgentCot 只供明確的 COT 流程，不能由 commentary phase 推定。
    /// 範例：AgentWork(..., MessageCompleted("id", "仍在等待")) -> Some("WorkMessage", "仍在等待")。
    let completedMessage (event: ReActEvent<'Prompt>) =
        match event with
        | AgentCot(_,_,MessageCompleted(_,text)) -> Some("CotMessage",text)
        | AgentWork(_,_,MessageCompleted(_,text)) -> Some("WorkMessage",text)
        | _ -> None

    /// Caller 必須緊鄰 wrapper.Send 之前 Restart；只有同一回合的 AgentEnd 可以停錶。
    /// 不用首筆輸出／MessageCompleted／Send 的 Ok 當終點；StdErr 也不是 terminal event。
    /// AgentEnd 只證明回合結束，是否成功仍交給原本的 StdErr／hub 驗收。
    /// 範例：tryFinish watch (AgentEnd(9L,8L,Message "orz")) -> Some elapsed。
    /// Caller 的 output gate 負責同步 Restart 與此函數；本函數不另設 runtime 或全域 state。
    let tryFinish (watch: Stopwatch) (event: ReActEvent<'Prompt>) =
        match event with
        | AgentEnd _ when watch.IsRunning ->
            watch.Stop()
            Some watch.Elapsed
        | _ -> None
