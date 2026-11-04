namespace Fodex.Supervisor

open System
open System.Text
open System.Globalization
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Continue 的 cursor 是 UTF-16 buffer index；Finish 不截斷游標之後的文字。
/// 不使用 exit：EOF 交回 demo，使既有 finally/stop 仍會執行。
type ConsoleInputStep =
    | Continue of cursor:int
    | Finish of continueOnNextLine:bool
    | EndOfInput

/// /exit 是 console host 的生命週期指令；結束 host 前仍由既有 IAgent/cleanup 清理。
type ConsoleHostArgumentV300 =
    | Exit
    interface IArgParserTemplate with member _.Usage="/exit：清理 participant 後離開 M018"

/// M018 暫用的小型行編輯政策。參考 dotnet/fsharp 的 StringBuilder + ReadKey 做法；
/// 只保留基本編輯，不攜帶 FSI evaluator、completion、history 或 Windows UI framework。
module ConsoleInputFodexFunV300 =
    let version=System.Version(3,0,0)
    let target="Console.EditLine"
    let targetExit="Console.ExitRequested"
    let hostParser=ArgumentParser.Create<ConsoleHostArgumentV300>("M018")

    /// 只攔截 host 的 /exit；一般文字及其他 wrapper 指令維持原路徑。
    /// 例：/exit -> Ok true；/exit extra -> Error；「請說明 /exit」-> Ok false。
    /// 仍用既有 PL + Argu 解析，避免手寫另一套 flag/argument parser。
    let exitRequested (ctx:UniContext) (text:string) : Result<bool,exn> =
        try
            let input=if isNull text then "" else text.Trim()
            if not(input.StartsWith("/exit",StringComparison.Ordinal)) then Ok false else
                let arguments=PL.parseLine ctx [|' ';'\t';'\r';'\n'|] (Some '"') None true ("--"+input.Substring 1)
                let parsed=hostParser.ParseCommandLine(arguments,raiseOnUsage=false)
                if parsed.IsUsageRequested || parsed.GetAllResults()<>[ConsoleHostArgumentV300.Exit] then invalidArg "input" "Use /exit without arguments"
                Ok true
        with ex -> Error ex
    let exitQuotation ctx : Expr<string -> FeasibleT<_,bool,exn>> =
        <@ fun _ -> fun input -> exitRequested ctx input @>
    /// host parser 需 ctx 給 PL；按鍵編輯不需 ctx，因此維持原本獨立 register。
    let registerExit node ctx=FodexFunRegistry.register node targetExit version (exitQuotation ctx)

    /// Backspace/Delete/左右移動以 text element 為單位，避免拆開 surrogate pair 或組合字。
    let previous (text:string) cursor =
        StringInfo.ParseCombiningCharacters text |> Array.filter(fun i -> i<cursor) |> Array.tryLast |> Option.defaultValue 0
    let next (text:string) cursor =
        StringInfo.ParseCombiningCharacters text |> Array.tryFind(fun i -> i>cursor) |> Option.defaultValue text.Length

    /// input 的 buffer 只由單一 console reader 持有；operation 修改它並回傳下一個動作。
    /// 例：buffer="abc", cursor=2, Enter -> Finish false，buffer 仍是完整的 "abc"。
    /// Shift+Enter -> Finish true；外層累積本行與 LF，繼續讀下一行而不送 wrapper。
    let edit ((buffer:StringBuilder),cursor,(key:ConsoleKeyInfo)) : Result<ConsoleInputStep,exn> =
        try
            if cursor<0 || cursor>buffer.Length then invalidArg "cursor" "Cursor is outside the input buffer"
            let ctrl=key.Modifiers.HasFlag ConsoleModifiers.Control
            let remove first count = buffer.Remove(first,count) |> ignore;Continue first
            let insert (value:string) = buffer.Insert(cursor,value) |> ignore;Continue(cursor+value.Length)
            Ok(match key.Key with
               | ConsoleKey.Enter -> Finish(key.Modifiers.HasFlag ConsoleModifiers.Shift)
               | ConsoleKey.J when ctrl -> Finish true // 可用 Ctrl+J 作終端未傳送 Shift+Enter 時的替代鍵。
               | ConsoleKey.C when ctrl -> EndOfInput // 不送出半成品；caller 的 finally 執行正常清理。
               | ConsoleKey.Z | ConsoleKey.D when ctrl && buffer.Length=0 -> EndOfInput
               | ConsoleKey.Home | ConsoleKey.A when key.Key=ConsoleKey.Home || ctrl -> Continue 0
               | ConsoleKey.End | ConsoleKey.E when key.Key=ConsoleKey.End || ctrl -> Continue buffer.Length
               | ConsoleKey.LeftArrow -> Continue(previous (buffer.ToString()) cursor)
               | ConsoleKey.RightArrow -> Continue(next (buffer.ToString()) cursor)
               | ConsoleKey.Backspace when cursor>0 ->
                   let start=previous (buffer.ToString()) cursor
                   remove start (cursor-start)
               | ConsoleKey.Delete when cursor<buffer.Length -> remove cursor (next (buffer.ToString()) cursor-cursor)
               | ConsoleKey.U when ctrl -> remove 0 cursor
               | ConsoleKey.K when ctrl -> remove cursor (buffer.Length-cursor)
               | ConsoleKey.Escape -> buffer.Clear() |> ignore;Continue 0
               | ConsoleKey.Tab -> insert(String(' ',4-cursor%4))
               | _ when key.KeyChar<>'\000' && not(Char.IsControl key.KeyChar) -> insert(string key.KeyChar)
               | _ -> Continue cursor) // IME/功能鍵可能回傳 KeyChar=0，不把它放進 prompt。
        with ex -> Error ex

    let quotation : Expr<(StringBuilder*int*ConsoleKeyInfo) -> FeasibleT<_,ConsoleInputStep,exn>> =
        <@ fun _ -> fun input -> edit input @>
    let register node=FodexFunRegistry.register node target version quotation

    // fullWidthCharRanges / BinarySearch 判定取自 dotnet/fsharp src/fsi/console.fs。
    // Copyright (c) Microsoft Corporation. MIT license；來源 commit、原文與完整授權見
    // ../doc/FsiConsoleImplementationAnalysis.md。其餘輸入政策是 M018 的小型適配。
    let fullWidthCharRanges =
        Array.concat
            [|
                [| '\u1100'; '\u115f' |]
                [| '\u231a'; '\u231b' |]
                [| '\u2329'; '\u232a' |]
                [| '\u23e9'; '\u23ec' |]
                [| '\u23f0'; '\u23f0' |]
                [| '\u23f3'; '\u23f3' |]
                [| '\u25fd'; '\u25fe' |]
                [| '\u2614'; '\u2615' |]
                [| '\u2648'; '\u2653' |]
                [| '\u267f'; '\u267f' |]
                [| '\u2693'; '\u2693' |]
                [| '\u26a1'; '\u26a1' |]
                [| '\u26aa'; '\u26ab' |]
                [| '\u26bd'; '\u26be' |]
                [| '\u26c4'; '\u26c5' |]
                [| '\u26ce'; '\u26ce' |]
                [| '\u26d4'; '\u26d4' |]
                [| '\u26ea'; '\u26ea' |]
                [| '\u26f2'; '\u26f3' |]
                [| '\u26f5'; '\u26f5' |]
                [| '\u26fa'; '\u26fa' |]
                [| '\u26fd'; '\u26fd' |]
                [| '\u2705'; '\u2705' |]
                [| '\u270a'; '\u270b' |]
                [| '\u2728'; '\u2728' |]
                [| '\u274c'; '\u274c' |]
                [| '\u274e'; '\u274e' |]
                [| '\u2753'; '\u2755' |]
                [| '\u2757'; '\u2757' |]
                [| '\u2795'; '\u2797' |]
                [| '\u27b0'; '\u27b0' |]
                [| '\u27bf'; '\u27bf' |]
                [| '\u2b1b'; '\u2b1c' |]
                [| '\u2b50'; '\u2b50' |]
                [| '\u2b55'; '\u2b55' |]
                [| '\u2e80'; '\u303e' |]
                [| '\u3041'; '\u3096' |]
                [| '\u3099'; '\u30ff' |]
                [| '\u3105'; '\u312f' |]
                [| '\u3131'; '\u318e' |]
                [| '\u3190'; '\u3247' |]
                [| '\u3250'; '\u4dbf' |]
                [| '\u4e00'; '\ua4c6' |]
                [| '\ua960'; '\ua97c' |]
                [| '\uac00'; '\ud7a3' |]
                [| '\uf900'; '\ufaff' |]
                [| '\ufe10'; '\ufe1f' |]
                [| '\ufe30'; '\ufe6b' |]
                [| '\uff01'; '\uff60' |]
                [| '\uffe0'; '\uffe6' |]
            |]

    let elementWidth (text:string) =
        let n=Array.BinarySearch(fullWidthCharRanges,text[0])
        let valid,value=Rune.TryGetRuneAt(text,0)
        let rune=if valid then value.Value else int text[0]
        if n>=0 || n%2=0 || (rune>=0x1F000 && rune<=0x1FAFF) || rune>=0x20000 then 2 else 1

    /// FSI 將 buffer index 與螢幕 cell 分開；這裡同樣保留 Unicode text element 邊界。
    /// 回傳逐列文字與 caret 的 row/column。不同終端的 emoji 字寬仍由其字型決定。
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
            let cells=elementWidth element
            if column+cells>width then rows.Add(row.ToString());row.Clear() |> ignore;column<-0
            if start<=targetIndex then point<-rows.Count,column
            row.Append element |> ignore
            column<-column+cells
        if targetIndex=source.Length then point<-rows.Count,column
        rows.Add(row.ToString())
        rows.ToArray(),point

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
