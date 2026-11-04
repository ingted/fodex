namespace Fodex.Supervisor

open System
open System.Collections.Generic
open FSharp.Quotations
open Fodex.Supervisor.Contracts.AliasMod
open Fodex.Supervisor.Contracts.Concepts

// CD 由 wrapper actor 持有；只把它交給已 pin 的 operation，不放 module/global registry。
// int 是草稿號；snapshot 是不可變的本批原文，與 graph participant number 無關。
type DraftInputV300 = CD<int,string> * CD<int,string * exn> * int * WrapperCommandV300
type DraftOutputV300 = int * (int * string) array * string
type DraftSettlementV300 = CD<int,string> * CD<int,string * exn> * (int * string) array * Result<unit,exn>

module WrapperDraftFodexFunV300 =
    [<Literal>]
    let targetOperate = "WrapperDraft"
    [<Literal>]
    let targetSettle = "WrapperDraftSettlement"
    let version = System.Version(3,0,0)

    /// preview/sendAll 共用一份建構邏輯；原 prompt 內 CRLF/LF 均不被改寫。
    let snapshot (drafts: CD<int,string>) =
        let rows = drafts.ToArray() |> Array.map (fun item -> item.Key,item.Value) |> Array.sortBy fst
        rows, (rows |> Array.map snd |> String.concat "\n")

    /// actor 先檢查 in-flight/busy，這裡只執行單一 buffer operation。
    /// 例：operate(drafts,failures,last,Append "next") -> (last+1,[||],ack)。
    /// preview/sendAll 只回傳 snapshot，不能在入列 acknowledgement 前清資料。
    let apply (drafts,failed,last,command: WrapperCommandV300) : Result<DraftOutputV300,exn> =
        try
            let drafts: CD<int,string> = drafts
            let failed: CD<int,string * exn> = failed
            let unchanged text = last,[||],text
            let existing i =
                if i <= 0 then invalidArg "i" "Draft index must be positive"
                match drafts.TryGetValue i with
                | true,value -> value
                | _ -> raise (KeyNotFoundException(sprintf "Draft %d does not exist" i))
            let output =
                match command with
                | WrapperCommandV300.Append prompt ->
                    if last < 0 || last = Int32.MaxValue then raise (OverflowException "Draft sequence exhausted or invalid")
                    let next = last + 1
                    if failed.ContainsKey next || not (drafts.TryAdd(next,prompt)) then invalidOp "Draft high-water mark is inconsistent"
                    next,[||],sprintf "Draft %d appended" next
                | WrapperCommandV300.Update(i,prompt) ->
                    if not (drafts.TryUpdate(i,prompt,existing i)) then invalidOp "Draft changed outside owner mailbox"
                    unchanged (sprintf "Draft %d updated" i)
                | WrapperCommandV300.Delete i ->
                    existing i |> ignore
                    if not (drafts.TryRemove i |> fst) then invalidOp "Draft changed outside owner mailbox"
                    unchanged (sprintf "Draft %d deleted" i)
                | WrapperCommandV300.Preview | WrapperCommandV300.SendAll ->
                    let rows,text = snapshot drafts
                    last,rows,text
                | WrapperCommandV300.ClearDrafts ->
                    let count = drafts.Count
                    drafts.Clear()
                    unchanged (sprintf "Cleared %d pending drafts; next sequence remains above %d" count last)
                | WrapperCommandV300.ClearFailures ->
                    let count = failed.Count
                    failed.Clear()
                    unchanged (sprintf "Cleared %d failures" count)
                | WrapperCommandV300.ReviewFailures(first,finish) ->
                    if first <= 0 || (finish |> Option.exists (fun last -> last > 0 && last < first)) then invalidArg "range" "Invalid failure range"
                    let selected =
                        failed.ToArray() |> Array.filter (fun item ->
                            match finish with
                            | None -> item.Key = first
                            | Some last -> item.Key >= first && (last <= 0 || item.Key <= last))
                        |> Array.sortBy (fun item -> item.Key)
                    if finish.IsNone && selected.Length = 0 then raise (KeyNotFoundException(sprintf "Failure %d does not exist" first))
                    selected |> Array.map (fun item ->
                        let prompt,error = item.Value
                        sprintf "[failure %d]\n%s\n%s" item.Key prompt (error.ToString()))
                    |> String.concat "\n\n" |> unchanged
                | _ -> invalidArg "command" "Not a draft operation"
            Ok output
        with ex -> Error ex

    /// ack 只指成功入列，不等待 LLM 完成。failure 保留各原序號／原文／exn，不自動 requeue。
    /// 先完整驗證，再寫 failure，最後移除本批；新 append 不受影響。
    /// actor 必須凍結本批的 update/delete；此處仍防禦檢查，避免 caller 誤用造成資料遺失。
    let complete (drafts,failed,rows,result: Result<unit,exn>) : Result<unit,exn> =
        try
            let drafts: CD<int,string> = drafts
            let failed: CD<int,string * exn> = failed
            let rows: (int * string) array = rows
            if (rows |> Array.map fst |> Array.distinct |> Array.length) <> rows.Length then invalidArg "rows" "Duplicate draft indices"
            for i,prompt in rows do
                match drafts.TryGetValue i with
                | true,current when current = prompt -> ()
                | _ -> invalidOp (sprintf "In-flight draft %d changed; settlement refused" i)
                if Result.isError result && failed.ContainsKey i then invalidOp (sprintf "Failure %d already exists" i)
            match result with
            | Ok () -> ()
            | Error error ->
                for i,prompt in rows do
                    if not (failed.TryAdd(i,(prompt,error))) then invalidOp "Failure buffer changed outside owner mailbox"
            // 操作序列由 owner actor 保護；移除的是這份不可變快照，不是 CD.Clear()。
            for i,_ in rows do
                if not (drafts.TryRemove i |> fst) then invalidOp "Draft buffer changed outside owner mailbox"
            Ok ()
        with ex -> Error ex

    let operate : Expr<DraftInputV300 -> FeasibleT<DraftInputV300,DraftOutputV300,exn>> =
        <@ fun _ -> fun input -> apply input @>
    let settle : Expr<DraftSettlementV300 -> FeasibleT<DraftSettlementV300,unit,exn>> =
        <@ fun _ -> fun input -> complete input @>

    let register node =
        FodexFunRegistry.register node targetOperate version operate
        FodexFunRegistry.register node targetSettle version settle
