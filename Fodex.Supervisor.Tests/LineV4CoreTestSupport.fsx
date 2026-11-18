#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module LineV4CoreTestSupport
#endif
open System
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open ImplementationTestSupportV300

/// Shared typed startup options, independent of another test's module initialization.
let coreSettings folder =
    FodexFunctions.pinBase<_,Argu.ParseResults<ConsoleArgumentV405>> "4.0.5" "Console.ParseStartup"
        (ctx,ConsolePolicyFodexFunV405.defaults,
         [|"--mainagent-type";"codex";"--native-adapter";"app-server";"--model";"known";
           "--port";"0";"--output-directory";folder|]) |> required

/// Only AGY's global registration is substituted. Cache ownership/HTTP/SQL remain real.
let checkCache ((node,_,_,_,url,_):NativeAgyCacheInputV100) =
    let check=FodexFunBinding.pin<string,Task<Result<unit,exn>>> node (Some "1.0.0") "SharedCache.CheckEndpoint"
    FodexFunBinding.invokeAsync check url

let cacheRegistrationQuotation:Expr<NativeAgyCacheInputV100 -> FeasibleT<NativeAgyCacheInputV100,Task<Result<unit,exn>>,exn>> =
    <@ fun _ -> fun input -> Ok(checkCache input) @>

let failedRegistrationQuotation:Expr<NativeAgyCacheInputV100 -> FeasibleT<NativeAgyCacheInputV100,Task<Result<unit,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "fixture registration failure" :> exn))) @>
