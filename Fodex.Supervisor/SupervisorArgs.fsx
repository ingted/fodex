namespace Fodex.Supervisor

open System
open System.Text.Json
open Argu
open LoggerUtil
open FodexLoggerAdapter

type SupervisorArgument =
    | Model of string
    | Effort of string
    | Working_Directory of string
    | Native_Adapter of string
    | Resume_Session of string
    | Codex_Exe of string
    | Sandbox of string
    | Prompt of string
    | Role of string
    interface IArgParserTemplate with
        member _.Usage = "v1.0.0 Supervisor option"

module SupervisorArgs =
    let parser = ArgumentParser.Create<SupervisorArgument>("Fodex")
    let format options =
        parser.PrintCommandLineArguments options
        |> Array.map (fun argument -> JsonSerializer.Serialize<string> argument)
        |> String.concat " "
    let parse args =
        try Ok(parser.ParseCommandLine(args, ignoreUnrecognized = true, raiseOnUsage = false))
        with ex -> Result.Error ex
    let parseText (ctx: UniContext) text =
        PL.parseLine ctx [| ' ' |] (Some '"') None true text |> parse
    /// 舊版本的呼叫入口保留；不是另一個 logger adapter。
    /// 新組裝點可直接指定 NLog.WithProperty("traceFile", path)，再使用 toUL/toCtx。
    let context (node: Akka.Actor.ActorSystem) =
        let logger = UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex." + node.Name)) |> FodexLoggerAdapter.toUL
        UniLogger.toCtx(logger, "Fodex")
