namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// 查詢函數是 IO dependency，不是另一個 runtime 類別；測試可給 deterministic protocol responses。
// Reuse the stable ModelPageRequestV300 input contract.

module NativeModelCatalogFodexFunV301 =
    let version = System.Version(3,0,1)
    let target = "NativeSettings.ModelCatalog"

    /// 接到最後一頁才成功；缺頁／重複 cursor 回 Error，不能當成模型不支援而取消使用者工作。
    let read (request: ModelPageRequestV300) : Result<JsonElement array,exn> =
        try
            let models = ResizeArray<JsonElement>()
            let cursors = HashSet<string>()
            let mutable cursor = None
            let mutable finished = false
            while not finished do
                let page = request cursor |> function Ok value -> value | Error ex -> raise ex
                for model in page.GetProperty("data").EnumerateArray() do models.Add(model.Clone())
                match page.TryGetProperty "nextCursor" with
                | true,value when value.ValueKind=JsonValueKind.String && not(String.IsNullOrWhiteSpace(value.GetString())) ->
                    let next = value.GetString()
                    if not(cursors.Add next) then invalidOp "Codex model/list returned a repeated cursor"
                    cursor <- Some next
                | _ -> finished <- true
            if models.Count=0 then invalidOp "Codex returned an empty model catalog"
            Ok(models.ToArray())
        with ex -> Error ex

    let quotation : Expr<ModelPageRequestV300 -> FeasibleT<_,JsonElement array,exn>> =
        <@ fun _ -> fun request -> read request @>

    /// 一次組裝查一次 catalog。Process lifecycle 沿用 Proc Supervisor 的 FeasibleT[] SOP。
    /// 只 initialize/model/list，不建 Codex thread、不執行模型；成功失敗都清除自己的 process/connection。
    let load (node: FodexNode) codex directory =
        let mutable resource: ProcSupervisorV100.StartState option = None
        let mutable connection: CodexAppServerConnection option = None
        let result =
            try
                let listener = new TcpListener(IPAddress.Loopback,0)
                listener.Start()
                let port = (listener.LocalEndpoint :?> IPEndPoint).Port
                listener.Stop()
                let endpoint = sprintf "ws://127.0.0.1:%d" port
                let launch=FodexFunBinding.pin<NativeProcessStartV301,ProcSupervisorV100.StartState> node (Some "3.0.1") NativeProcessFodexFunV301.targetStart
                let started=launch(node,codex,directory,["app-server";"--listen";endpoint],None) |> ReActLoopHandlerV300.required
                resource <- Some started
                let connected = CodexAppServerV100.connect endpoint ignore ignore |> ReActLoopHandlerV300.required
                connection <- Some connected
                CodexAppServerV100.initialize connected |> ReActLoopHandlerV300.required
                let read = ReActLoopHandlerV300.bind<ModelPageRequestV300,JsonElement array> node target
                read(fun cursor -> connected.Call("model/list",box {|cursor=Option.toObj cursor;limit=100;includeHidden=true|}))
            with ex -> Error ex
        // Disposal of one resource must not skip the other. Do not lose a process handle
        // on an RPC/connection exception; final actor teardown remains the fallback.
        let errors=ResizeArray<exn>()
        try connection |> Option.iter(fun value -> (value :> IDisposable).Dispose())
        with error -> errors.Add error
        resource |> Option.iter(fun state ->
            let stop=FodexFunBinding.pin<ProcSupervisorV100.StartState,unit> node (Some "3.0.1") NativeProcessFodexFunV301.targetStop
            match stop state with
            | Ok () -> ()
            | Error error -> errors.Add error;NativeProcessFodexFunV301.releaseActors state)
        match result,errors.Count with
        | result,0 -> result
        | Error error,_ -> Error(AggregateException(Array.append [|error|] (errors.ToArray())) :> exn)
        | Ok _,_ -> Error(AggregateException(errors) :> exn)

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
