namespace Fodex.Supervisor

open System
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

module FodexFunRegistry =
    let register
        (node: FodexNode)
        (target: TargetName)
        (version: Version)
        (quotation: Expr<'Input -> FeasibleT<'Input, 'Success, 'Error>>)
        : unit =
        let feasibleFun =
            FeasibleFun<'Input, 'Success, 'Error>(quotation)
        let fodexFun =
            FodexFun<'Input, 'Success, 'Error>(node, target)
        fodexFun.Register(version, feasibleFun)

