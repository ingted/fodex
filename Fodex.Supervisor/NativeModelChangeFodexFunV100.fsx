#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Invocation resources：node/ctx、實際native參數、typed SID、busy、完整舊設定、要求的pair、取消token。
/// 回傳已驗證的catalog/設定/參數；此q完全不取消工作、不修改actor、不寫DB。
type NativeModelChangeInputV100=FodexNode*UniContext*string*AgentSessionId option*bool*NativeSettingsV300*string*string*CancellationToken
type NativeModelChangeOutputV100=JsonElement array*NativeSettingsV300*string

/// 僅owner對本機native的控制訊息；catalog已由PrepareChange查驗。
/// 一般native必須先idle；raw native在自己的mailbox保留原order completion，再取消/套用/續作。
type NativeModelApplyV304 =
    | SetValidatedNativePair of JsonElement array*NativeSettingsV300*(Result<unit,exn> -> unit)

module NativeModelChangeFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="NativeModel.PrepareChange"
    let required=function Ok value -> value | Error(error:exn) -> raise error

    /// 先辨認provider與檢查SID，再查Fresh目錄；不會為了改設定建立session。
    /// 例：AGY首輪active/None在呼叫目錄前直接Error，既有工作保持原樣。
    /// old/low -> new/high以最終pair一次驗證，沒有new/low的錯誤中間態。
    let prepare ((node,ctx,arguments,sid,busy,current,model,effort,ct):NativeModelChangeInputV100)=task {
        try
            ct.ThrowIfCancellationRequested()
            let options=SupervisorArgs.parseText ctx arguments |> required
            let provider,executable=
                match options.GetResult(SupervisorArgument.Native_Adapter,defaultValue="app-server") with
                | "agy" -> "agy",options.GetResult(SupervisorArgument.Agy_Exe,defaultValue=NativeAgyModelCatalogFodexFunV300.defaultExecutable())
                | "exec" | "app-server" | "codex" -> "codex",options.GetResult(SupervisorArgument.Codex_Exe,defaultValue=NativeAgentV200.executable())
                | _ -> invalidArg "arguments" "Participant arguments contain an unknown native adapter"
            let checkSession=FodexFunBinding.pin<NativeModelSessionCheckInputV100,unit> node (Some "1.0.0") NativePreflightFodexFunV100.targetSession
            checkSession(provider,sid,busy) |> required
            let proposed={current with Model=model;Effort=effort}
            let directory=options.GetResult(SupervisorArgument.Working_Directory,defaultValue=Environment.CurrentDirectory) |> Path.GetFullPath
            let query=FodexFunBinding.pin<NativePreflightInputV100,Task<Result<JsonElement array,exn>>> node (Some "1.0.0") NativePreflightFodexFunV100.target
            let! result=FodexFunBinding.invokeAsync query (node,ctx,(provider,executable,directory),proposed,false,ct)
            let catalog=result |> required
            // 只替換native的model/effort；SID、adapter、permission與contract operation版本保留。
            // Argu/PL處理字串邊界，不以字串replace或shell拼接改參數。
            let native=options.GetAllResults() |> List.filter(function SupervisorArgument.Model _ | SupervisorArgument.Effort _ -> false | _ -> true)
            let versions=FodexCommanddHelper.ParseStr ctx arguments
            let versionText=FodexCommanddHelper.Parser.PrintCommandLineArguments(versions.GetAllResults()) |> Array.map JsonSerializer.Serialize<string> |> String.concat " "
            let updated=SupervisorArgs.format(native@[SupervisorArgument.Model model;SupervisorArgument.Effort effort])+
                        sprintf " --fast %b --memory %b " proposed.Fast proposed.Memory+versionText
            return Ok(catalog,proposed,updated)
        with error -> return Error error }

    let quotation:Expr<NativeModelChangeInputV100 -> FeasibleT<_,Task<Result<NativeModelChangeOutputV100,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(prepare input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
