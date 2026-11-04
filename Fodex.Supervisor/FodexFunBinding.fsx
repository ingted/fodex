namespace Fodex.Supervisor

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
