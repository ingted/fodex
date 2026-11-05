#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// 無狀態 client 的版本邊界。HTTP/Argu 行為沿用 V101，呼叫端只選 quotation。
/// 此 input 不含 FodexNode；單純送件不需要建立 ActorSystem 或載入 node 的 SQL registry。
module HubClientFodexFunV101 =
    type RunInput = HttpClient * string array * (string -> unit) * CancellationToken

    [<Literal>]
    let targetRun = "HubClient.Run"

    let version = System.Version(1,0,1)

    /// 例：Invoke (http, [|"--url"; url; "--action"; "health"|], log, ct)。
    /// 外層 Result 表達 operation 的啟動；HTTP/取消等非同步失敗由 Task 內的 Result 表達。
    /// 不重送 order，因為連線失敗時 hub 可能已接單，重送會重複執行。
    let run ((http,argv,log,ct):RunInput) : Result<Task<Result<string,exn>>,exn> =
        Ok(task {
            try
                let! response = HubClientV101.run http argv log ct
                return Ok response
            with error -> return Error error
        })

    /// HttpClient/logger/token 全在執行時傳入，不捕捉資源；同一 q 可序列化後再 eval。
    /// 外層 input 準備吃 input 的函數，FeasibleFun.invoke 負責將同一 input 送入兩層。
    let quotation : Expr<RunInput -> FeasibleT<RunInput,Task<Result<string,exn>>,exn>> =
        <@ fun _ -> fun input -> run input @>

    [<AutoOpen>]
    module Definitions =
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetRun version quotation
        |]
