namespace Fodex.Supervisor

open System
open System.Text
open Fodex.Supervisor.Contracts.CoreMod

/// 純顯示函數，不保管歷史；傳入 callback 不可同步 Ask 同一 ReActLoopHandler。
module WrapperOutputV200 =
    /// 例：createSink 100 (printf "%s") (fun () -> completed.Set() |> ignore)。
    /// width=0 關閉 soft wrap；單位是 Unicode scalar，不是終端機中文字寬。
    let createSink width (write: string -> unit) (onEnd: unit -> unit) =
        let mutable phase = ""
        let mutable column = 0
        let mutable previousCR = false
        let response = StringBuilder()
        let newLine () = write Environment.NewLine; column <- 0
        let finishLine () =
            if column > 0 then newLine ()
            previousCR <- false
        let print name (text: string) =
            if not (String.IsNullOrEmpty text) then
                if phase <> name then
                    finishLine ()
                    write ("[" + name + "]" + Environment.NewLine)
                    phase <- name
                for rune in text.EnumerateRunes() do
                    match rune.Value with
                    | 13 -> newLine (); previousCR <- true
                    | 10 ->
                        if not previousCR then newLine ()
                        previousCR <- false
                    | _ ->
                        previousCR <- false
                        if width > 0 && column >= width then newLine ()
                        write (rune.ToString())
                        column <- column + 1
        fun (_,target,event: ReActEvent<string>) ->
            if target = ReActEndpoint.Human then
                match event with
                | AgentCot(_,_,Message text) -> print "COT" text
                | AgentWork(_,_,Message text) -> response.Append text |> ignore; print "Response" text
                | AgentEnd(_,_,Message text) ->
                    let streamed = response.ToString()
                    // 正常 final 是已串流的完整內容；若最後 delta 未到，僅補完整 final 的後綴。
                    // final 與串流完全不相符時，明列 Final，不能默默藏掉更正後的答案。
                    if text.StartsWith(streamed,StringComparison.Ordinal) then print "Response" (text.Substring streamed.Length)
                    elif text <> streamed then print "Final" text
                    finishLine ()
                    newLine ()
                    phase <- ""
                    response.Clear() |> ignore
                    onEnd ()
                | StdErr(_,_,text) -> print "Error" text; finishLine ()
                | _ -> ()
