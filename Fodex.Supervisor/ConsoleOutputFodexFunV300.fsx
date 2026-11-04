namespace Fodex.Supervisor

open System
open System.IO
open System.Diagnostics
open System.Text
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// M018 的 Console 邊界；不持有 participant、turn 或 actor 執行狀態。
/// 格式化政策可經 registry 替換；TextReader/TextWriter 只是本機 console 的接線。
module ConsoleOutputFodexFunV300 =
    let version=System.Version(3,0,0)
    let target="Console.FormatEvent"

    /// 只選取完整訊息與 wrapper 診斷；AgentEnd 是回合終點，不重印其 Message。
    /// 例：MessageCompleted(_, "甲\n\n乙\n") 保留原文的段落與結尾 LF。
    /// 不使用 Trim/ReplaceLineEndings：空行可能是 agent 的 Markdown/code block 內容。
    let format (event:ReActEvent<string>) : Result<string option,exn> =
        let message heading text=Some(if String.IsNullOrEmpty text then heading else heading+"\n"+text)
        let diagnostic heading text=
            if String.IsNullOrWhiteSpace text then None else
            // 多行清單的第一個 key 也要維持原縮排；[wrapper] 不擠在第一欄前面。
            let separator=if text.Contains '\n' || text.Contains '\r' then "\n" else " "
            Some(heading+separator+text)
        Ok(match event with
           | AgentWork(_,_,MessageCompleted(_,text)) -> message "[AgentWork completed]" text
           | AgentCot(_,_,MessageCompleted(_,text)) -> message "[AgentCot completed]" text
           | StdOut(_,_,text) -> diagnostic "[wrapper]" text
           | StdErr(_,_,text) -> diagnostic "[error]" text
           | AgentEnd(id,cause,_) -> Some(sprintf "[AgentEnd] stream=%d causedBy=%d" id cause)
           | _ -> None)

    let quotation : Expr<ReActEvent<string> -> FeasibleT<_,string option,exn>> =
        <@ fun _ -> fun event -> format event @>
    let register node=FodexFunRegistry.register node target version quotation

    /// 回傳 readLine / writeBlock；後者的第一個參數表示「印完後重顯 pending read 的 prompt」。
    /// 例：let readLine,writeBlock=create ctx Console.In Console.Out "M018> "
    ///     writeBlock true "[AgentEnd] stream=1050 causedBy=592"
    ///     writeBlock true "" // WrapperRequest 完成 callback：結果已由 sink 印出，只重顯提示。
    ///
    /// Console.ReadLine 沒有可靠的取消機制，因此完成通知只重顯提示，不建立第二個 reader。
    /// readLine 在原呼叫執行緒阻塞，沒有 polling、timer 或每筆訊息的背景 reader。
    /// 不嘗試把輸入列固定在畫面底部；這仍是 plain console，不是 TUI。
    let createCore (ctx:UniContext) (input:TextReader) (output:TextWriter) (prompt:string)
                   (editKey: FeasibleT<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep,exn> option) =
        let gate=obj()
        let mutable reading=false
        let mutable promptOnLine=false
        let mutable readNumber=0L
        // 僅在實體 console 啟用的小型行編輯狀態；已按 Shift+Enter 的行不再重新編輯。
        let line=StringBuilder()
        let lines=StringBuilder()
        let mutable cursor=0
        let mutable anchorRow=0
        let mutable renderedRows=0
        let currentPrompt ()=if lines.Length=0 then prompt else "....> "
        let trace stage characters elapsed status =
            // 時間戳由 NLog 配置提供；不把 prompt/response 原文或 exception.Message 放進 trace。
            ctx.logger.Info(sprintf "FODEX_CONSOLE stage=%s read=%d pending=%b characters=%d elapsedMs=%.3f status=%s"
                                stage readNumber reading characters elapsed status)
        let showPrompt stage =
            if editKey.IsSome then
                anchorRow<-Console.CursorTop
                renderedRows<-1
            output.Write(currentPrompt())
            output.Flush()
            promptOnLine<-true
            trace stage prompt.Length 0. "ok"
        let place row column =
            Console.SetCursorPosition(max 0 (min column (Console.BufferWidth-1)),max 0 (min row (Console.BufferHeight-1)))
        let eraseEditor () =
            let width=max 8 (Console.BufferWidth-2)
            for i in 0..renderedRows-1 do
                place (anchorRow+i) 0
                output.Write(String(' ',width))
            place anchorRow 0
            renderedRows<-0
            promptOnLine<-false
        let renderEditor atEnd =
            let width=max 8 (Console.BufferWidth-2)
            let rows,(row,column)=ConsoleInputFodexFunV300.layout (currentPrompt()) (line.ToString())
                                     (if atEnd then line.Length else cursor) width
            let count=max rows.Length renderedRows
            place anchorRow 0
            for i in 0..count-1 do
                let text=if i<rows.Length then rows[i] else ""
                output.Write(String(' ',width))
                place Console.CursorTop 0
                output.Write text
                if i<count-1 then
                    let before=Console.CursorTop
                    output.WriteLine()
                    if Console.CursorTop<=before then anchorRow<-max 0 (anchorRow-1)
            renderedRows<-rows.Length
            place (anchorRow+row) column
            output.Flush()
            promptOnLine<-true
        let readKeys edit =
            let mutable result: string option=None
            let mutable bufferedKeys=0
            while result.IsNone do
                // ReadKey 是唯一的實體 input consumer；等待按鍵時不持有 output lock。
                let key=Console.ReadKey true
                lock gate (fun () ->
                    let action=match edit(line,cursor,key) with Ok action -> action | Error ex -> raise ex
                    match action with
                    | Continue next ->
                        cursor<-next
                        bufferedKeys<-bufferedKeys+1
                        // 貼上文字每最多 64 keys 重畫一次，避免每個字都重畫整行。
                        if bufferedKeys>=64 || not Console.KeyAvailable then renderEditor false;bufferedKeys<-0
                    | Finish more ->
                        renderEditor true
                        output.WriteLine()
                        renderedRows<-0
                        promptOnLine<-false
                        if more then
                            lines.Append(line.ToString()).Append('\n') |> ignore
                            line.Clear() |> ignore
                            cursor<-0
                            trace "read.continue" lines.Length 0. "newline"
                            showPrompt "prompt.continue"
                        else result<-Some(lines.ToString()+line.ToString())
                    | EndOfInput ->
                        renderEditor true
                        output.WriteLine()
                        result<-Some null)
            result.Value
        let readLine () =
            let watch=Stopwatch.StartNew()
            lock gate (fun () ->
                if reading then invalidOp "This console already has a pending reader"
                reading<-true
                readNumber<-readNumber+1L
                line.Clear() |> ignore
                lines.Clear() |> ignore
                cursor<-0
                showPrompt "prompt.read"
                trace "read.begin" 0 0. "pending")
            let mutable characters=0
            let mutable status="failed"
            try
                let text=
                    match editKey with
                    | Some edit ->
                        // FSI 自己也處理 Ctrl+C；讀鍵期間把它當輸入，由我們回傳 EOF 走 caller finally。
                        // 無論成功、EOF 或例外，都還原 host 原本設定；沒有第二個 reader 或 signal thread。
                        let original=Console.TreatControlCAsInput
                        try
                            Console.TreatControlCAsInput<-true
                            readKeys edit
                        finally Console.TreatControlCAsInput<-original
                    | None -> input.ReadLine()
                characters<-if isNull text then 0 else text.Length
                status<-if isNull text then "eof" elif String.IsNullOrWhiteSpace text then "empty" else "line"
                text
            finally
                lock gate (fun () ->
                    reading<-false
                    promptOnLine<-false
                    trace "read.end" characters watch.Elapsed.TotalMilliseconds status)
        let writeBlock showPromptAfter (text:string) =
            lock gate (fun () ->
                let hasText=not(String.IsNullOrEmpty text)
                if hasText then
                    if reading && editKey.IsSome then eraseEditor()
                    // pending prompt 還在本行時只結束這一行；後續 output 不再插入空白行。
                    if promptOnLine then output.WriteLine();promptOnLine<-false
                    output.Write text
                    if not(text.EndsWith '\n' || text.EndsWith '\r') then output.WriteLine()
                    output.Flush()
                    trace "write" text.Length 0. "ok"
                    if reading && editKey.IsSome then
                        // actor 輸出後恢復當前輸入行；同一個 reader 繼續，不丟部分輸入。
                        anchorRow<-Console.CursorTop
                        renderEditor false
                // 指令結果由 sink 先寫入；callback 可以只傳空字串重顯，不重印結果。
                // callback 比下一次 read 更早到達時不做事；readLine 自己會顯示提示。
                if showPromptAfter && reading && not promptOnLine then
                    showPrompt (if hasText then "prompt.agent-end" else "prompt.command"))
        readLine,writeBlock

    /// TextReader 版本保留給 redirected stdin、FSI 面板與原有測試。
    let create ctx input output prompt = createCore ctx input output prompt None

    /// 沿用 FSI 的 console capability probe；失敗就回到 TextReader，不要求 Windows target。
    /// 回傳額外 bool 供 demo 顯示目前是否有鍵盤修飾鍵支援。
    let createInteractive ctx input output prompt editKey =
        let supportsKeys=
            if Console.IsInputRedirected || Console.IsOutputRedirected then false else
            try
                Console.KeyAvailable |> ignore
                let struct(left,top)=Console.GetCursorPosition()
                Console.SetCursorPosition(left,top)
                Console.TreatControlCAsInput<-Console.TreatControlCAsInput
                Console.BufferWidth>=10 && Console.BufferHeight>0
            with _ -> false
        let read,write=createCore ctx input output prompt (if supportsKeys then Some editKey else None)
        read,write,supportsKeys

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
