#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeMcpConfigurationFodexFunV100Tests
#endif
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let url="http://127.0.0.1:18825/mcp"
let dictionary items=Dictionary<string,obj>(dict items) :> IDictionary<string,obj>
let child (values:IDictionary<string,obj>) key=values[key] :?> IDictionary<string,obj>
[<Tests>]
let tests=testList "Native cache MCP configuration V100" [
    testCase "AGY registration invokes endpoint verification before any global CLI mutation" <| fun () -> withNode(fun node ->
        let rejected:Expr<string -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
            <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "FixtureCacheRejected" :> exn))) @>
        FodexFunctions.initialize()
        FodexFunRegistry.register node "SharedCache.CheckEndpoint" (System.Version(1,0,0)) rejected
        let ensure=evaluated<NativeAgyCacheInputV100,Task<Result<unit,exn>>> "1.0.0" node "NativeMcpConfiguration.EnsureAgy"
        let result=ensure(node,ctx,"nonexistent-must-not-be-started.exe","F:/fodex",url,CancellationToken.None) |> required |> wait
        match result with
        | Error error -> Expect.equal error.Message "FixtureCacheRejected" "did not start CLI, acquire lock or change global configuration"
        | Ok _ -> failtest "registration cannot succeed against a rejected endpoint")
    testCase "Codex merge preserves every existing server and never mutates caller dictionaries" <| fun () -> withNode(fun node ->
        let merge=evaluated<string*IDictionary<string,obj>,IDictionary<string,obj>> "1.0.0" node "NativeMcpConfiguration.Codex"
        let hub=dictionary ["url",box "http://127.0.0.1:18819/mcp"]
        let original=dictionary ["mcp_servers",box(dictionary ["fodex_order_hub",box hub]);"service_tier",box "default"]
        let result=merge(url,original) |> required
        Expect.equal ((child original "mcp_servers").Count) 1 "original server dictionary unchanged"
        let servers=child result "mcp_servers"
        Expect.equal (servers.Keys |> Set.ofSeq) (Set.ofList["fodex_order_hub";"fodex_cache"]) "only cache added"
        let cacheServer,hubServer=child servers "fodex_cache",child servers "fodex_order_hub"
        Expect.equal cacheServer["url"] (box url) "explicit cache endpoint"
        Expect.equal hubServer["url"] hub["url"] "root hub preserved"
        Expect.equal result["service_tier"] (box "default") "unrelated settings preserved"
        let again=merge(url,result) |> required
        Expect.equal ((child again "mcp_servers").Count) 2 "idempotent merge")
    testCase "cache configuration cannot overwrite another URL or accept a stdio collision" <| fun () -> withNode(fun node ->
        let merge=evaluated<string*IDictionary<string,obj>,IDictionary<string,obj>> "1.0.0" node "NativeMcpConfiguration.Codex"
        for fields in [["url",box "http://127.0.0.1:18826/mcp"];["command",box "unrelated-tool"]] do
            let original=dictionary ["mcp_servers",box(dictionary ["fodex_cache",box(dictionary fields)])]
            Expect.isError (merge(url,original)) "do not overwrite another server with the same fixed name"
        let disabled=dictionary ["mcp_servers",box(dictionary ["fodex_cache",box(dictionary ["url",box url;"enabled",box false])])]
        Expect.equal ((child (child (merge(url,disabled) |> required) "mcp_servers") "fodex_cache")["enabled"]) (box true) "same endpoint can be enabled")
    testCase "AGY listing gives deterministic add enable or no-op and refuses conflicts" <| fun () -> withNode(fun node ->
        let plan=evaluated<string*string,AgyCacheRegistrationV100> "1.0.0" node "NativeMcpConfiguration.AgyPlan"
        let header="NAME  TYPE  STATUS   COMMAND/URL\n"
        Expect.equal (plan(url,header) |> required) Missing "no rows"
        Expect.equal (plan(url,header+"comm  http  enabled  http://127.0.0.1:8888/mcp\n") |> required) Missing "unrelated server untouched"
        Expect.equal (plan(url,header+"fodex_cache  http  enabled  "+url) |> required) Enabled "already correct"
        Expect.equal (plan(url,header+"fodex_cache  http  disabled  "+url) |> required) Disabled "enable without add"
        for row in ["fodex_cache http enabled http://127.0.0.1:18826/mcp";"fodex_cache stdio enabled unrelated.exe";"fodex_cache http unknown "+url] do
            Expect.isError (plan(url,header+row)) "no overwrite/unknown-state guess"
        Expect.isError (plan(url,"a new unsupported listing format")) "no invented JSON flag or silent missing result")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeMcpConfigurationFodexFunV100Tests failed"
#endif
