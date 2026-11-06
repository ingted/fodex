#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System.Threading.Tasks
open Argu
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// 新增唯讀的 list_subagent；order/receive/health 仍走 1.0.1 的同一實作與版本邊界。
module HubClientFodexFunV102 =
    let version=System.Version(1,0,2)
    let targetRun=HubClientFodexFunV101.targetRun
    let run ((http,argv,log,ct):HubClientFodexFunV101.RunInput) =
        Ok(task {
            try
                let options=ArgumentParser.Create<HubClientV101.ClientArgument>().ParseCommandLine argv
                match options.GetResult HubClientV101.Action with
                | "list_subagent" | "list" ->
                    let url=System.Uri(options.GetResult HubClientV101.Url)
                    if not url.IsLoopback || url.Scheme<>"http" then invalidArg "url" "Only loopback HTTP is supported"
                    let! json=HubClientV101.request http log ct url System.Net.Http.HttpMethod.Get "/participants" "" ""
                    return Ok json
                | _ ->
                    let operation=FodexFun<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>,exn>(
                                      VersionRegistry<FeasibleFun<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>,exn>>.BaseCD,targetRun)
                                  |> FodexFun.pin(Some "1.0.1") |> Option.defaultWith(fun () -> invalidOp "Missing HubClient.Run 1.0.1")
                    match FeasibleFun.invoke (http,argv,log,ct) operation with
                    | Ok work -> return! work
                    | Error error -> return Error error
            with error -> return Error error })

    let quotation:Expr<HubClientFodexFunV101.RunInput -> FeasibleT<_,Task<Result<string,exn>>,exn>> =
        <@ fun _ -> fun input -> run input @>
    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare targetRun version quotation |]
