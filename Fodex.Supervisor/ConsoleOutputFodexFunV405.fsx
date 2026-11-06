namespace Fodex.Supervisor

open System
open System.IO
open System.Diagnostics
open System.Text
open Microsoft.FSharp.Quotations
open LoggerUtil
open Argu
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// M018 的 Console 邊界；不持有 participant、turn 或 actor 執行狀態。
/// 格式化政策可經 registry 替換；TextReader/TextWriter 只是本機 console 的接線。
type ConsoleEditorInputV405 = UniContext * TextReader * TextWriter * (unit -> string) * FeasibleT<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep,exn>

/// 只屬於本機呈現端的設定；不加入 wrapper 的遠端命令／Contract。
type ConsoleLabelsArgumentV405 =
    | Labels of string
    interface IArgParserTemplate with
        member _.Usage = "/labels on|off: show or hide event and native I/O metadata labels."

/// 時間只在新提示或輸出後重顯時取樣，不設 timer、不在每個 key 上改變時間。
module ConsoleOutputFodexFunV405 =
    let version=System.Version(4,0,5)
    let target="Console.CreateInteractive"
    let targetPrompt="Console.Prompt"
    let targetFormat="Console.FormatEvent"
    let targetNativeFormat="Console.FormatNativeOutput"
    let targetLabels="Console.ParseLabels"
    let targetStream="Wrapper.FormatOutput"
    let prompt (now:DateTime) = Ok("fdx."+now.ToString("yyMMddHHmmss",Globalization.CultureInfo.InvariantCulture)+"> ")

    /// 只拿掉 metadata；正文逐字保留，AgentEnd 不再重印已顯示的回答。
    /// 例：format(false, AgentWork(..., MessageCompleted("id","answer"))) = Ok(Some "answer")。
    /// errors 保留識別標記，避免被誤認成模型的正常回答。
    let format (labels,event:ReActEvent<string>) =
        if labels then ConsoleOutputFodexFunV300.format event else
        match event with
        | AgentWork(_,_,MessageCompleted(_,text)) | AgentCot(_,_,MessageCompleted(_,text)) -> Ok(Some text)
        | AgentEnd _ -> Ok None
        | _ -> ConsoleOutputFodexFunV300.format event

    /// 接收 typed native batch；不從字串猜測或刪除第一行，正文可能本來就含有 [native ...]。
    /// /show 的過濾已由 output hub 完成；labels 不改變 show/trace 或任何持久化內容。
    let formatNative (labels,batch) =
        if labels then NativeOutputFodexFunV300.format batch else
        let (NativeOutputBatch(_,_,_,_,text,_))=batch
        Ok text

    /// --json 使用現有 Fodex.ReActEdge schema 3，可用 ReActJsonV300.deserialize 還原完整 typed edge。
    /// source/target、event DU、StreamId/causedBy 均保留；不把 raw native stdout 塞進事件通道。
    /// Console 與 service 共用這個輸出邊界；supervisee 直接轉送產出的 string，不再次序列化。
    let formatStream (json,labels,edge:ReActDataStream<string>) =
        if json then ReActJsonV300.serialize edge |> Result.map Some
        else let _,_,event=edge in format(labels,event)

    let labelsParser=ArgumentParser.Create<ConsoleLabelsArgumentV405>(programName="/labels")
    let parseLabels (ctx:UniContext,text:string) =
        try
            let text=text.Trim()
            if text="/labels" || text.StartsWith("/labels ",StringComparison.Ordinal) || text.StartsWith("/labels\t",StringComparison.Ordinal) then
                // 正規化 slash 指令；token、參數個數與未知參數檢查全部交 Argu。
                let argv=PL.parseLine ctx [|' ';'\t'|] (Some '"') None true ("--labels"+text.Substring(7))
                let options=labelsParser.ParseCommandLine(argv,raiseOnUsage=false)
                match options.GetResult Labels |> fun value -> value.ToLowerInvariant() with
                | "on" | "true" -> Ok(Some true)
                | "off" | "false" -> Ok(Some false)
                | _ -> Error(ArgumentException "Use /labels on or /labels off" :> exn)
            else Ok None
        with error -> Error error

    /// 回傳 readLine / writeBlock；後者的第一個參數表示「印完後重顯 pending read 的 prompt」。
    /// 例：let readLine,writeBlock=create ctx Console.In Console.Out (fun () -> "fdx.261106120000> ")
    ///     writeBlock true "[AgentEnd] stream=1050 causedBy=592"
    ///     writeBlock true "" // WrapperRequest 完成 callback：結果已由 sink 印出，只重顯提示。
    ///
    /// Console.ReadLine 沒有可靠的取消機制，因此完成通知只重顯提示，不建立第二個 reader。
    /// readLine 在原呼叫執行緒阻塞，沒有 polling、timer 或每筆訊息的背景 reader。
    /// 不嘗試把輸入列固定在畫面底部；這仍是 plain console，不是 TUI。
    let createCore (ctx:UniContext) (input:TextReader) (output:TextWriter) (promptFactory:unit -> string)
                   (editKey: FeasibleT<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep,exn> option) =
        let mutable prompt=""
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
            if lines.Length=0 then prompt<-promptFactory()
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
                        if lines.Length=0 then prompt<-promptFactory()
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
            if not(Object.ReferenceEquals(input,Console.In) && Object.ReferenceEquals(output,Console.Out)) || Console.IsInputRedirected || Console.IsOutputRedirected then false else
            try
                Console.KeyAvailable |> ignore
                let struct(left,top)=Console.GetCursorPosition()
                Console.SetCursorPosition(left,top)
                Console.TreatControlCAsInput<-Console.TreatControlCAsInput
                Console.BufferWidth>=10 && Console.BufferHeight>0
            with _ -> false
        let read,write=createCore ctx input output prompt (if supportsKeys then Some editKey else None)
        read,write,supportsKeys

    let quotation : Microsoft.FSharp.Quotations.Expr<ConsoleEditorInputV405 -> FeasibleT<_,(unit -> string) * (bool -> string -> unit) * bool,exn>> =
        <@ fun _ -> fun (ctx,input,output,prompt,edit) ->
            try Ok(createInteractive ctx input output prompt edit) with error -> Error error @>
    let promptQuotation : Microsoft.FSharp.Quotations.Expr<DateTime -> FeasibleT<_,string,exn>> =
        <@ fun _ -> fun input -> prompt input @>
    let formatQuotation : Expr<(bool*ReActEvent<string>) -> FeasibleT<_,string option,exn>> = <@ fun _ -> fun input -> format input @>
    let nativeFormatQuotation : Expr<(bool*NativeOutputBatchV300) -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> formatNative input @>
    let labelsQuotation : Expr<(UniContext*string) -> FeasibleT<_,bool option,exn>> = <@ fun _ -> fun input -> parseLabels input @>
    let streamQuotation : Expr<(bool*bool*ReActDataStream<string>) -> FeasibleT<_,string option,exn>> = <@ fun _ -> fun input -> formatStream input @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare target version quotation
            FodexFunRegistry.declare targetPrompt version promptQuotation
            FodexFunRegistry.declare targetFormat version formatQuotation
            FodexFunRegistry.declare targetNativeFormat version nativeFormatQuotation
            FodexFunRegistry.declare targetLabels version labelsQuotation
            FodexFunRegistry.declare targetStream version streamQuotation
        |]
