namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Argu
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Native actor 保存下一個新 turn 的設定；執行中的 turn 已拿到自己的不可變快照。
type NativeSettingsV300 = { Model: string; Effort: string; Fast: bool; Memory: bool }

type NativeSpeedArgumentV300 =
    | Fast of bool
    | Memory of bool
    interface IArgParserTemplate with member _.Usage = "Native options: fast defaults to false; memory defaults to true"

/// 僅限同 node 的 owner -> IAgent.Send；remote transport 不能序列化這些 callback。
type NativeControlV300 =
    | ReadNativeSettings of (Result<NativeSettingsV300 * bool,exn> -> unit)
    | SetNativeSettings of NativeSettingsV300 * (Result<unit,exn> -> unit)
    | CompactNative of (Result<unit,exn> -> unit)
    | NativeCompactCompleted of generation:Guid * Result<unit,exn>
    | InterruptDeadline of incarnation: Guid * turnId: string

module NativeSettingsFodexFunV300 =
    let version = System.Version(3,0,0)
    let targetCheck = "NativeSettings.Check"
    let targetExec = "NativeSettings.ExecArguments"
    let targetTurn = "NativeSettings.TurnParameters"
    let targetMemory = "NativeSettings.MemoryArguments"
    let targetReload = "NativeSettings.ReloadMemory"

    let fromArguments (ctx: UniContext) text =
        SupervisorArgs.parseText ctx text |> Result.bind (fun options ->
            try
                let parser = ArgumentParser.Create<NativeSpeedArgumentV300>("Fodex native speed")
                let parsed = parser.ParseCommandLine(PL.parseLine ctx [|' '|] (Some '"') None true text,ignoreUnrecognized=true,raiseOnUsage=false)
                Ok {Model=options.GetResult Model;Effort=options.GetResult Effort
                    Fast=parsed.GetResult(Fast,defaultValue=false);Memory=parsed.GetResult(Memory,defaultValue=true)}
            with ex -> Error ex)

    /// 例：check(models,current,SettingChangeV300.Memory(Some false)) 只更改 Memory。
    /// Model/effort/tier 是一組相容設定；換 model 後不默默改另外兩個欄位。
    let check (models: JsonElement array, current: NativeSettingsV300, change: SettingChangeV300) : Result<NativeSettingsV300,exn> =
        try
            let proposed =
                match change with
                | SettingChangeV300.Model value -> {current with Model=value}
                | SettingChangeV300.Effort value -> {current with Effort=value}
                | SettingChangeV300.Fast value -> {current with Fast=Option.defaultValue (not current.Fast) value}
                | SettingChangeV300.Memory value -> {current with Memory=Option.defaultValue (not current.Memory) value}
            let found = models |> Array.tryFind (fun model ->
                model.GetProperty("model").GetString()=proposed.Model || model.GetProperty("id").GetString()=proposed.Model)
            let model = found |> Option.defaultWith (fun () -> invalidArg "model" ("Model is not in the current Codex catalog: " + proposed.Model))
            let efforts = model.GetProperty("supportedReasoningEfforts").EnumerateArray() |> Seq.map (fun value -> value.GetProperty("reasoningEffort").GetString()) |> Set.ofSeq
            if not(efforts.Contains proposed.Effort) then invalidArg "effort" (sprintf "Model %s does not advertise effort %s" proposed.Model proposed.Effort)
            if proposed.Fast then
                let supported =
                    match model.TryGetProperty "serviceTiers" with
                    | true,tiers -> tiers.EnumerateArray() |> Seq.exists (fun tier -> tier.GetProperty("id").GetString()="fast")
                    | _ -> false
                if not supported then invalidArg "fast" ("Model does not advertise fast service tier: " + proposed.Model)
            Ok proposed
        with ex -> Error ex

    /// Memory 控制本 participant 的 Codex process：功能、讀取與背景生成一起開關。
    /// 例：memoryArguments false -> -c features.memories=false ...；不寫使用者 config.toml。
    /// 關閉不刪既有記憶，也不撤回過去已保存的 generation inputs 或其他 process 的工作。
    /// app-server 必須重新載入原 sid；不能把這些欄位塞進不支援它們的 turn/start。
    let memoryArguments (enabled: bool) : Result<string list,exn> =
        let value = if enabled then "true" else "false"
        Ok ["-c";"features.memories="+value
            "-c";"memories.use_memories="+value
            "-c";"memories.generate_memories="+value]

    /// 由持有 process 的 actor 在 idle 邊界呼叫，load 必須停止原 process 再 resume 原 sid。
    /// 例：on -> off 載入失敗時再 load on；仍回傳第一次失敗，讓呼叫端知道 off 未生效。
    /// callback 只在同一 actor 執行，不跨 node、不進 graph；測試可驗證復原順序而不啟動模型。
    let reloadMemory (current: NativeSettingsV300, proposed: NativeSettingsV300, load: NativeSettingsV300 -> Result<unit,exn>) : Result<unit,exn> =
        let attempt value = try load value with ex -> Error ex
        match attempt proposed with
        | Ok () -> Ok ()
        | Error changeError ->
            match attempt current with
            | Ok () -> Error changeError
            | Error restoreError -> Error(AggregateException("Memory reload and restoration both failed; session id retained",[|changeError;restoreError|]) :> exn)

    /// Fast 與 reasoning effort 獨立。明確 default 避免繼承使用者全域 fast 設定。
    /// argv 是給 ProcStartSpec 的字串陣列，不是拼 shell command；stdin prompt 仍由 ProcLocalIo 傳入。
    let execArguments (settings: NativeSettingsV300, sandbox: string, sid: string option) : Result<string list,exn> =
        memoryArguments settings.Memory |> Result.map (fun memory ->
            memory @ ["-c";"service_tier=" + JsonSerializer.Serialize(if settings.Fast then "fast" else "default")]
            @ NativeExecV208.arguments settings.Model settings.Effort sandbox sid)

    /// 只有 turn/start 可帶這些覆寫；turn/steer 不支援換 model／effort／serviceTier。
    let turnParameters (settings: NativeSettingsV300, sid: string, prompt: string) : Result<IDictionary<string,obj>,exn> =
        Ok(dict ["threadId",box sid; "input",box [| {| ``type``="text";text=prompt |} |]
                 "model",box settings.Model; "effort",box settings.Effort
                 "serviceTier",box(if settings.Fast then "fast" else "default")])

    let checkQuotation : Expr<(JsonElement array * NativeSettingsV300 * SettingChangeV300) -> FeasibleT<_,NativeSettingsV300,exn>> =
        <@ fun _ -> fun input -> check input @>
    let execQuotation : Expr<(NativeSettingsV300 * string * string option) -> FeasibleT<_,string list,exn>> =
        <@ fun _ -> fun input -> execArguments input @>
    let turnQuotation : Expr<(NativeSettingsV300 * string * string) -> FeasibleT<_,IDictionary<string,obj>,exn>> =
        <@ fun _ -> fun input -> turnParameters input @>
    let memoryQuotation : Expr<bool -> FeasibleT<bool,string list,exn>> =
        <@ fun _ -> fun input -> memoryArguments input @>
    let reloadQuotation : Expr<(NativeSettingsV300 * NativeSettingsV300 * (NativeSettingsV300 -> Result<unit,exn>)) -> FeasibleT<_,unit,exn>> =
        <@ fun _ -> fun input -> reloadMemory input @>
    let register node =
        FodexFunRegistry.register node targetCheck version checkQuotation
        FodexFunRegistry.register node targetExec version execQuotation
        FodexFunRegistry.register node targetTurn version turnQuotation
        FodexFunRegistry.register node targetMemory version memoryQuotation
        FodexFunRegistry.register node targetReload version reloadQuotation

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetCheck version checkQuotation
            FodexFunRegistry.declare targetExec version execQuotation
            FodexFunRegistry.declare targetTurn version turnQuotation
            FodexFunRegistry.declare targetMemory version memoryQuotation
            FodexFunRegistry.declare targetReload version reloadQuotation
        |]
