namespace Fodex.Supervisor

open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Compact completed-message headings keep the answer on the wrapper identity line.
/// Reuses V405 policies and editor; raw /show remains an independent output channel.
module ConsoleOutputFodexFunV406 =
    let version=System.Version(4,0,6)
    let targetFormat=ConsoleOutputFodexFunV405.targetFormat
    let targetStream=ConsoleOutputFodexFunV405.targetStream
    let format (labels,event:ReActEvent<string>) =
        match labels,event with
        | true,AgentWork(_,_,MessageCompleted(_,text)) -> Ok(Some("[AgentWork completed] "+text))
        | true,AgentCot(_,_,MessageCompleted(_,text)) -> Ok(Some("[AgentCot completed] "+text))
        | _ -> ConsoleOutputFodexFunV405.format(labels,event)
    let formatStream (json,labels,edge:ReActDataStream<string>) =
        if json then ConsoleOutputFodexFunV405.formatStream(json,labels,edge)
        else let _,_,event=edge in format(labels,event)
    let quotation:Expr<bool*ReActEvent<string> -> FeasibleT<_,string option,exn>> =
        <@ fun _ -> fun input -> format input @>
    let streamQuotation:Expr<bool*bool*ReActDataStream<string> -> FeasibleT<_,string option,exn>> =
        <@ fun _ -> fun input -> formatStream input @>
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetFormat version quotation
                       FodexFunRegistry.declare targetStream version streamQuotation|]
