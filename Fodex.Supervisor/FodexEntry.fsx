namespace Fodex.Supervisor

open System.Threading.Tasks
open Fodex.Supervisor.Contracts.Concepts

/// Host 的單一建立入口。M 系列腳本只供應設定、輸入/輸出，不再自行接 factory、hub 與 handler。
/// 此模組沒有另一份建立實作或 cache；業務流程在 registry 中的 Wrapper.Start quotation。
module FodexEntry =
    /// None 固定使用此次建立時的最大版本；Some "3.0.2" 明確 pin。
    /// 例：createWrapper (Some "3.0.2") (connectOutput,node,cs,sid,args,ctx,models,Mcp,url,sink)。
    /// 回傳 wrapper IAgent 與同一次建立的 loop/hub/remote/cleanup，供 host 接輸出與正常釋放。
    let createWrapper versionText (input:WrapperCompositionInput) =
        try
            let _,node,_,_,_,_,_,_,_,_=input
            WrapperCompositionV302.register node
            FodexFunBinding.pin<WrapperCompositionInput,Task<WrapperCompositionOutput>> node versionText WrapperCompositionV302.target input
        with ex -> Error ex
