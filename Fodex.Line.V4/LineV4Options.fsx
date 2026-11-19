#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Options
#endif
open System
open System.IO
open Argu
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open LoggerUtil

type Argument =
    | Line_Account of string | Akkaport of int | Line_Conversation of int64
    | Line_Catalog of string | Bootstrap_Health_Url of string | Cluster_Host of string
    | Data_Directory of string | Bindings of string | Allowed_Line_Reply_User_Id of string
    | Cache_Url of string | Sql_Server of string | Database of string
    | Working_Directory of string | Output_Directory of string | Shutdown_Seconds of int
    | Mainagent_Type of string | Subagent_Type of string | Subagent_Mode of string
    | Model of string | Effort of string | Native_Adapter of string | Agy_Exe of string
    | Sub_Codex_Model of string | Sub_Codex_Effort of string
    | Sub_Agy_Model of string | Sub_Agy_Effort of string
    | Fast of bool | Memory of bool | Labels of bool | Json
    interface IArgParserTemplate with
        member _.Usage="Shared CLI/LINE v4 host; deployment identity and paths are startup configuration."

let parser=ArgumentParser.Create<Argument>(programName="Fodex.Line.V4")

/// FSI users edit this text. The executable parses argv only, with sparse defaults.
let defaultArgumentsText="""
--line-account d9System_test2 --akkaport 15979 --cluster-host 127.0.0.1
--bootstrap-health-url http://127.0.0.1:8799/resource-node/healthz
--line-catalog G:/PulseTrade.fs/deployment/services/prod/PulseTradeCommResourceNode/config/line-catalog.json
--data-directory G:/PulseTrade.fs/deployment/process/dev/Fodex.Line.V4/shared-cli-line/state
--bindings G:/PulseTrade.fs/deployment/process/dev/Fodex.Line.V4/shared-cli-line/config/ordered-bindings.json
--allowed-line-reply-user-id U8075ab9e46cfe73a095ee1340d5fbd61
--cache-url http://127.0.0.1:18825/mcp --sql-server localhost --database FodexM018
--working-directory G:/PulseTrade.fs
--output-directory G:/PulseTrade.fs/deployment/process/dev/Fodex.Line.V4/shared-cli-line/output --shutdown-seconds 30
--mainagent-type agy --subagent-type codex --subagent-mode wrapper --labels true
"""
let defaults=parser.ParseCommandLine(global.PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" ")))
let resolvePath (value:string)=Path.GetFullPath(if Path.IsPathRooted value then value else Path.Combine(AppContext.BaseDirectory,value))
let choose (parsed:ParseResults<Argument>) (selector:Quotations.Expr<'T -> Argument>)=parsed.TryGetResult selector |> Option.orElseWith(fun () -> defaults.TryGetResult selector) |> Option.get

let nativeImageRoot parsed=Path.Combine(choose parsed <@ Data_Directory @> |> resolvePath,"native-images")
/// V107 already reads this process-local, non-secret configuration. One v4
/// process owns its service; derive it from the same explicit data directory.
let withNativeImageRoot parsed action =
    let name="FODEX_LINE_IMAGE_ROOT"
    let previous=Environment.GetEnvironmentVariable name
    let expected=nativeImageRoot parsed
    if not(String.IsNullOrWhiteSpace previous) &&
       not(String.Equals(Path.GetFullPath previous,expected,StringComparison.OrdinalIgnoreCase)) then
        invalidOp "FODEX_LINE_IMAGE_ROOT must match the configured data-directory/native-images"
    Environment.SetEnvironmentVariable(name,expected,EnvironmentVariableTarget.Process)
    try action()
    finally Environment.SetEnvironmentVariable(name,previous,EnvironmentVariableTarget.Process)

/// Map typed common options back through the existing policy, retaining provider defaults
/// and quoting semantics. No alternate argument grammar or FSI startup runs here.
let common (ctx:UniContext) (parsed:ParseResults<Argument>) =
    let convert = function
        | Sql_Server value -> Some(ConsoleArgumentV405.Sql_Server value)
        | Database value -> Some(ConsoleArgumentV405.Database value)
        | Working_Directory value -> Some(ConsoleArgumentV405.Working_Directory(Path.GetFullPath value))
        | Output_Directory value -> Some(ConsoleArgumentV405.Output_Directory(resolvePath value))
        | Shutdown_Seconds value -> Some(ConsoleArgumentV405.Shutdown_Seconds value)
        | Mainagent_Type value -> Some(ConsoleArgumentV405.Mainagent_Type value)
        | Subagent_Type value -> Some(ConsoleArgumentV405.Subagent_Type value)
        | Subagent_Mode value -> Some(ConsoleArgumentV405.Subagent_Mode value)
        | Model value -> Some(ConsoleArgumentV405.Model value)
        | Effort value -> Some(ConsoleArgumentV405.Effort value)
        | Native_Adapter value -> Some(ConsoleArgumentV405.Native_Adapter value)
        | Agy_Exe value -> Some(ConsoleArgumentV405.Agy_Exe value)
        | Sub_Codex_Model value -> Some(ConsoleArgumentV405.Sub_Codex_Model value)
        | Sub_Codex_Effort value -> Some(ConsoleArgumentV405.Sub_Codex_Effort value)
        | Sub_Agy_Model value -> Some(ConsoleArgumentV405.Sub_Agy_Model value)
        | Sub_Agy_Effort value -> Some(ConsoleArgumentV405.Sub_Agy_Effort value)
        | Fast value -> Some(ConsoleArgumentV405.Fast value)
        | Memory value -> Some(ConsoleArgumentV405.Memory value)
        | Labels value -> Some(ConsoleArgumentV405.Labels value)
        | Json -> Some ConsoleArgumentV405.Json
        | _ -> None
    let values=parsed.GetAllResults() |> List.choose convert
    let fallback=defaults.GetAllResults() |> List.choose convert
    let parse=FodexFunctions.pinBase<_,ParseResults<ConsoleArgumentV405>> "4.0.5" "Console.ParseStartup"
    parse(ctx,ConsolePolicyFodexFunV405.defaults,ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments fallback)
    |> Result.bind(fun settings ->
        let defaultText=ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments(settings.GetAllResults()) |> ConsolePolicyFodexFunV410.quoteTokens
        parse(ctx,defaultText,ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments values))

let connectionString (options:ParseResults<ConsoleArgumentV405>) =
    let builder=SqlConnectionStringBuilder()
    builder.DataSource<-options.GetResult ConsoleArgumentV405.Sql_Server
    builder.InitialCatalog<-options.GetResult ConsoleArgumentV405.Database
    builder.IntegratedSecurity<-true
    builder.TrustServerCertificate<-true
    builder.Pooling<-false
    builder.ConnectTimeout<-5
    builder.ConnectionString
