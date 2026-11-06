#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// 只解析新增的兩參數語法；其他選項仍由 V405 的完整 Argu template 處理。
/// Argu 不允許 tuple 中放 int option，所以舊的單參數 --mode 交回 V405。
type ModePortArgumentV406 =
    | [<Unique>] Mode of mode:string * port:int
    interface IArgParserTemplate with
        member _.Usage = "console/supervisor: Akka node port (0..65535); mcp: HTTP port (1..65535)."

module ConsolePolicyFodexFunV406 =
    let version=System.Version(4,0,6)
    let targetParse=ConsolePolicyFodexFunV405.targetParse
    let modeParser=ArgumentParser.Create<ModePortArgumentV406>(programName="Fodex.Supervisor")

    /// 保留同一個 ParseResults<ConsoleArgumentV405>，不讓語法調整擴散到 service/console host。
    /// 例：--mode mcp 18924 --port 21924 同時指定 HTTP 18924 與 Akka 21924。
    /// command line 的兩種寫法若指向同一設定卻互相衝突，拒絕；不以出現順序暗中覆蓋。
    let normalize strict fallbackUrl (argv:string array) =
        let selected =
            try Some(modeParser.ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false))
            with :? ArguParseException -> None
        match selected |> Option.bind(fun values -> values.TryGetResult Mode |> Option.map(fun pair -> values,pair)) with
        | None -> argv // 舊語法或不合法輸入仍須經 V405 完整驗證，並非忽略解析錯誤。
        | Some(selected,(name,port)) ->
            let name=name.ToLowerInvariant()
            if not(List.contains name ["console";"supervisor";"mcp"]) then invalidArg "mode" "Use console, supervisor or mcp"
            let minimum=if name="mcp" then 1 else 0
            if port<minimum || port>65535 then invalidArg "mode" (sprintf "%s port must be %d..65535" name minimum)
            let parser=ConsolePolicyFodexFunV405.parser
            let legacyArgv=Array.concat [|
                selected.UnrecognizedCliParams |> List.toArray
                parser.PrintCommandLineArguments [ConsoleArgumentV405.Mode name]
                if selected.IsUsageRequested then [|"--help"|] else [||] |]
            let legacy=parser.ParseCommandLine(legacyArgv,raiseOnUsage=false)
            if legacy.IsUsageRequested then legacyArgv else
            let update =
                if name="mcp" then
                    let explicitUrl=legacy.TryGetResult ConsoleArgumentV405.Service_Url
                    let uri=UriBuilder(explicitUrl |> Option.defaultValue fallbackUrl)
                    if strict && explicitUrl.IsSome && uri.Port<>port then
                        invalidArg "service-url" "--mode mcp <port> and --service-url must specify the same HTTP port"
                    uri.Port<-port
                    ConsoleArgumentV405.Service_Url(uri.Uri.AbsoluteUri.TrimEnd('/'))
                else
                    if strict && (legacy.TryGetResult ConsoleArgumentV405.Port |> Option.exists((<>) port)) then
                        invalidArg "port" "--mode console/supervisor <port> and --port must specify the same Akka port"
                    ConsoleArgumentV405.Port port
            ParticipantArgumentsFodexFunV402.merge (legacy.GetAllResults()) [update]
            |> parser.PrintCommandLineArguments

    /// defaults 先轉換，argv 再覆寫；自訂的 MCP host 保留，只替換指定 port。
    /// provider/model/headless 與原有參數規則由 registry 中 pinned 4.0.5 quotation 繼續處理。
    let parse (ctx:UniContext,text:string,argv:string array) =
        try
            let parser=ConsolePolicyFodexFunV405.parser
            let defaults=PL.parseLine ctx [|' '|] (Some '"') None true (text.ReplaceLineEndings(" ").Trim())
                         |> normalize false "http://127.0.0.1:18824"
                         |> fun values -> parser.ParseCommandLine(values,raiseOnUsage=false)
            let changes=normalize true (defaults.GetResult ConsoleArgumentV405.Service_Url) argv
            let previous=ConsolePolicyFodexFunV403.pinBase<_,ParseResults<ConsoleArgumentV405>> "4.0.5" targetParse
            previous(ctx,parser.PrintCommandLineArgumentsFlat(defaults.GetAllResults()),changes)
        with error -> Error error

    let usage () =
        "Mode port syntax:\n  --mode console <akka-port>\n  --mode supervisor <akka-port>\n  --mode mcp <http-port>\n"+
        "The port is optional; --port still selects Akka in mcp mode. Conflicting explicit port settings are rejected.\n\n"+
        ConsolePolicyFodexFunV405.parser.PrintUsage()

    let parseQuotation:Expr<(UniContext*string*string array) -> FeasibleT<_,ParseResults<ConsoleArgumentV405>,exn>> =
        <@ fun _ -> fun input -> parse input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetParse version parseQuotation|]
