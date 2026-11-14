#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Collections.Generic
open System.IO
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// scope 必須來自實際 provider 啟動設定；AccountScope/SettingsScope 是非秘密 identity/hash。
/// 不放使用者 cache KV，也不能以單一全域 models array 混用 AGY/Codex 或不同帳戶。
type NativeModelDirectoryScopeV100={Provider:string;Executable:string;Directory:string;AccountScope:string;SettingsScope:string}
type NativeModelCatalogSnapshotV100=
    | Fresh of fetchedAt:DateTimeOffset * models:JsonElement array
    | Stale of fetchedAt:DateTimeOffset * models:JsonElement array * refreshFailure:exn
type NativeModelDirectoryReadInputV100=FodexNode*NativeModelDirectoryScopeV100*bool*(unit -> Task<Result<JsonElement array,exn>>)
type NativeModelDirectoryInputV100=FodexNode*UniContext*NativeModelDirectoryScopeV100*bool

/// 只在本 node 傳送；外部 service 回 metadata JSON，不傳 TCS 或這個管理協定。
type NativeModelDirectoryCallV100=
    | ReadCatalog of NativeModelDirectoryScopeV100*bool*(unit -> Task<Result<JsonElement array,exn>>)*TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>
    | CatalogLoaded of NativeModelDirectoryScopeV100*DateTimeOffset*Result<JsonElement array,exn>

module NativeModelDirectoryFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="NativeModels.List"
    let targetRead="NativeModelDirectory.Read"
    let lifetime=TimeSpan.FromMinutes 5.
    let clone=function
        | Fresh(at,models) -> Fresh(at,models |> Array.map(fun item -> item.Clone()))
        | Stale(at,models,error) -> Stale(at,models |> Array.map(fun item -> item.Clone()),error)
    let normalize (scope:NativeModelDirectoryScopeV100)=
        if not(List.contains scope.Provider ["agy";"codex"]) then invalidArg "provider" "Expected agy or codex"
        if String.IsNullOrWhiteSpace scope.AccountScope || String.IsNullOrWhiteSpace scope.SettingsScope then invalidArg "scope" "Account and working-settings identities are required"
        let file=FileInfo scope.Executable
        if not file.Exists then raise(FileNotFoundException("Catalog executable must be resolved before lookup",scope.Executable))
        // 長度／mtime 是本機 executable identity；不在每次 model validation 重讀整個大型exe。
        {scope with Executable=sprintf "%s|%d|%d" file.FullName file.Length file.LastWriteTimeUtc.Ticks
                    Directory=Path.GetFullPath scope.Directory}

    type DirectoryActor() as self=
        inherit ReceiveActor()
        let actor=self :> IInternalActor
        let snapshots=Dictionary<NativeModelDirectoryScopeV100,NativeModelCatalogSnapshotV100>()
        let pending=Dictionary<NativeModelDirectoryScopeV100,ResizeArray<TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>>>()
        do self.Receive<NativeModelDirectoryCallV100>(fun message ->
            match message with
            | ReadCatalog(key,force,load,reply) ->
                let cached=match snapshots.TryGetValue key with true,value -> Some value | _ -> None
                match cached with
                | Some(Fresh(at,_) as value) when not force && DateTimeOffset.UtcNow-at<lifetime -> reply.TrySetResult(Ok(clone value)) |> ignore
                | _ ->
                    match pending.TryGetValue key with
                    | true,waiters -> waiters.Add reply
                    | _ ->
                        pending[key]<-ResizeArray([reply])
                        let target=actor.ActorContext.Self
                        // 真正 async IO；立即釋放 mailbox，其他 scope 與同 scope waiters 都可進入。
                        task {
                            let! result=task {try return! load() with error -> return Error error}
                            target.Tell(CatalogLoaded(key,DateTimeOffset.UtcNow,result))
                        } |> ignore
            | CatalogLoaded(key,at,result) ->
                let previous=match snapshots.TryGetValue key with true,value -> Some value | _ -> None
                let outcome=
                    match result,previous with
                    | Ok models,_ when models.Length>0 -> Ok(Fresh(at,models |> Array.map(fun item -> item.Clone())))
                    | Ok _,_ -> Error(InvalidOperationException "Empty model catalog" :> exn)
                    | Error error,Some(Fresh(time,models) | Stale(time,models,_)) -> Ok(Stale(time,models,error))
                    | Error error,None -> Error error
                // 空輸出也是一次刷新失敗；同樣撤銷舊 Fresh 資格。
                let outcome=match outcome,previous with
                            | Error error,Some(Fresh(time,models) | Stale(time,models,_)) -> Ok(Stale(time,models,error))
                            | _ -> outcome
                match outcome with Ok value -> snapshots[key]<-value | _ -> ()
                match pending.TryGetValue key with
                | true,waiters ->
                    pending.Remove key |> ignore
                    for reply in waiters do reply.TrySetResult(outcome |> Result.map clone) |> ignore
                | _ -> ())
        override _.PostStop()=
            for KeyValue(_,waiters) in pending do
                for reply in waiters do reply.TrySetResult(Error(InvalidOperationException "Model directory stopped" :> exn)) |> ignore
            base.PostStop()

    // CWT factory 只建立 Lazy；實際建立 actor 的副作用僅發生在被採用的 Lazy 一次。
    let directories=ConditionalWeakTable<FodexNode,Lazy<IActorRef>>()
    let directory node=directories.GetValue(node,fun owner -> lazy(owner.ActorOf(Props.Create(fun () -> DirectoryActor()),"native-model-directory"))).Value
    /// load 是 invocation 的 IO dependency，測試可給未完成 Task；q 不捕捉該 delegate。
    let read ((node,scope,force,load):NativeModelDirectoryReadInputV100)=task {
        try
            let key=normalize scope
            let reply=TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            (directory node).Tell(ReadCatalog(key,force,load,reply))
            return! reply.Task
        with error -> return Error error }
    /// 顯式 list 用 force=true；validation 用false且只接受Fresh。失敗的舊快照必為Stale。
    let list ((node,ctx,scope,force):NativeModelDirectoryInputV100)=task {
        try
            let target,version=if scope.Provider="agy" then NativeAgyModelCatalogFodexFunV301.target,"3.0.1" else NativeModelCatalogFodexFunV302.targetLoad,"3.0.2"
            let query=FodexFunBinding.pin<NativeCatalogQueryInputV301,Task<Result<JsonElement array,exn>>> node (Some version) target
            let read=FodexFunBinding.pin<NativeModelDirectoryReadInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> node (Some "1.0.0") targetRead
            return! FodexFunBinding.invokeAsync read (node,scope,force,fun () -> FodexFunBinding.invokeAsync query (node,ctx,scope.Executable,scope.Directory,CancellationToken.None))
        with error -> return Error error }

    let quotation:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> = <@ fun _ -> fun input -> Ok(list input) @>
    let readQuotation:Expr<NativeModelDirectoryReadInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> = <@ fun _ -> fun input -> Ok(read input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation
                       FodexFunRegistry.declare targetRead version readQuotation|]
