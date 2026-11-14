namespace Fodex.Supervisor

open System.Threading.Tasks
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// Registry 接線 helper，沒有業務實作與另一份 cache；選定的 FeasibleFun 已持有 Lazy .f。
module FodexFunBinding =
    /// 例：pin node (Some "3.0.1") "OrderHub.Receive"，或 None 選啟動當時最大版本。
    /// 回傳的 function 固定使用該 FeasibleFun；換版必須重新 pin，不會在回合中偷換版本。
    let pin<'Input,'Output> (node:FodexNode) versionText target : FeasibleT<'Input,'Output,exn> =
        let operation =
            FodexFun<'Input,'Output,exn>(node,target) |> FodexFun.pin versionText
            |> Option.defaultWith(fun () -> invalidOp(sprintf "Missing FodexFun %s (%s)" target (defaultArg versionText "max-at-startup")))
        fun input -> FeasibleFun.invoke input operation

    /// 連接已pin的兩階段函數與Task ROP；同步／非同步失敗均保留原Error。
    /// 例：let! result = invokeAsync commit input。此helper不選版本、不擁有actor/SQL/process。
    let invokeAsync (operation:FeasibleT<'Input,Task<Result<'Output,exn>>,exn>) input : Task<Result<'Output,exn>> = task {
        try
            match operation input with
            | Error error -> return Error error
            | Ok work -> return! work
        with error -> return Error error
    }
