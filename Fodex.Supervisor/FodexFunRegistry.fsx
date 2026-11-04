#if INTERACTIVE
#load "QuotationStore.fsx"
#endif

namespace Fodex.Supervisor

open System
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

[<AutoOpen>]
module FodexFunRegistry =
    /// 宣告內建 q，不建立 node、不 eval q、不連 DB。回傳的函數只配置 typed storage。
    /// module 範例：let builtins = [| declare target version quotation |]。
    /// entrypoint 首次使用 FodexFunctions.initialize()，集中 evaluate builtins 並執行配置。
    /// 註冊與執行分離：node/ctx/loop 必須是 quotation 產出函數的 input，不得閉包捕捉。
    let declare
        (target:TargetName)
        (version:Version)
        (quotation:Expr<'Input -> FeasibleT<'Input,'Success,'Error>>) : (unit -> unit) =
        QuotationStore.validateQuotation quotation
        FodexFun<'Input,'Success,'Error>(VersionRegistry<FeasibleFun<'Input,'Success,'Error>>.BaseCD,target)
            .Register(version,FeasibleFun<'Input,'Success,'Error>(quotation))
        fun () -> QuotationStore.configureOptional<'Input,'Success,'Error> QuotationStore.connectionStringFor

    /// 舊 node-scoped bootstrap API，保留給尚未轉移的歷史實作／測試。
    /// 新 module 宣告用 declare；runtime 修改用 FodexFun(node,target).Register，經 SQL 後才發布。
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

