#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Akka wire 只傳 JSON string，不要求 caller 安裝 closure／IAgent／Process serializer。
/// id 是送件識別；同一個 send id 重試只查原工作，不重送 prompt。
/// after 是 WSID 內的 StreamId cursor；requestId 可指定只收某次 send 的輸出。
[<CLIMutable>]
type ServiceRequestV100 = {
    v:int; id:string; op:string; sid:Guid; args:string array; text:string
    after:int64; requestId:string; waitMs:int
}

module ServiceProtocolFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="Service.ParseRequest"
    let options=JsonSerializerOptions(PropertyNameCaseInsensitive=true)
    let encode (value:ServiceRequestV100)=JsonSerializer.Serialize(value,options)
    let request operation sid =
        {v=1;id=Guid.NewGuid().ToString("N");op=operation;sid=sid;args=[||];text="";after=0L;requestId="";waitMs=0}

    /// 例：{"v":1,"id":"create-1","op":"create","args":["--mainagent-type","agy"]}。
    /// args 的內容仍交 Console.ParseStartup 的 FAkka.Argu；此處只驗證 transport envelope。
    let parse (text:string) =
        try
            let value=JsonSerializer.Deserialize<ServiceRequestV100>(text,options)
            if isNull(box value) || value.v<>1 then invalidArg "v" "Expected service protocol v=1"
            if String.IsNullOrWhiteSpace value.id || value.id.Length>128 then invalidArg "id" "A nonempty request id of at most 128 characters is required"
            if not(List.contains value.op ["create";"attach";"send";"send_wait";"receive";"close";"list";"shutdown"]) then
                invalidArg "op" "Use create, attach, send, send_wait, receive, close, list or shutdown"
            if value.op<>"create" && value.op<>"list" && value.op<>"shutdown" && value.sid=Guid.Empty then invalidArg "sid" "Wrapper SID is required"
            if value.after<0L then invalidArg "after" "StreamId cursor cannot be negative"
            if value.waitMs<0 || value.waitMs>1200000 then invalidArg "waitMs" "Use 0..1200000 milliseconds; timeout never cancels or resends a prompt"
            if (value.op="send" || value.op="send_wait") && String.IsNullOrWhiteSpace value.text then invalidArg "text" "Prompt/command cannot be empty"
            Ok {value with args=if isNull value.args then [||] else value.args
                           requestId=if isNull value.requestId then "" else value.requestId
                           text=if isNull value.text then "" else value.text}
        with error -> Error error

    let reply id sid kind data = JsonSerializer.Serialize {|v=1;id=id;sid=sid;kind=kind;ok=true;data=data|}
    let failure id sid (error:exn) = JsonSerializer.Serialize {|v=1;id=id;sid=sid;kind="error";ok=false;error=error.Message|}

    let cause = function
        | AgentWork(_,id,_) | AgentCot(_,id,_) | AgentEnd(_,id,_) | StdOut(_,id,_) | StdErr(_,id,_) -> Some id
        | _ -> None

    /// 收件以 committed history 為準。causedBy 可跨 native/wrapper 轉送，沿既有因果邊找原 prompt。
    /// 不靠「收到的最後一則訊息」猜 request，避免兩個 client 的回覆被錯配。
    let belongsTo (edges:ReActDataStream<string> array) expected =
        let parents=edges |> Array.choose(fun edge -> let _,_,event=edge in cause event |> Option.map(fun p -> ReActJsonV300.streamIdOf edge,p)) |> dict
        fun event ->
            let visited=Collections.Generic.HashSet<int64>()
            let rec contains id =
                id=expected || (visited.Add id && match parents.TryGetValue id with true,parent -> contains parent | _ -> false)
            cause event |> Option.exists contains

    let quotation:Expr<string -> FeasibleT<_,ServiceRequestV100,exn>> = <@ fun _ -> fun input -> parse input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
