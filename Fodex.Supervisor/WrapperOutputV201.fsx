namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text
open Fodex.Supervisor.Contracts.CoreMod

/// 只處理顯示，不保存 graph、查 DB 或操作 actor。V200 保留以重現舊顯示行為。
module WrapperOutputV201 =
    /// 例：createSink 100 (printf "%s") (fun () -> completed.Set() |> ignore)。
    /// 同一個 sink 由 handler 序列呼叫；callback 不得反向同步 Ask 該 handler。
    /// 一則訊息可有 Delta 或只有 Completed；AgentEnd 是 turn boundary，不是第二份答案。
    let createSink width (write: string -> unit) (onEnd: unit -> unit) =
        let messages = Dictionary<string,string>()
        let mutable lastResponse = ""
        let mutable lastMessage = ""
        let mutable phase = ""
        let mutable column = 0
        let mutable previousCR = false
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
                    | 10 -> if not previousCR then newLine ()
                            previousCR <- false
                    | _ ->
                        previousCR <- false
                        if width > 0 && column >= width then newLine ()
                        write (rune.ToString())
                        column <- column + 1
        let display name completed id text =
            let previous = match messages.TryGetValue id with true,value -> value | _ -> ""
            // 同一 turn 的多則訊息要分行；同一則訊息的連續 Delta 不硬插換行。
            if lastMessage <> id then finishLine (); lastMessage <- id
            let full = if completed then text else previous + text
            if not completed then print name text
            elif full.StartsWith(previous,StringComparison.Ordinal) then print name (full.Substring previous.Length)
            elif full <> previous then
                finishLine ()
                print (name + " corrected") full
            messages[id] <- full
            if name = "Response" then lastResponse <- full
        let data name = function
            | MessageDelta(id,text) -> display name false id text
            | MessageCompleted(id,text) -> display name true id text
            | Message text -> display name false ("legacy-" + name) text
            | _ -> ()
        fun (_,target,event: ReActEvent<string>) ->
            if target = ReActEndpoint.Human then
                match event with
                | AgentCot(_,_,payload) -> data "COT" payload
                | AgentWork(_,_,payload) -> data "Response" payload
                | AgentEnd(_,_,Message text) ->
                    if text.StartsWith(lastResponse,StringComparison.Ordinal) then print "Response" (text.Substring lastResponse.Length)
                    elif text <> lastResponse then print "Final" text
                    finishLine ()
                    newLine ()
                    phase <- ""
                    lastMessage <- ""
                    lastResponse <- ""
                    messages.Clear()
                    onEnd ()
                | StdErr(_,_,text) -> print "Error" text; finishLine ()
                | _ -> ()
