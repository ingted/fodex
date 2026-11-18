#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Registry
#endif
open System
open System.Text.Json
open LineV4StoreContract
open LineV4StoreAsync
open LineV4Transport
module Commands=LineV4Commands

exception RegistryRejected of string
type RegistryDecision = RootSession option * string * string option

/// Resolve strictly within the supplied conversation roster, including SID prefixes.
let lookup (roots:RootSession array) selector =
    try
        let sid=Commands.resolve (roots |> Array.map(fun root -> root.Mapping.WrapperId,root.Topic)) selector
        roots |> Array.find(fun root -> root.Mapping.WrapperId=sid)
    with
    | :? Collections.Generic.KeyNotFoundException as error -> raise(RegistryRejected error.Message)
    | :? ArgumentException as error -> raise(RegistryRejected error.Message)
let available roots selector =
    let root=lookup roots selector
    if List.contains root.Lifecycle ["Dropping";"Dropped";"MigrationAmbiguous"] then raise(RegistryRejected("Root is "+root.Lifecycle))
    root

/// The common registry transaction for CLI and LINE. Its ids are local domain ids,
/// not RN Headers/receipts; callers independently materialize their proper output.
let apply db (sql:AsyncStore) settings owner conversation operationId focusScope
          (roots:RootSession array) (defaultRoot:RootSession) (selected:RootSession) command ct = task {
    let reply root text:RegistryDecision=root,"Immediate",Some text
    let barrier (root:RootSession) kind force=task {
        let parameters=["@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation;"@id",box operationId;"@force",box force]
        let! _=Sql.execute db ct "UPDATE dbo.LineFodexV3Sessions SET OrderedControlId=@id,OrderedControlEpoch=OrderedControlEpoch+1,OrderedControlState='Pending' WHERE WrapperId=@w AND Generation=@g AND (OrderedControlId IS NULL OR @force=1)" parameters
        if kind="Drop" then
            let! _=Sql.execute db ct "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Dropping' WHERE WrapperId=@w AND Generation=@g AND OrderedControlId=@id AND DeletedAtUtc IS NULL" parameters
            ()
        return Some root,kind,None }
    match command with
    | Commands.Setagent selector ->
        let root=available roots selector
        if root.Lifecycle<>"Ready" then raise(RegistryRejected "Root is not Ready")
        do! sql.SelectInTransactionAsync(db,conversation,root.Mapping.WrapperId,ct)
        do! LineV4Focus.updateInTransaction db focusScope conversation false ct
        return reply (Some root) (sprintf "Selected Topic=%s SID=%O" root.Topic root.Mapping.WrapperId)
    | Commands.Setdefault selector ->
        let root=available roots selector
        if root.Lifecycle<>"Ready" then raise(RegistryRejected "預設 agent 必須 Ready")
        do! sql.SetDefaultInTransactionAsync(db,conversation,root,ct)
        return reply (Some root) (sprintf "Default Topic=%s SID=%O；目前選取仍是 %s" root.Topic root.Mapping.WrapperId selected.Topic)
    | Commands.Newagent(engine,topic) ->
        let _,key=Commands.topic topic
        if roots |> Array.exists(fun root -> root.TopicKey=key) then raise(RegistryRejected "Topic already exists")
        let! root=sql.ReserveRootInTransactionAsync(db,conversation,operationId,engine,topic,nativeArguments settings engine,owner,ct)
        return Some root,"Provision",None
    | Commands.Dropagent(selector,force) ->
        let root=lookup roots selector
        if root.Mapping.Key=defaultRoot.Mapping.Key then raise(RegistryRejected "不能刪除預設 agent；請先使用 **setdefault 切換預設。")
        if root.Lifecycle="MigrationAmbiguous" && not force then raise(RegistryRejected "舊資料缺少啟動資訊；須明確 --force 清理。")
        if root.Lifecycle="Dropped" || (root.Lifecycle="Dropping" && not force) then raise(RegistryRejected("Root is "+root.Lifecycle))
        return! barrier root "Drop" force
    | Commands.Model(selector,_,_) -> return! barrier (available roots selector) "Model" false
    | Commands.Whoami ->
        return reply (Some selected) (sprintf "目前選取：%s\nSID：%O\n預設：%s\n預設 SID：%O\n普通訊息與 /next 的目標：%s" selected.Topic selected.Mapping.WrapperId defaultRoot.Topic defaultRoot.Mapping.WrapperId selected.Topic)
    | Commands.Listagent ->
        return reply None (roots |> Array.map(fun root ->
            let flags=(if root.Mapping.Key=selected.Mapping.Key then "[目前選取]" else "")+(if root.Mapping.Key=defaultRoot.Mapping.Key then "[預設]" else "")
            sprintf "%sTopic=%s SID=%O status=%s" flags root.Topic root.Mapping.WrapperId root.Lifecycle) |> String.concat "\n")
    | Commands.To _ -> return invalidArg "command" "Targeted prompt is not a registry control"
    | _ -> return None,"Query",None }
