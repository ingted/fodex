#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

type AgyCacheRegistrationV100=Missing | Enabled | Disabled
type NativeAgyCacheInputV100=FodexNode*UniContext*string*string*string*CancellationToken

/// 共用 cache 設定，不附帶 root lifecycle/hub 權限。native304 的所有建立路徑使用此 q。
module NativeMcpConfigurationFodexFunV100 =
    let version=System.Version(1,0,0)
    let name="fodex_cache"
    let targetCodex="NativeMcpConfiguration.Codex"
    let targetPlan="NativeMcpConfiguration.AgyPlan"
    let targetAgy="NativeMcpConfiguration.EnsureAgy"
    let required=function Ok value -> value | Error(error:exn) -> raise error
    let endpoint text=
        let uri=Uri(text:string)
        if not(uri.IsAbsoluteUri) || not(List.contains uri.Scheme ["http";"https"]) || uri.AbsolutePath<>"/mcp" || uri.Query<>"" || uri.Fragment<>"" || uri.UserInfo<>"" then
            invalidArg "url" "Expected explicit HTTP cache /mcp endpoint without credentials/query"
        uri.AbsoluteUri
    let clone (source:IDictionary<string,obj>)=
        let result=Dictionary<string,obj>(StringComparer.Ordinal)
        if not(isNull source) then for KeyValue(key,value) in source do result.Add(key,value)
        result
    let objectMap (value:obj)=
        match value with
        | :? IDictionary<string,obj> as fields -> fields
        | _ -> invalidOp "MCP configuration must be an object dictionary"

    /// 例：merge(cacheUrl, existingConfig) 僅加入/啟用cache。回新字典，不修改caller資料。
    /// root main 是否取得 fodex_order_hub 由既有 factory 決定，此處不授權其他 native。
    let codex (url,configuration:IDictionary<string,obj>)=
        try
            let url=endpoint url
            let result=clone configuration
            let servers=match result.TryGetValue "mcp_servers" with true,value -> clone(objectMap value) | _ -> clone null
            let server=
                match servers.TryGetValue name with
                | true,value ->
                    let fields=clone(objectMap value)
                    if fields.ContainsKey "command" then invalidOp "Cache MCP name is already owned by a stdio server"
                    match fields.TryGetValue "url" with
                    | true,(:? string as previous) when endpoint previous=url -> fields
                    | _ -> invalidOp "Cache MCP name is already bound to a different endpoint"
                | _ -> clone null
            server["url"]<-url
            server["enabled"]<-true
            servers[name]<-server
            result["mcp_servers"]<-servers
            Ok(result :> IDictionary<string,obj>)
        with error -> Error error

    /// 實測 agy mcp list 是四欄表格，沒有 --json。未知格式不能被當成『沒有設定』。
    /// 其他 server 的 command/URL 只留在記憶體，不寫進 log 或錯誤訊息。
    let agyPlan (url,text:string)=
        try
            let expected=endpoint url
            let lines=text.Split '\n' |> Array.map(fun line -> line.Trim()) |> Array.filter(String.IsNullOrWhiteSpace >> not)
            if lines.Length=0 || Regex.Split(lines[0],"\\s+")<>[|"NAME";"TYPE";"STATUS";"COMMAND/URL"|] then invalidOp "Unsupported AGY MCP listing format"
            let matches=ResizeArray<string array>()
            for line in lines |> Array.skip 1 do
                let columns=Regex.Split(line,"\\s+",RegexOptions.None,TimeSpan.FromSeconds 1.)
                if columns.Length<4 then invalidOp "Malformed AGY MCP listing row"
                if columns[0]=name then matches.Add columns
            if matches.Count>1 then invalidOp "Duplicate fixed cache MCP name"
            if matches.Count=0 then Ok Missing else
            let row=matches[0]
            if row[1]<>"http" || row.Length<>4 || endpoint row[3]<>expected then invalidOp "AGY cache MCP name conflicts with an existing endpoint"
            match row[2] with
            | "enabled" -> Ok Enabled
            | "disabled" -> Ok Disabled
            | _ -> invalidOp "Unknown AGY cache MCP state"
        with error -> Error error

    /// Fodex 多程序共同鎖住『檢查→add/enable→重查』。不在 async workflow 使用有 thread affinity 的 Mutex。
    /// 空 lock file 是產品的機器級資源標記，不保存設定、token或業務資料，也不刪除以免產生 inode race。
    let acquireConfigurationLock executable (ct:CancellationToken)=task {
        let root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Fodex","locks")
        Directory.CreateDirectory root |> ignore
        let identity=Path.GetFullPath(executable).ToUpperInvariant()+"|"+Environment.UserName+"|"+name
        let path=Path.Combine(root,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes identity))+".lock")
        let mutable lease:FileStream option=None
        while lease.IsNone do
            ct.ThrowIfCancellationRequested()
            try lease<-Some(new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            with :? IOException -> do! Task.Delay(50,ct)
        return lease.Value }

    /// 成功之前核對 dedicated cache health+tools。add 的 flags 在 name 前，符合目前 AGY CLI。
    /// 同名異URL拒絕、不覆蓋；任一步失敗回Error，下一次先重讀CLI現況，沒有盲目重送add。
    let ensureAgy ((node,ctx,executable,directory,url,ct):NativeAgyCacheInputV100)=task {
        try
            let url=endpoint url
            let verify=FodexFunBinding.pin<string,Task<Result<unit,exn>>> node (Some "1.0.0") "SharedCache.CheckEndpoint"
            let! verified=FodexFunBinding.invokeAsync verify url
            required verified
            use timeout=CancellationTokenSource.CreateLinkedTokenSource ct
            timeout.CancelAfter(TimeSpan.FromSeconds 30.)
            use! lease=acquireConfigurationLock executable timeout.Token
            let capture=FodexFunBinding.pin<NativeCommandInputV100,Task<Result<int*string*string,exn>>> node (Some "1.0.0") "NativeCommand.Capture"
            let plan=FodexFunBinding.pin<string*string,AgyCacheRegistrationV100> node (Some "1.0.0") targetPlan
            let run args=task {
                let! result=FodexFunBinding.invokeAsync capture (node,ctx,executable,directory,args,TimeSpan.FromSeconds 20.,timeout.Token)
                let code,stdout,_=required result
                if code<>0 then invalidOp(sprintf "AGY MCP management exited with code %d" code)
                return stdout }
            let! listing=run ["mcp";"list"]
            let state=plan(url,listing) |> required
            match state with
            | Missing ->
                let! _=run ["mcp";"add";"--type";"http";name;url]
                let! _=run ["mcp";"enable";name]
                ()
            | Disabled -> let! _=run ["mcp";"enable";name] in ()
            | Enabled -> ()
            let! current=run ["mcp";"list"]
            if (plan(url,current) |> required)<>Enabled then invalidOp "AGY cache MCP registration not confirmed"
            return Ok()
        with error -> return Error error }

    let codexQuotation:Expr<string*IDictionary<string,obj> -> FeasibleT<_,IDictionary<string,obj>,exn>> = <@ fun _ -> fun input -> codex input @>
    let planQuotation:Expr<string*string -> FeasibleT<_,AgyCacheRegistrationV100,exn>> = <@ fun _ -> fun input -> agyPlan input @>
    let agyQuotation:Expr<NativeAgyCacheInputV100 -> FeasibleT<_,Task<Result<unit,exn>>,exn>> = <@ fun _ -> fun input -> Ok(ensureAgy input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetCodex version codexQuotation
                       FodexFunRegistry.declare targetPlan version planQuotation
                       FodexFunRegistry.declare targetAgy version agyQuotation|]
