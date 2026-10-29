namespace Fodex.Supervisor.Demos

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// MCP / shell 都接收這個「程式產生」的結果，不要求模型依此 schema 生成。
/// Output 是 Codex 原始完成文字；錯誤與空答案不可混為一談。
type HubResult = {
    OrderId: string
    Who: string
    SessionId: string option
    Output: string
    Error: string option
}

/// 只供 M008/M009 的派工示範，不是另一個 Fodex runtime 或 ReAct history。
/// participants 的每個函數擁有自己的 wrapper 或 native exec session。
/// 範例：hub.Order("contract", "請 review...")；let! ready = hub.Receive(ct)。
type DemoOrderHubV100(participants: Map<string,string -> CancellationToken -> Task<Result<string * string,exn>>>) =
    let gate = obj()
    let lifetime = new CancellationTokenSource()
    let ready = Channel.CreateUnbounded<HubResult>()
    let readers = new SemaphoreSlim(1,1)
    let tails = Dictionary<string,Task>()
    let mutable stopped = false
    let mutable accepted = 0
    let mutable finished = 0
    let mutable failed = 0
    let completions = Dictionary<string,int>()
    let sessions = Dictionary<string,HashSet<string>>()

    member _.Participants = participants |> Map.toArray |> Array.map fst
    member _.Counts = lock gate (fun () -> accepted,finished)
    member _.FailedCount = lock gate (fun () -> failed)
    /// 僅是驗收用的 order metadata，不保存第二份 ReActDataStream。
    member _.SessionSummary = lock gate (fun () ->
        participants |> Map.toArray |> Array.map (fun (who,_) ->
            let count = match completions.TryGetValue who with true,value -> value | _ -> 0
            let ids = match sessions.TryGetValue who with true,value -> value |> Seq.sort |> Seq.toArray | _ -> [||]
            who,count,ids))

    /// 立即回 orderId；同 who 接在前一個工作之後，不同 who 可以並行。
    /// 不使用 shell 拼字串執行 instruction；它只是交給 participant 的文字資料。
    member _.Order(who: string, instruction: string) =
        if String.IsNullOrWhiteSpace instruction then invalidArg "instruction" "Instruction cannot be empty"
        let perform = participants |> Map.tryFind who |> Option.defaultWith (fun () -> invalidArg "who" "Unknown participant")
        lock gate (fun () ->
            if stopped then invalidOp "Hub is stopping"
            let orderId = Guid.NewGuid().ToString("N")
            let previous = match tails.TryGetValue who with true,value -> value | _ -> Task.CompletedTask
            let work = Task.Run(Func<Task>(fun () -> task {
                do! previous
                let! result = task {
                    try
                        lifetime.Token.ThrowIfCancellationRequested()
                        return! perform instruction lifetime.Token
                    with ex -> return Error ex
                }
                let output =
                    match result with
                    | Ok(sid,text) -> {OrderId=orderId;Who=who;SessionId=Some sid;Output=text;Error=None}
                    | Error ex -> {OrderId=orderId;Who=who;SessionId=None;Output="";Error=Some ex.Message}
                // 無 bounded-channel backpressure：已接受的結果不能因 receive 尚未發生而遺失。
                lock gate (fun () ->
                    finished <- finished + 1
                    completions[who] <- (match completions.TryGetValue who with true,value -> value | _ -> 0) + 1
                    if output.Error.IsSome then failed <- failed + 1
                    match output.SessionId with
                    | Some sid ->
                        if not (sessions.ContainsKey who) then sessions[who] <- HashSet<string>()
                        sessions[who].Add sid |> ignore
                    | None -> ())
                ready.Writer.TryWrite output |> ignore
            }))
            tails[who] <- work
            accepted <- accepted + 1
            orderId)

    /// 空時 async 等第一筆，然後一次取走當下所有 ready。
    /// 不等仍在生成的慢 participant；多個 receive 以 gate 避免互相拆散同一批。
    /// ct 取消空佇列的等待，不取消已接受的 order；一旦回傳 batch 就視為已領取。
    /// 此 demo 不提供 HTTP delivery acknowledgement；取出後斷線不會自動重送。
    member _.Receive(ct: CancellationToken) : Task<HubResult array> = task {
        do! readers.WaitAsync(ct)
        try
            let! first = ready.Reader.ReadAsync(ct).AsTask()
            let batch = ResizeArray<HubResult>()
            batch.Add first
            let mutable next = Unchecked.defaultof<HubResult>
            while ready.Reader.TryRead(&next) do batch.Add next
            return batch.ToArray()
        finally readers.Release() |> ignore
    }

    /// 只有 host 停止才取消工作。等待所有 lane 收尾後才關閉結果 channel。
    member _.StopAsync() : Task = task {
        let work = lock gate (fun () -> stopped <- true; tails.Values |> Seq.toArray)
        lifetime.Cancel()
        do! Task.WhenAll work
        ready.Writer.TryComplete() |> ignore
    }
