#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Host 的獨立 endpoint 選項；Mode 回到 V405 的單參數語法。
/// 不改既有 wrapper/native 的設定型別，避免 CLI 參數擴散到 participant contract。
type EndpointArgumentV407 =
    | [<Unique>] Mcpport of int
    | [<Unique>] Akkaport of int
    | [<Unique>] Akkaname of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Mcpport _ -> "MCP HTTP port, 1..65535."
            | Akkaport _ -> "Akka node port, 0..65535; 0 lets the OS allocate a port."
            | Akkaname _ -> "ActorSystem name; omit to retain the generated unique name."

/// 第二個值只屬於 host 的 ActorSystem；不是 wrapper/native 的 SID 或 participant name。
type ConsoleStartupV407 = ParseResults<ConsoleArgumentV405> * string option

module ConsolePolicyFodexFunV407 =
    let version=System.Version(4,0,7)
    let targetParse=ConsolePolicyFodexFunV405.targetParse
    let endpointParser=ArgumentParser.Create<EndpointArgumentV407>(programName="Fodex.Supervisor")

    /// Argu 負責型別與 token 邊界；未在 endpoint template 出現的選項交完整 V405 template。
    /// 例：--mode mcp --mcpport 18824 --akkaport 21824 --akkaname fodex-service。
    let normalize strict fallbackUrl (argv:string array) =
        let endpoints=endpointParser.ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)
        let remaining=Array.append (endpoints.UnrecognizedCliParams |> List.toArray)
                                   (if endpoints.IsUsageRequested then [|"--help"|] else [||])
        let parser=ConsolePolicyFodexFunV405.parser
        let legacy=parser.ParseCommandLine(remaining,raiseOnUsage=false)
        let name=endpoints.TryGetResult Akkaname
        if name |> Option.exists String.IsNullOrWhiteSpace then invalidArg "akkaname" "ActorSystem name cannot be empty"
        // 完整的 ActorSystem 名稱規則交由 Akka 在建立 node 時驗證，不另複製一套 regex。
        if legacy.IsUsageRequested then remaining,name else
        let updates=[
            match endpoints.TryGetResult Akkaport with
            | Some port ->
                if port<0 || port>65535 then invalidArg "akkaport" "Expected 0..65535"
                if strict && (legacy.TryGetResult ConsoleArgumentV405.Port |> Option.exists((<>) port)) then
                    invalidArg "akkaport" "--akkaport and --port must specify the same Akka port"
                yield ConsoleArgumentV405.Port port
            | None -> ()
            match endpoints.TryGetResult Mcpport with
            | Some port ->
                if port<1 || port>65535 then invalidArg "mcpport" "Expected 1..65535"
                let explicitUrl=legacy.TryGetResult ConsoleArgumentV405.Service_Url
                let uri=UriBuilder(explicitUrl |> Option.defaultValue fallbackUrl)
                if strict && explicitUrl.IsSome && uri.Port<>port then
                    invalidArg "mcpport" "--mcpport and --service-url must specify the same HTTP port"
                uri.Port<-port
                yield ConsoleArgumentV405.Service_Url(uri.Uri.AbsoluteUri.TrimEnd('/'))
            | None -> () ]
        parser.PrintCommandLineArguments(ParticipantArgumentsFodexFunV402.merge (legacy.GetAllResults()) updates),name

    /// defaults 先處理、argv 再覆寫；name 不使用全域 mutable 或環境變數傳到 host。
    /// 舊 mode+port 的裸數字會被 V405 Argu 拒絕；不再採用 V406 的 tuple interpretation。
    let parse (ctx:UniContext,text:string,argv:string array) =
        try
            let parser=ConsolePolicyFodexFunV405.parser
            let initial=PL.parseLine ctx [|' '|] (Some '"') None true (text.ReplaceLineEndings(" ").Trim())
            let defaults,defaultName=normalize false "http://127.0.0.1:18824" initial
            let defaults=parser.ParseCommandLine(defaults,raiseOnUsage=false)
            let changes,changedName=normalize true (defaults.GetResult ConsoleArgumentV405.Service_Url) argv
            let previous=ConsolePolicyFodexFunV403.pinBase<_,ParseResults<ConsoleArgumentV405>> "4.0.5" targetParse
            previous(ctx,parser.PrintCommandLineArgumentsFlat(defaults.GetAllResults()),changes)
            |> Result.map(fun options -> options,Option.orElse defaultName changedName)
        with error -> Error error

    let usage () =
        "Host options:\n  --mode <console|supervisor|mcp>\n  --mcpport <HTTP-port>\n  --akkaport <Akka-port>\n  --akkaname <ActorSystem-name>\n"+
        "Omitted values retain existing defaults. Legacy --port/--service-url remain supported.\n\n"+
        ConsolePolicyFodexFunV405.parser.PrintUsage()

    let parseQuotation:Expr<(UniContext*string*string array) -> FeasibleT<_,ConsoleStartupV407,exn>> =
        <@ fun _ -> fun input -> parse input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetParse version parseQuotation|]
