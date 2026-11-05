namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// 支援 agy CLI 模型目錄查詢
module NativeAgyModelCatalogFodexFunV300 =
    let version = System.Version(3,0,0)
    let target = "NativeAgySettings.ModelCatalog"

    let defaultExecutable () =
        let custom = Environment.GetEnvironmentVariable "AGY_EXE"
        if not (String.IsNullOrWhiteSpace custom) && File.Exists custom then custom
        else
            let localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe")
            if File.Exists localApp then localApp
            else "agy.exe"

    let parseModelsOutput (text: string) : JsonElement array =
        let lines = text.Split([|'\r'; '\n'|], StringSplitOptions.RemoveEmptyEntries)
        let list = ResizeArray<JsonElement>()
        for line in lines do
            let trimmed = line.Trim()
            if not (trimmed.StartsWith("Fetching", StringComparison.OrdinalIgnoreCase)) && not (String.IsNullOrWhiteSpace trimmed) then
                let parts = trimmed.Split('\t')
                let id = parts[0].Trim()
                let name = if parts.Length > 1 then parts[1].Trim() else id
                let json = sprintf """{"id":"%s","model":"%s","displayName":"%s","supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"medium"},{"reasoningEffort":"low"}],"serviceTiers":[]}""" id id name
                use doc = JsonDocument.Parse json
                list.Add (doc.RootElement.Clone())
        if list.Count = 0 then
            // Fallback default models if catalog empty
            for id in [ "gemini-3.8-flash-high"; "gemini-3.8-flash"; "gemini-3.8-pro"; "claude-sonnet-4-6"; "claude-opus-4-6-thinking"; "gpt-oss-120b-medium" ] do
                let json = sprintf """{"id":"%s","model":"%s","displayName":"%s","supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"medium"},{"reasoningEffort":"low"}],"serviceTiers":[]}""" id id id
                use doc = JsonDocument.Parse json
                list.Add (doc.RootElement.Clone())
        list.ToArray()

    let load (node: FodexNode) (executable: string) (directory: string) : Result<JsonElement array, exn> =
        try
            let psi = ProcessStartInfo()
            let exe = if String.IsNullOrWhiteSpace executable then defaultExecutable() else executable
            psi.FileName <- exe
            psi.Arguments <- "models"
            psi.WorkingDirectory <- if String.IsNullOrWhiteSpace directory then Environment.CurrentDirectory else directory
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = Process.Start psi
            let stdout = proc.StandardOutput.ReadToEnd()
            let _stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()
            if proc.ExitCode = 0 then
                Ok (parseModelsOutput stdout)
            else
                Ok (parseModelsOutput "")
        with _ex ->
            Ok (parseModelsOutput "")

    let quotation : Expr<string -> FeasibleT<_,JsonElement array,exn>> =
        <@ fun _ -> fun executable -> load Unchecked.defaultof<_> executable Environment.CurrentDirectory @>
    let register node = FodexFunRegistry.register node target version quotation

    [<AutoOpen>]
    module Definitions =
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
