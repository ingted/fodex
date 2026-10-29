namespace Fodex.Supervisor

open System
open System.Text.Json
open Argu
open LoggerUtil
open Akka.Event

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
    let context (node: Akka.Actor.ActorSystem) =
        let log = Logging.GetLogger(node, "Fodex")
        let logger =
            { new UniLogger() with
                member _.Info text = log.Info text
                member _.Debug text = log.Debug text
                member _.Warn text = log.Warning text
                member _.Error text = log.Error text
                member _.infof f = Printf.kprintf (fun text -> log.Info text) f
                member _.debugf f = Printf.kprintf (fun text -> log.Debug text) f
                member _.warnf f = Printf.kprintf (fun text -> log.Warning text) f
                member _.errorf f = Printf.kprintf (fun text -> log.Error text) f
                member _.pInfof f = Printf.kprintf (fun text -> log.Info text) f
                member _.pDebugf f = Printf.kprintf (fun text -> log.Debug text) f
                member _.pWarnf f = Printf.kprintf (fun text -> log.Warning text) f
                member _.pErrorf f = Printf.kprintf (fun text -> log.Error text) f
                member _.fInfof f = Printf.kprintf (fun text -> raise (InvalidOperationException text)) f
                member _.fDebugf f = Printf.kprintf (fun text -> raise (InvalidOperationException text)) f
                member _.fWarnf f = Printf.kprintf (fun text -> raise (InvalidOperationException text)) f
                member _.fErrorf f = Printf.kprintf (fun text -> raise (InvalidOperationException text)) f
                member _.pfErrorf f = Printf.kprintf (fun text -> raise (InvalidOperationException text)) f }
        { logger = logger; usage = [ "Fodex" ]; caller = [] }
