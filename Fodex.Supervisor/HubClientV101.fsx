namespace Fodex.Supervisor

open LoggerUtil

open System
open System.Diagnostics
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu

/// M011 的無狀態 HTTP 送件函數。沒有 ActorSystem、process、sid 或工作佇列。
/// main 在一輪先送出全部 Order，再 Receive；真正排程與結果保存在常駐 hub actor。
module HubClientV101 =
    type ClientArgument =
        | Url of string
        | Action of string
        | Who of string
        | Instruction of string
        | Order of who: string * instruction: string
        interface IArgParserTemplate with
            member argument.Usage =
                match argument with
                | Url _ -> "Existing loopback HTTP hub URL."
                | Action _ -> "order, receive or read-only health; never start/stop the host."
                | Who _ -> "Single-order participant name; pair with --instruction."
                | Instruction _ -> "Single-order instruction text; pair with --who."
                | Order _ -> "Repeat --order WHO INSTRUCTION to submit a round without restarting FSI per participant."

    /// 沒有 prompt/output 的 transport trace；每個 ioId 透過 header 傳給 hub。
    /// log 可由 FSI 寫 stderr；ReActData 的 IO 仍使用各 actor 的 ctx.logger。
    let request (http: HttpClient) (log: string -> unit) (ct: CancellationToken)
                (url: Uri) (method: HttpMethod) path who body : Task<string> = task {
        let id = Guid.NewGuid().ToString("N")
        let watch = Stopwatch.StartNew()
        let trace status =
            log ("FODEX_CLIENT_IO " + JsonSerializer.Serialize
                {| utc=DateTime.UtcNow; ticks=Stopwatch.GetTimestamp(); stage="client.http"
                   ioId=id; status=status; elapsedMs=watch.Elapsed.TotalMilliseconds; path=path; who=who |})
        trace "begin"
        try
            use message = new HttpRequestMessage(method,url.AbsoluteUri.TrimEnd('/') + path)
            message.Headers.Add("X-Fodex-Io-Id",id)
            if method = HttpMethod.Post then
                message.Content <- new StringContent(body,Encoding.UTF8,"application/json")
            use! response = http.SendAsync(message,HttpCompletionOption.ResponseContentRead,ct)
            response.EnsureSuccessStatusCode() |> ignore
            let! text = response.Content.ReadAsStringAsync ct
            trace "ok"
            return text
        with ex ->
            trace (if ex :? OperationCanceledException then "cancelled" else "error")
            return raise ex
    }

    /// 同一個 HTTP client 非同步送出所有 order，只等待「已接單」ack，不等待工作完成。
    /// 例：--action order --order contract "只回 orz" --order implementation "只回 OGC"。
    /// 單筆 Who/Instruction 格式保留方便人工操作；兩種格式不混用，不偷偷忽略輸入。
    /// 不自動 Receive、不重試、不保存 order/session 狀態；main 自己決定下一次派工。
    let run (http: HttpClient) argv log (ct: CancellationToken) : Task<string> = task {
        let options = ArgumentParser.Create<ClientArgument>().ParseCommandLine argv
        let url = Uri(options.GetResult Url)
        if not url.IsLoopback || url.Scheme <> "http" then invalidArg "url" "Only loopback HTTP is supported"
        match options.GetResult Action with
        | "order" ->
            let repeated = options.GetResults Order
            let singleWho,singleInstruction = options.TryGetResult Who,options.TryGetResult Instruction
            let orders =
                match repeated,singleWho,singleInstruction with
                | [],Some who,Some instruction -> [(who,instruction)]
                | (_::_),None,None -> repeated
                | _ -> invalidArg "order" "Use either repeated --order WHO INSTRUCTION or one --who/--instruction pair"
            for who,instruction in orders do
                if String.IsNullOrWhiteSpace who || String.IsNullOrWhiteSpace instruction then
                    invalidArg "order" "who and instruction must not be empty"
            let requests = orders |> List.map (fun (who,instruction) ->
                let body = JsonSerializer.Serialize {| who=who; instruct=instruction |}
                request http log ct url HttpMethod.Post "/order" who body)
            let! replies = Task.WhenAll requests
            // envelope 由程式組成；不是要求模型產生 JSON。原生生成文字只會出現在 receive。
            return if replies.Length=1 then replies[0] else "[" + String.Join(",",replies) + "]"
        | "receive" -> return! request http log ct url HttpMethod.Post "/receive" "" ""
        | "health" -> return! request http log ct url HttpMethod.Get "/health" "" ""
        | _ -> return invalidArg "action" "Use order, receive or health"
    }
