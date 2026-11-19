module LineV4CliInfo

open System
open System.Text.RegularExpressions
open Argu
open LoggerUtil
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

type Kind = All | Accepted | Wrapper | Command | Status
type Target = EveryParticipant | InWrapper of Guid*ParticipantSelectorV300
type Rule = { Target:Target; Kind:Kind; Enabled:bool }
type Argument =
    | [<CustomCommandLine("--cliinfo")>] CliInfo of participant:string * kind:string * enabled:string
    interface IArgParserTemplate with
        member _.Usage="/cliinfo(ALL|N|i..j|i,j) ALL|accepted|wrapper|command|status on|off"
let parser=ArgumentParser.Create<Argument>(programName="/cliinfo")
let kindName = function All -> "ALL" | Accepted -> "accepted" | Wrapper -> "wrapper" | Command -> "command" | Status -> "status"
let selectorText = function
    | ParticipantSelectorV300.All -> "ALL"
    | ParticipantSelectorV300.Number n -> string n
    | ParticipantSelectorV300.Range(first,last) -> sprintf "%d..%d" first last
    | ParticipantSelectorV300.Numbers values -> values |> List.map string |> String.concat ","
    | ParticipantSelectorV300.Main -> "main"
let parse (ctx:UniContext,text:string) =
    try
        let text=text.Trim()
        if not(text.StartsWith("/cliinfo",StringComparison.OrdinalIgnoreCase)) then Ok None else
        let matched=Regex.Match(text,@"^/cliinfo\(([^)]+)\)([\s\S]*)$",RegexOptions.IgnoreCase)
        if not matched.Success then invalidArg "input" (parser.PrintUsage())
        let tail=matched.Groups[2].Value
        let tail=if Regex.IsMatch(tail,@"^\s*(?:on|off)\s*$",RegexOptions.IgnoreCase) then " ALL "+tail else tail
        let quote=ConsolePolicyFodexFunV410.quoteTokens [|matched.Groups[1].Value|]
        let argv=global.PL.parseLine [|' ';'\t'|] (Some '"') None true ("--cliinfo "+quote+" "+tail)
        let parsed=parser.ParseCommandLine(argv,raiseOnUsage=false)
        if parsed.IsUsageRequested then invalidArg "input" (parser.PrintUsage())
        let participant,kind,mode=parsed.GetResult(<@ CliInfo @>)
        let kind=match kind.ToLowerInvariant() with
                 | "all" -> All | "accepted" -> Accepted | "wrapper" -> Wrapper | "command" -> Command | "status" -> Status
                 | _ -> invalidArg "kind" "Use ALL, accepted, wrapper, command, or status"
        let enabled=match mode.ToLowerInvariant() with "on" -> true | "off" -> false | _ -> invalidArg "mode" "Use on or off"
        let select=FodexFunctions.pinBase<UniContext*string,WrapperCommandV300> "4.0.0" WrapperCommandFodexFunV300.target
        let selector=match select(ctx,"/show("+participant+") status") |> Result.defaultWith raise with
                     | WrapperCommandV300.ShowOutput(ParticipantSelectorV300.Main,_) -> invalidArg "participant" "Use ALL or event source N; wrapper notices use N=1"
                     | WrapperCommandV300.ShowOutput(value,_) -> value
                     | _ -> invalidArg "participant" "Expected an existing participant selector"
        Ok(Some(selector,kind,enabled))
    with error -> Error error

let matches number = function
    | ParticipantSelectorV300.All -> true
    | ParticipantSelectorV300.Number n -> n=number
    | ParticipantSelectorV300.Range(first,last) -> first<=number && number<=last
    | ParticipantSelectorV300.Numbers values -> List.contains number values
    | ParticipantSelectorV300.Main -> false
let enabled rules wrapper number kind =
    rules |> List.tryPick(fun rule ->
        let target=match rule.Target,wrapper with
                   | EveryParticipant,_ -> true
                   | InWrapper(sid,selector),Some actual -> actual=sid && matches number selector
                   | _ -> false
        if target && (rule.Kind=All || rule.Kind=kind) then Some rule.Enabled else None)
    |> Option.defaultValue true
let update rules target kind enabled =
    let remaining=if target=EveryParticipant && kind=All then [] else rules |> List.filter(fun rule -> rule.Target<>target || rule.Kind<>kind)
    if remaining.Length>=128 then invalidOp "CLI info has 128 rules; use /cliinfo(ALL) ALL on or off to reset"
    {Target=target;Kind=kind;Enabled=enabled}::remaining
/// Answers, errors, /show and /labels stay independent.
let eventKind = function
    | StdOut(_,_,text) when text.StartsWith("Prompt accepted",StringComparison.Ordinal) -> Some Accepted
    | StdOut _ -> Some Wrapper
    | _ -> None
