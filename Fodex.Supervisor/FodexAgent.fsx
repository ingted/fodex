#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.CoreMod

/// 使用者入口只有 Contract 的 FodexAgent 物件；不要求腳本 bootstrap/register。
[<AutoOpen>]
module FodexAgentMembers =
    type FodexAgent<'Command,'Message when 'Command:equality> with
        /// 先選定 factory 版本，再由該 object expression 建立同一 wrapper/loop。
        /// 範例：FodexAgent<string,obj>.Load(input,version="4.0.2")。
        /// 省略 version 時 pin 首次使用的最大版本；更新 registry 不改既有 factory。
        static member Load(input:FodexAgentCreationV402,?version:string) : Result<FodexAgent<string,obj>,exn> =
            try
                FodexFunctions.initialize()
                let node,_,_,_,_,_,_,_ = input
                FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node version FodexAgentFodexFunV402.target input
            with error -> Error error

        /// 明確查詢原生能力；不在 Load/CreateWrapper 時暗中起動 Codex。
        static member LoadModelCatalog(node,codex,directory,?version:string) =
            try
                FodexFunctions.initialize()
                FodexFunBinding.pin<_,System.Text.Json.JsonElement array> node version FodexAgentFodexFunV402.targetCatalog (node,codex,directory)
            with error -> Error error
