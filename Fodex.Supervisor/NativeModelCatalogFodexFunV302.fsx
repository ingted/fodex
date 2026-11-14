#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

type ModelPageRequestV302=string option -> Task<Result<JsonElement,exn>>

module NativeModelCatalogFodexFunV302 =
    let version=System.Version(3,0,2)
    let target="NativeSettings.ModelCatalog"
    let targetLoad="NativeSettings.LoadModelCatalog"
    let required=function Ok value -> value | Error(error:exn) -> raise error
    let flag defaultValue key (model:JsonElement)=
        match model.TryGetProperty(key:string) with
        | false,_ -> defaultValue
        | true,value -> value.GetBoolean()

    /// 例：request None -> 第一頁，request(Some nextCursor) -> 下一頁；全頁成功才發布。
    /// 缺頁／重複cursor／空目錄皆Error，不把可見的部分冒稱完整目錄。
    let read (request:ModelPageRequestV302)=task {
        try
            let models=ResizeArray<JsonElement>()
            let cursors=HashSet<string>(StringComparer.Ordinal)
            let ids=HashSet<string>(StringComparer.Ordinal)
            let mutable cursor=None
            let mutable finished=false
            while not finished do
                let! result=request cursor
                let page=required result
                for model in page.GetProperty("data").EnumerateArray() do
                    if not(flag false "hidden" model || flag false "isHidden" model) && flag true "isSelectable" model && flag true "selectable" model then
                        let id=match model.TryGetProperty "model" with true,value -> value.GetString() | _ -> model.GetProperty("id").GetString()
                        if String.IsNullOrWhiteSpace id || not(ids.Add id) then invalidOp "Codex model/list contains an invalid or duplicate model"
                        models.Add(model.Clone())
                match page.TryGetProperty "nextCursor" with
                | false,_ -> finished<-true
                | true,value when value.ValueKind=JsonValueKind.Null -> finished<-true
                | true,value when value.ValueKind=JsonValueKind.String && not(String.IsNullOrWhiteSpace(value.GetString())) ->
                    let next=value.GetString()
                    if not(cursors.Add next) then invalidOp "Codex model/list repeated a cursor"
                    cursor<-Some next
                | _ -> invalidOp "Malformed Codex model/list cursor"
            if models.Count=0 then invalidOp "Codex returned an empty selectable model catalog"
            return Ok(models.ToArray())
        with error -> return Error error }

    /// 一次管理查詢只 initialize/model/list，完全不建立 thread/turn。
    /// websocket與process各自清理；任一清理失敗也不能當作成功目錄交付。
    let load ((node,_,executable,directory,ct):NativeCatalogQueryInputV301)=task {
        let mutable resource:ProcSupervisorV100.StartState option=None
        let mutable connection:CodexAppServerConnectionV101 option=None
        let mutable outcome:Result<JsonElement array,exn>=Error(InvalidOperationException "Not queried" :> exn)
        let cleanupErrors=ResizeArray<exn>()
        let start=FodexFunBinding.pin<NativeProcessStartV301,Task<Result<ProcSupervisorV100.StartState,exn>>> node (Some "3.0.2") NativeProcessFodexFunV302.targetStart
        let stop=FodexFunBinding.pin<ProcSupervisorV100.StartState,Task<Result<unit,exn>>> node (Some "3.0.2") NativeProcessFodexFunV302.targetStop
        use budget=CancellationTokenSource.CreateLinkedTokenSource ct
        budget.CancelAfter(TimeSpan.FromSeconds 30.)
        try
            use listener=new TcpListener(IPAddress.Loopback,0)
            listener.Start()
            let port=(listener.LocalEndpoint :?> IPEndPoint).Port
            listener.Stop()
            let endpoint=sprintf "ws://127.0.0.1:%d" port
            let! launched=FodexFunBinding.invokeAsync start (node,executable,directory,["app-server";"--listen";endpoint],None)
            resource<-Some(required launched)
            let! connected=CodexAppServerV101.connectAsync endpoint ignore ignore budget.Token
            let client=required connected
            connection<-Some client
            let! initialized=CodexAppServerV101.initializeAsync(client).WaitAsync budget.Token
            required initialized
            let read=FodexFunBinding.pin<ModelPageRequestV302,Task<Result<JsonElement array,exn>>> node (Some "3.0.2") target
            let! result=FodexFunBinding.invokeAsync read (fun cursor -> client.CallAsync("model/list",box {|cursor=Option.toObj cursor;limit=100;includeHidden=true|},budget.Token))
            outcome<-result
        with error -> outcome<-Error error
        match connection with
        | Some client ->
            try do! client.CloseAsync()
            with error -> cleanupErrors.Add error
        | None -> ()
        match resource with
        | Some state ->
            let! stopped=FodexFunBinding.invokeAsync stop state
            match stopped with
            | Ok _ -> ()
            | Error error -> cleanupErrors.Add error;NativeProcessFodexFunV301.releaseActors state
        | None -> ()
        return if cleanupErrors.Count=0 then outcome
               else Error(AggregateException("Model catalog resource cleanup failed",cleanupErrors) :> exn) }

    let quotation:Expr<ModelPageRequestV302 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> = <@ fun _ -> fun request -> Ok(read request) @>
    let loadQuotation:Expr<NativeCatalogQueryInputV301 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> = <@ fun _ -> fun input -> Ok(load input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation
                       FodexFunRegistry.declare targetLoad version loadQuotation|]
