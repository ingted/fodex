#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Host的cache endpoint，不混入participant/native的argv或actor身分。
type CacheArgumentV410 =
    | [<Unique>] Cacheport of int
    | [<Unique>] Cache_Url of string
    interface IArgParserTemplate with
        member this.Usage=match this with
                          | Cacheport _ -> "Dedicated shared cache MCP port; default 18825."
                          | Cache_Url _ -> "Shared cache MCP endpoint; default http://127.0.0.1:18825/mcp. Console connects to this existing service."
type ConsoleStartupV410=ParseResults<ConsoleArgumentV405>*string option*Uri
type ConsoleRunInputV410=ConsoleRunInputV407*Uri

module ConsolePolicyFodexFunV410 =
    let version=System.Version(4,1,0)
    let targetParse=ConsolePolicyFodexFunV405.targetParse
    let cacheParser=ArgumentParser.Create<CacheArgumentV410>(programName="Fodex.Supervisor")
    let defaultCacheUrl="http://127.0.0.1:18825/mcp"
    let required=function Ok value -> value | Error(error:exn) -> raise error
    let remaining (options:ParseResults<CacheArgumentV410>)=
        Array.append (options.UnrecognizedCliParams |> List.toArray) (if options.IsUsageRequested then [|"--help"|] else [||])

    /// Argu分離host cache選項；所有其他argv原樣交既有405/407 parser，不以字串replace或位置解析拆參數。
    /// 例：--mode supervisor --akkaport 21824 --cacheport 18825；console可用--cache-url連既有服務。
    let parse (ctx:UniContext,text:string,argv:string array)=
        try
            let initial=PL.parseLine ctx [|' '|] (Some '"') None true (text.ReplaceLineEndings(" ").Trim())
            let defaults=cacheParser.ParseCommandLine(initial,ignoreUnrecognized=true,raiseOnUsage=false)
            let changes=cacheParser.ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)
            let explicitUrl=changes.TryGetResult CacheArgumentV410.Cache_Url
            let url=explicitUrl |> Option.orElseWith(fun () -> defaults.TryGetResult CacheArgumentV410.Cache_Url) |> Option.defaultValue defaultCacheUrl
            let endpoint=UriBuilder(Uri(url,UriKind.Absolute))
            let requestedPort=changes.TryGetResult CacheArgumentV410.Cacheport
            let selectedPort=requestedPort |> Option.orElseWith(fun () -> if explicitUrl.IsSome then None else defaults.TryGetResult CacheArgumentV410.Cacheport)
            selectedPort |> Option.iter(fun port ->
                if port<1 || port>65535 then invalidArg "cacheport" "Expected 1..65535"
                if explicitUrl.IsSome && requestedPort.IsSome && endpoint.Port<>port then invalidArg "cacheport" "--cacheport and --cache-url must specify the same port"
                endpoint.Port<-port)
            let uri=endpoint.Uri
            if not(List.contains uri.Scheme ["http";"https"]) || uri.Port<1 || uri.Port>65535 || uri.UserInfo<>"" || uri.Query<>"" || uri.Fragment<>"" ||
               not(List.contains uri.AbsolutePath ["/";"/mcp"]) then invalidArg "cache-url" "Expected an HTTP(S) cache endpoint without user info, query or fragment"
            endpoint.Path<-"/mcp"
            let previous=ConsolePolicyFodexFunV403.pinBase<_,ConsoleStartupV407> "4.0.7" targetParse
            // 先讓舊parser合併文字defaults，再讓外部argv覆寫；Argu formatter負責帶空白/引號的值。
            let initialOptions,defaultName=previous(ctx,ConsolePolicyFodexFunV405.defaults,remaining defaults) |> required
            let normalized=ConsolePolicyFodexFunV405.parser.PrintCommandLineArgumentsFlat(initialOptions.GetAllResults())
            let options,name=previous(ctx,normalized,remaining changes) |> required
            Ok(options,Option.orElse defaultName name,endpoint.Uri)
        with error -> Error error
    let usage()="Shared cache options:\n  --cacheport <1..65535>\n  --cache-url <http[s]://host:port/mcp>\n\n"+ConsolePolicyFodexFunV407.usage()
    let quotation:Expr<(UniContext*string*string array) -> FeasibleT<_,ConsoleStartupV410,exn>> =
        <@ fun _ -> fun input -> parse input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetParse version quotation|]
