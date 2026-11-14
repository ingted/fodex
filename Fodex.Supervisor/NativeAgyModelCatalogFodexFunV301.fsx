#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// IO resource 隨 invocation 傳入；quotation 不捕捉 node／executable／CancellationToken。
type NativeCatalogQueryInputV301=FodexNode*UniContext*string*string*CancellationToken

module NativeAgyModelCatalogFodexFunV301 =
    let version=System.Version(3,0,1)
    let target="NativeAgySettings.ModelCatalog"
    let targetParse="NativeAgySettings.ParseModels"
    let required=function Ok value -> value | Error(error:exn) -> raise error

    /// agy models 實測為 id<TAB>displayName，不是 JSON。不能替 TSV 猜每個模型的 effort。
    /// 例：gemini-3.8-flash-high\tGemini 3.8 Flash (High)；空／錯誤／重複資料皆拒絕。
    let parse (text:string)=
        try
            if isNull text then nullArg "text"
            let found=ResizeArray<JsonElement>()
            let ids=HashSet<string>(StringComparer.Ordinal)
            for raw in text.Split '\n' do
                let line=raw.TrimEnd '\r'
                if not(String.IsNullOrWhiteSpace line) && not(line.StartsWith("Fetching models",StringComparison.OrdinalIgnoreCase)) then
                    let columns=line.Split '\t'
                    let header=columns.Length=2 && (columns[0].Equals("id",StringComparison.OrdinalIgnoreCase) || columns[0].Equals("model",StringComparison.OrdinalIgnoreCase)) && columns[1].Equals("name",StringComparison.OrdinalIgnoreCase)
                    if not header then
                        if columns.Length<>2 || not(Regex.IsMatch(columns[0],"^[A-Za-z0-9][A-Za-z0-9._:/-]*$")) || String.IsNullOrWhiteSpace columns[1] then invalidOp "Malformed agy models TSV row"
                        if not(ids.Add columns[0]) then invalidOp "Duplicate agy model id"
                        found.Add(JsonSerializer.SerializeToElement {|id=columns[0];model=columns[0];displayName=columns[1];provider="agy"|})
            if found.Count=0 then invalidOp "Agy returned an empty model catalog"
            Ok(found.ToArray())
        with error -> Error error

    /// 只跑 CLI 管理命令；由 Proc Supervisor 控制 timeout／stdio／退出，沒有模型生成。
    let load ((node,ctx,executable,directory,ct):NativeCatalogQueryInputV301)=task {
        try
            let capture=FodexFunBinding.pin<NativeCommandInputV100,Task<Result<int*string*string,exn>>> node (Some "1.0.0") "NativeCommand.Capture"
            let! captured=FodexFunBinding.invokeAsync capture (node,ctx,executable,directory,["models"],TimeSpan.FromSeconds 30.,ct)
            let code,stdout,_=required captured
            if code<>0 then invalidOp(sprintf "agy models exited with code %d" code)
            let parse=FodexFunBinding.pin<string,JsonElement array> node (Some "3.0.1") targetParse
            return parse stdout
        with error -> return Error error }

    let parseQuotation:Expr<string -> FeasibleT<_,JsonElement array,exn>> = <@ fun _ -> fun text -> parse text @>
    let quotation:Expr<NativeCatalogQueryInputV301 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> = <@ fun _ -> fun input -> Ok(load input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation
                       FodexFunRegistry.declare targetParse version parseQuotation|]
