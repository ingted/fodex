namespace Fodex.Supervisor

open System
open System.Collections.Generic
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type ParticipantSelectionV300 = WrappedReActDataFlow<string> * ReActParticipant * ParticipantSelectorV300

[<RequireQualifiedAccess>]
type ParticipantLifecycleV300 = Start | Stop | Remove

/// 同一份 graph snapshot 決定整批控制的目標；不讀 actor、不執行 lifecycle、不配置新的 N。
module ParticipantControlFodexFunV300 =
    let version=System.Version(3,0,0)
    let targetSelect="ParticipantControl.Select"
    let targetNative="ParticipantControl.NativeTargets"
    let required=function Ok value -> value | Error(ex:exn) -> raise ex

    /// 包含 roots 本身。只走目前有效 Owns；已撤銷的歷史 edge 不會再次成為子樹。
    let descendants (graph:WrappedReActDataFlow<string>) (roots:ReActParticipant array) =
        let selected=HashSet<ReActParticipant>(roots)
        let owns=ReActTopologyFodexFunV300.relations graph |> Array.choose(function
            | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Owns -> Some(parent,child)
            | _ -> None)
        let mutable changed=true
        while changed do
            changed<-false
            for parent,child in owns do
                if selected.Contains parent && selected.Add child then changed<-true
        selected

    /// 例：Numbers [3L;5L;3L] 只選一次 N=3；Range(3L,8L) 篩現有節點，包含兩端。
    /// 整批先驗證再回傳，某個明確 N 不存在/已 remove 時，不會先執行其他人的動作。
    let select ((graph,owner,selector):ParticipantSelectionV300) : Result<ReActParticipant array,exn> =
        try
            let owned=descendants graph [|owner|]
            let available=ReActTopologyFodexFunV300.participants graph
                          |> Array.filter(fun (p,info) -> owned.Contains p && info.Status<>ReActParticipantStatus.Retired)
            let one n =
                if n<=0L then invalidArg "N" "Participant number must be positive"
                available |> Array.tryFind(fun (_,info) -> info.Number=n) |> Option.map fst
                |> Option.defaultWith(fun () -> raise(KeyNotFoundException(sprintf "Participant N=%d is absent, removed, or outside this wrapper" n)))
            let chosen=match selector with
                       | ParticipantSelectorV300.Main -> [|ReActTopologyFodexFunV300.main(graph,owner) |> required|]
                       | ParticipantSelectorV300.Number n -> [|one n|]
                       | ParticipantSelectorV300.Numbers values ->
                           if values.IsEmpty then invalidArg "selector" "Participant list must not be empty"
                           values |> List.map one |> List.toArray
                       | ParticipantSelectorV300.Range(first,last) ->
                           if first>last then invalidArg "selector" "Participant range must be ascending"
                           one first |> ignore;one last |> ignore
                           available |> Array.choose(fun (p,info) -> if info.Number>=first && info.Number<=last then Some p else None)
                       | ParticipantSelectorV300.All -> available |> Array.map fst
            Ok(chosen |> Array.distinct |> Array.sortBy fst)
        with ex -> Error ex

    /// 例：同時選 sub wrapper N=3 與其 main N=6，只對 native N=6 執行一次。
    /// 已停止仍是合法目標，是否可執行某個 operation 由其 owner 決定；Retired 在 select 就拒絕。
    let nativeTargets ((graph,owner,selector):ParticipantSelectionV300) : Result<ReActParticipant array,exn> =
        select(graph,owner,selector) |> Result.bind(fun selected ->
            try Ok(selected |> Array.map(fun p -> ReActTopologyFodexFunV300.main(graph,p) |> required) |> Array.distinct |> Array.sortBy fst)
            with ex -> Error ex)

    let selectQuotation : Expr<ParticipantSelectionV300 -> FeasibleT<_,ReActParticipant array,exn>> = <@ fun _ -> fun input -> select input @>
    let nativeQuotation : Expr<ParticipantSelectionV300 -> FeasibleT<_,ReActParticipant array,exn>> = <@ fun _ -> fun input -> nativeTargets input @>
    let register node =
        FodexFunRegistry.register node targetSelect version selectQuotation
        FodexFunRegistry.register node targetNative version nativeQuotation

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetSelect version selectQuotation
            FodexFunRegistry.declare targetNative version nativeQuotation
        |]
