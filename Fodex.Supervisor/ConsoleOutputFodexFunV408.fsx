namespace Fodex.Supervisor

open System
open System.IO
open System.Text
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// This editor returns a complete ;;-terminated submission, rather than a physical line.
type ConsoleSubmissionEditorInputV408 = ConsoleEditorInputV405 * ConsolePromptHistoryV100.History

module ConsoleOutputFodexFunV408 =
    let version=Version(4,0,8)
    let target="Console.CreateSubmissionEditor"

    /// Use the existing grapheme/cell policy, adding real newlines for recalled multiline prompts.
    let layout (prompt:string) (text:string) cursor width =
        let width=max 8 width
        let source=prompt+text
        let targetIndex=prompt.Length+cursor
        let rows=ResizeArray<string>()
        let row=StringBuilder()
        let mutable column=0
        let mutable point=0,0
        let starts=StringInfo.ParseCombiningCharacters source
        for i in 0..starts.Length-1 do
            let start=starts[i]
            let finish=if i+1<starts.Length then starts[i+1] else source.Length
            let element=source.Substring(start,finish-start)
            if element<>"\n" && column+ConsoleInputFodexFunV300.elementWidth element>width then
                rows.Add(row.ToString());row.Clear() |> ignore;column<-0
            if start=targetIndex then point<-rows.Count,column
            if element="\n" then
                rows.Add(row.ToString());row.Clear().Append("....> ") |> ignore;column<-6
            else
                row.Append element |> ignore
                column<-column+ConsoleInputFodexFunV300.elementWidth element
        if targetIndex=source.Length then point<-rows.Count,column
        rows.Add(row.ToString())
        rows.ToArray(),point

    let edit (history:ConsolePromptHistoryV100.History) fallback (buffer:StringBuilder,cursor,key:ConsoleKeyInfo) =
        match key.Key with
        | ConsoleKey.UpArrow | ConsoleKey.DownArrow ->
            match history.Browse(key.Key=ConsoleKey.UpArrow) with
            | Some text -> buffer.Clear().Append(text) |> ignore;Ok(Continue buffer.Length)
            | None -> Ok(Continue cursor)
        | _ -> fallback(buffer,cursor,key)

    let create (((ctx,input,output,promptFactory,editKey),history):ConsoleSubmissionEditorInputV408) =
        // Reuse V407's exact reader/writer identity and native capability probe/fallback.
        let readLine,writeLine,physical=ConsoleOutputFodexFunV407.createInteractive(ctx,input,output,promptFactory,editKey)
        if not physical then
            let read ()=
                let pending=ConsolePolicyFodexFunV403.submission((fun _ -> Task.FromResult(readLine())),CancellationToken.None)
                pending.GetAwaiter().GetResult() |> Option.toObj
            read,writeLine,false
        else
            let gate=obj()
            let buffer=StringBuilder()
            let mutable cursor=0
            let mutable prompt=""
            let mutable reading=false
            let mutable anchor=0
            let mutable rendered=0
            let restore ()=ConsoleOutputFodexFunV407.restoreCursor true
            let place row column=Console.SetCursorPosition(max 0 (min column (Console.BufferWidth-1)),max 0 (min row (Console.BufferHeight-1)))
            let render ()=
                let width=max 8 (Console.BufferWidth-2)
                let rows,(row,column)=layout prompt (buffer.ToString()) cursor width
                let count=max rows.Length rendered
                place anchor 0
                for i in 0..count-1 do
                    output.Write(String(' ',width))
                    place Console.CursorTop 0
                    if i<rows.Length then output.Write rows[i]
                    if i<count-1 then
                        let before=Console.CursorTop
                        output.WriteLine()
                        if Console.CursorTop<=before then anchor<-max 0 (anchor-1)
                rendered<-rows.Length
                place (anchor+row) column
                output.Flush()
                restore()
            let erase ()=
                for i in 0..rendered-1 do
                    place (anchor+i) 0
                    output.Write(String(' ',max 8 (Console.BufferWidth-2)))
                place anchor 0
                rendered<-0
            let read ()=
                lock gate (fun () ->
                    history.ResetBrowse()
                    buffer.Clear() |> ignore
                    cursor<-0;prompt<-promptFactory();anchor<-Console.CursorTop;reading<-true
                    render())
                let original=Console.TreatControlCAsInput
                try
                    Console.TreatControlCAsInput<-true
                    let mutable result:string option=None
                    let mutable eof=false
                    let mutable buffered=0
                    while result.IsNone && not eof do
                        let key=Console.ReadKey true
                        lock gate (fun () ->
                            match edit history editKey (buffer,cursor,key) |> Result.defaultWith raise with
                            | Continue next ->
                                cursor<-next;buffered<-buffered+1
                                if buffered>=64 || not Console.KeyAvailable then render();buffered<-0
                            | Finish more ->
                                let text=buffer.ToString()
                                let boundary=text.LastIndexOf '\n'
                                if not more && text.Substring(boundary+1).Trim()=";;" then
                                    cursor<-buffer.Length;render();output.WriteLine();rendered<-0
                                    result<-Some(if boundary<0 then "" else text.Substring(0,boundary))
                                else
                                    buffer.Append '\n' |> ignore
                                    cursor<-buffer.Length
                                    render()
                            | EndOfInput ->
                                cursor<-buffer.Length;render();output.WriteLine();rendered<-0;eof<-true)
                    result |> Option.toObj
                finally
                    lock gate (fun () -> reading<-false;restore())
                    Console.TreatControlCAsInput<-original
            let write show text =
                lock gate (fun () ->
                    if not(String.IsNullOrEmpty text) then
                        if reading then erase()
                        output.Write text
                        if not(text.EndsWith '\n' || text.EndsWith '\r') then output.WriteLine()
                        output.Flush()
                        if reading then
                            prompt<-promptFactory();anchor<-Console.CursorTop
                            render()
                    elif show && reading then render()
                    restore())
            read,write,true

    let quotation:Expr<ConsoleSubmissionEditorInputV408 -> FeasibleT<_,(unit -> string)*(bool -> string -> unit)*bool,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with error -> Error error @>
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
