# FSI Console 輸入實作技術分析

2026-11-03；用途：M018 過渡 console，以及日後實作可重用的互動輸入層。

## 來源與閱讀範圍

- 上游：dotnet/fsharp，固定 commit `fcc3990af38079c9b6c94fa3505d653d1b4d1a73`。
- 參考 checkout：`F:\fsharp-fodex-reference`（depth 1 / sparse checkout）；不是 Fodex build dependency。
- 核心：`src/fsi/console.fs`；接線：`src/fsi/fsimain.fs`；host contract：`src/Compiler/Interactive/fsi.fs`。
- 本文是 console 輸入／行編輯的技術分析，不代表已 review F# compiler、evaluation 或 Visual Studio 整個 subsystem。
- 以下程式碼框直接擷取該 commit 原始行，保留上游命名與存取修飾字；它們是上下文片段，不是每段都能獨立 #load。Fodex 新增程式仍遵守本專案的命名／存取規則。
- 上游授權為 MIT，全文附於文末；移植片段需保留 attribution 與授權。

## 核心結論

FSI 的進階 console 並非在 System.Console.ReadLine 旁另掛 KeyDown event。它用唯一的 `Console.ReadKey true` 取得按鍵，自己持有 StringBuilder、文字 index、螢幕 anchor、已繪製寬度，再由 ReadLine() 對 caller 回傳一整行字串。這與 FSI evaluator 分層；上層處理 F# interaction 邊界，console reader 不負責執行使用者程式。

這份實作的 Enter case 沒有檢查 Shift；Shift 在 Tab completion 的反向選擇用到。因此不能說「原版 FSI 已經替我們支援 Shift+Enter」。M018 需要在明確取得 ConsoleKeyInfo 後加入自己的提交政策。

可借用的是 buffer 與螢幕分離、IME 非字元過濾、基本編輯與單一 reader。M018 不搬 evaluator、語意 completion、歷史搜尋或 WinForms event loop；正式輸入只使用 System.Console 等跨平台 .NET API，不新增 Windows desktop target 或 UI 套件。


## 1. 先 probe console，再提供可選的讀取函數

**What**：host 建立 ReadLineConsole，檢查 KeyAvailable、顏色與游標 API 能否使用，再回傳 `Some (unit -> string)`。

**Why**：互動 terminal、重導向 stdin、IDE 的文字輸入 pane 不具有完全相同的能力。不能因為程式是 console executable，就假設一定可讀修飾鍵或定位游標。

**How**：能力不足時回傳 None，由 evaluation host 選擇一般 TextReader。UI 能力與 evaluator 不綁死。

來源：[src/fsi/fsimain.fs:188](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/fsimain.fs#L188-L210)

```fsharp
        let console = new FSharp.Compiler.Interactive.ReadLineConsole()

        // Define the function we pass to the FsiEvaluationSession
        let getConsoleReadLine (probeToSeeIfConsoleWorks) =
            let consoleIsOperational =
                if probeToSeeIfConsoleWorks then
                    //if progress then fprintfn outWriter "probing to see if console works..."
                    try
                        // Probe to see if the console looks functional on this version of .NET
                        let _ = Console.KeyAvailable
                        let _ = Console.ForegroundColor
                        let _ = Console.CursorLeft <- Console.CursorLeft
                        true
                    with _ ->
                        //if progress then fprintfn outWriter "probe failed, we have no console..."
                        false
                else
                    true

            if consoleIsOperational then
                Some(fun () -> console.ReadLine())
            else
                None
```

套用要點：M018 同樣 probe，不成功就保留逐行 TextReader。這個 fallback 不宣稱能判斷 Shift；自動化仍可透過字串／actor API 傳完整多行內容。


## 2. 特殊 reader 是 host 的一個 callback

**What**：host contract 將 console 讀取表示為可選 callback。

**Why**：先讀第一行的 peekahead、之後逐行讀取、非 console TextReader 可以由 host 統一安排。

**How**：原文同時解釋 peekahead 的背景讀取。這是 FSI 的啟動／lexer 策略，不能把它誤解成可同時再開另一個 reader 攔截按鍵。原文開關註解需對照該版本的實際 options，不能單靠註解推論目前 CLI 預設。

來源：[src/Compiler/Interactive/fsi.fs:568](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/Compiler/Interactive/fsi.fs#L568-L586)

```fsharp
    /// Returning 'Some' indicates a console is to be used, so some special rules apply.
    ///
    /// A "console" gets used if
    ///     --readline- is specified (the default on Windows + .NET); and
    ///     not --fsi-server (which should always be combined with --readline-); and
    ///     GetOptionalConsoleReadLine() returns a Some
    ///
    /// "Peekahead" occurs if --peekahead- is not specified (i.e. it is the default):
    ///     - If a console is being used then
    ///         - a prompt is printed early
    ///         - a background thread is created
    ///         - the GetOptionalConsoleReadLine() callback is used to read the first line
    ///     - Otherwise call inReader.Peek()
    ///
    /// Further lines are read as follows:
    ///     - If a console is being used then use GetOptionalConsoleReadLine()
    ///     - Otherwise use inReader.ReadLine()

    abstract GetOptionalConsoleReadLine: probeToSeeIfConsoleWorks: bool -> (unit -> string) option
```

套用要點：M018 保留一個 read owner。actor 可並行輸出，但不能另建 ReadKey／ReadLine consumer 搶同一個 stdin。


## 3. 文字 buffer、文字 index 與螢幕寬度分開

**What**：StringBuilder 保存完整文字，current 是字串中的位置，rendered 則是上次實際繪出的 cell 數。

**Why**：中文可能占兩格，控制字元可能顯示成兩字元記號；.NET 的 string.Length 是 UTF-16 code unit 數，不等於 terminal cell 數。

**How**：render 先把 caret 的文字 index 轉成 cell position，寫出新內容，補空白抹掉舊內容較長的尾端，再把 cursor 放回正確的位置。

來源：[src/fsi/console.fs:369](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L369-L402)

```fsharp
                Console.Write(c)
                rendered <- rendered + x.GetCharacterSize(c)

        /// The console input buffer.
        let input = new StringBuilder()

        /// Current position - index into the input buffer
        let mutable current = 0

        let render () =
            let curr = current
            anchor.PlaceAt(x.Inset, 0)

            let rec getLineWidth state i =
                if i = curr || i = input.Length then
                    state
                else
                    getLineWidth (state + x.GetCharacterSize(input.Chars i)) (i + 1)

            let position = getLineWidth 0 0

            // render the current text, computing a new value for "rendered"
            let old_rendered = rendered
            rendered <- 0

            for i = 0 to input.Length - 1 do
                writeChar (input.Chars(i))

            // blank out any dangling old text
            for i = rendered to old_rendered - 1 do
                writeBlank ()

            anchor.PlaceAt(x.Inset, position)
```

套用要點：保持資料與顯示分離。文字不得因 cursor 位於中間而截斷。測試除了比對結果字串，還要檢查刪除後螢幕沒有殘字。


## 4. 尾端 append 有便宜路徑，中間 insert 才重畫

**What**：游標在尾端時 Append 並只輸出新字；在中間則 Insert 後 render。

**Why**：逐字輸入／貼上是最常見路徑。每輸入一字就重畫整個長字串，可能造成不必要的 CPU 與 I/O。

**How**：分開資料 mutation 與渲染成本；兩種路徑最後得到相同的完整 buffer。

來源：[src/fsi/console.fs:403](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L403-L418)

```fsharp
        render ()

        let insertChar (c: char) =
            if current = input.Length then
                current <- current + 1
                input.Append(c) |> ignore
                writeChar (c)
            else
                input.Insert(current, c) |> ignore
                current <- current + 1
                render ()

        let insertTab () =
            for i = ReadLineConsole.TabSize - (current % ReadLineConsole.TabSize) downto 1 do
                insertChar (' ')
```

套用要點：M018 保留 StringBuilder；連續貼上按鍵最多每 64 keys 重畫一次，沒有無輸入時的 polling loop。若日後要進一步優化，可以沿用 upstream 的 append-only draw fast path，而不是新增 background runtime。


## 5. Anchor 將邏輯位置轉成螢幕座標

**What**：Anchor 保存輸入起點，PlaceAt 將 inset + index 對可用寬度做除法／餘數。

**Why**：一行文字會跨實體列，游標不能只做 CursorLeft += 1。

**How**：先定義寬度與起點，所有移動都透過同一套換算。ResetTo 對 buffer 高度做邊界處理。

來源：[src/fsi/console.fs:200](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L200-L237)

```fsharp

[<Sealed>]
type internal Cursor =
    static member ResetTo(top, left) =
        Utils.guard (fun () ->
            Console.CursorTop <- min top (Console.BufferHeight - 1)
            Console.CursorLeft <- left)

    static member Move(delta) =
        let width = Utils.bufferWidth ()
        let position = Console.CursorTop * width + Console.CursorLeft + delta

        let top = position / width
        let left = position % width
        Cursor.ResetTo(top, left)

type internal Anchor =
    {
        top: int
        left: int
    }

    static member Current(inset) =
        {
            top = Console.CursorTop
            left = max inset Console.CursorLeft
        }

    static member Top(inset) = { top = 0; left = inset }

    member p.PlaceAt(inset, index) =
        //printf "p.top = %d, p.left = %d, inset = %d, index = %d\n" p.top p.left inset index
        let width = Utils.bufferWidth ()
        let index = inset + index

        let left = index % width
        let top = p.top + index / width
        Cursor.ResetTo(top, left)
```

套用要點：M018 使用同一概念，但以文字元素建立換行版面。actor 輸出後要更新輸入 anchor；只鎖 WriteLine 而不更新 anchor，下一次重畫可能覆蓋新輸出。


## 6. 全形字元與安全邊界

**What**：full-width 判斷使用已排序區間的端點陣列與 BinarySearch。可用寬度刻意少兩格。

**Why**：CJK 寬字若剛好落在最右邊，terminal 的自動換行與自行計算位置容易不一致。

**How**：端點命中為寬字；落在區間內時 BinarySearch 回傳的負索引具有固定奇偶規律。以下保留演算法與上游的具體螢幕例子。

來源：[src/fsi/console.fs:170](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L170-L198)

```fsharp
        // for array [| a; b; c; d |],
        // if a value is in (a, b) or (c, d), the result of Array.BinarySearch will be a negative even number
        // if a value is a, b, c, d, the result will be a positive number
        let n = Array.BinarySearch(fullWidthCharRanges, char)
        n >= 0 || n % 2 = 0

    /// Limits BufferWidth to make sure that full-width characters will not be print to wrong position.
    ///
    /// The return value is Console.BufferWidth - 2.
    ///
    /// When printing full-width characters to the screen (such as 一二三四五六七八九零),
    ///
    /// if BufferWidth = Console.BufferWidth, the output will be
    ///
    /// #> 一二三四五六七八九零一二三四五六七八九  # (零 is missing)
    ///
    /// #一二三四五六七八九零                      #
    ///
    /// if BufferWidth = Console.BufferWidth - 1, the output will be
    ///
    /// #> 一二三四五六七八九零一二三四五六七八九零# (零 is printed, but will not correctly calculate cursor position)
    ///
    /// #一二三四五六七八九零                      # (cursor may appear in the middle of the character)
    ///
    /// if BufferWidth = Console.BufferWidth - 2, the output will be
    ///
    /// #> 一二三四五六七八九零一二三四五六七八九  #
    ///
    /// #零一二三四五六七八九零                    # (work correctly)
```

套用要點：這是 cell-width 模型，不是完整 Unicode grapheme engine。upstream 以 char 處理；M018 用 StringInfo 的 text element 避免 Backspace 拆開 surrogate pair／combining mark。emoji 字寬仍受 terminal/font 影響，不宣稱所有 terminal 渲染相同。


## 7. 刪除修改 buffer，不從螢幕反推輸入

**What**：Delete、清除開頭、刪除前一個字、清除尾端都在 StringBuilder 上操作，再 render。

**Why**：螢幕是輸出結果，可能被其他輸出或視窗大小改變；不能靠讀畫面重建真正要送出的 prompt。

**How**：明確定義刪除區間，更新 caret；render 負責清掉舊殘字。

來源：[src/fsi/console.fs:477](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L477-L503)

```fsharp
                input.Remove(current, 1) |> ignore
                render ()

        let deleteFromStartOfLineToCursor () =
            if (input.Length > 0 && current > 0) then
                input.Remove(0, current) |> ignore
                current <- 0
                render ()

        let deleteWordLeadingToCursor () =
            if (input.Length > 0 && current > 0) then
                let line = input.ToString()
                let idx = Utils.previousWordFromIdx line (current - 1, false)
                input.Remove(idx, current - idx) |> ignore
                current <- idx
                render ()

        let deleteToEndOfLine () =
            if (current < input.Length) then
                input.Remove(current, input.Length - current) |> ignore
                render ()

        let insert (key: ConsoleKeyInfo) =
            // REVIEW: is this F6 rewrite required? 0x1A looks like Ctrl-Z.
            // REVIEW: the Ctrl-Z code is not recognised as EOF by the lexer.
            // REVIEW: looks like a relic of the port of readline, which is currently removable.
            let c = if (key.Key = ConsoleKey.F6) then '\x1A' else key.KeyChar
```

套用要點：不要採用「從螢幕救回被 ReadConsoleW 覆寫的字」之類 workaround。應從一開始就保存完整輸入 buffer。


## 8. Enter 回傳完整字串，EOF 不一定是特殊按鍵事件

**What**：enter() 取 input.ToString()，不是取 input[..current]。字串恰好是 Ctrl+Z 字元時回傳 null。

**Why**：即使 Enter 時 cursor 在行中，也要保存其後的文字。EOF 則是呼叫層的控制訊號。

**How**：顯示換行與回傳資料是兩件事；有內容時另外記入 history。

來源：[src/fsi/console.fs:513](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L513-L530)

```fsharp
        let enter () =
            Console.Write("\n")
            let line = input.ToString()

            if (line = "\x1A") then
                null
            else
                if (line.Length > 0) then
                    history.AddLast(line)

                line

        let clear () =
            current <- input.Length

            let setPrompt prompt =
                if prompt then // We only allow clearing if prompt is ">"
                    Console.Clear()
```

套用要點：M018 普通 Enter 同樣送完整 buffer；Shift+Enter 把完整當前行加 LF 到累積區，清當前行後再讀。EOF 丟棄未送的暫存內容、回傳 null，讓 demo 的 finally 清理 actor/process。


## 9. 唯一 ReadKey loop 與 modifier 判斷

**What**：ReadKey(true) 不自動 echo，由行編輯器統一輸出。Tab 明確讀 Shift modifier。

**Why**：可以知道哪個按鍵結束輸入，同時避免兩個 reader 爭搶字元。

**How**：按鍵先 dispatch 成編輯動作；需要繼續時再次呼叫 read。沒有背景 timer 每幾毫秒掃一次鍵盤。

來源：[src/fsi/console.fs:547](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L547-L584)

```fsharp
        let rec read () =
            let key = Console.ReadKey true

            match key.Key with
            | ConsoleKey.Backspace ->
                backspace ()
                change ()
            | ConsoleKey.Delete ->
                delete ()
                change ()
            | ConsoleKey.Enter -> enter ()
            | ConsoleKey.Tab ->
                tabPress (key.Modifiers &&& ConsoleModifiers.Shift <> enum 0)
                read ()
            | ConsoleKey.UpArrow ->
                setInput (history.Previous())
                change ()
            | ConsoleKey.DownArrow ->
                setInput (history.Next())
                change ()
            | ConsoleKey.RightArrow when key.Modifiers &&& ConsoleModifiers.Control = enum 0 ->
                moveRight ()
                change ()
            | ConsoleKey.LeftArrow when key.Modifiers &&& ConsoleModifiers.Control = enum 0 ->
                moveLeft ()
                change ()
            | ConsoleKey.Escape ->
                setInput String.Empty
                change ()
            | ConsoleKey.Home ->
                home ()
                change ()
            | ConsoleKey.End ->
                current <- input.Length
                anchor.PlaceAt(x.Inset, rendered)
                change ()
            | _ ->
```

套用要點：M018 的 Finish(bool) 就在這個層次辨識 Shift；不把每一行或按鍵送進 ReActLoop。只有完整提交的字串才進 IAgent.Send。


## 10. IME 的 KeyChar=0 不是要插入的 NUL

**What**：上游明確忽略 KeyChar=0，並解釋它可能是組合鍵輸入中的一部分。

**Why**：把所有 key event 直接當文字 append，會把功能鍵或 IME 中間狀態變成污染 prompt 的字元。

**How**：先處理已知功能鍵，再只插入真正的字元。

來源：[src/fsi/console.fs:631](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L631-L646)

```fsharp
                | _ ->
                    // Note: If KeyChar=0, it's not a proper char, e.g. it could be part of a multi key-press character,
                    //       e.g. e-acute is ' and e with the French (Belgium) IME and US Intl KB.
                    // Here: skip KeyChar=0 (except for F6 which maps to 0x1A (ctrl-Z?)).
                    if key.KeyChar <> '\000' || key.Key = ConsoleKey.F6 then
                        insert (key)
                        change ()
                    else
                        // Skip and read again.
                        read ()

        and change () =
            changed <- true
            read ()

        read ()
```

套用要點：保留此判斷；搭配中文、accent、surrogate pair 測試。注入 Unicode key records 只能證明程式的字元處理，不等於已測過所有真實 IME 組字流程。


## 11. Ctrl+D 的 process exit 不能照搬到 wrapper

**What**：FSI 在空行 Ctrl+D 分支直接 exit 0。

**Why**：對整個 FSI CLI 的設計是一種退出方式；但 Fodex 還有 actor、hub、native process 的生命週期清理。

**How**：Fodex 把 EOF 變成回傳值，由既有 stop/finally 負責收尾，不讓 key handler 決定整個 process 退出。

來源：[src/fsi/console.fs:616](https://github.com/dotnet/fsharp/blob/fcc3990af38079c9b6c94fa3505d653d1b4d1a73/src/fsi/console.fs#L616-L621)

```fsharp
                | ConsoleModifiers.Control, ConsoleKey.D ->
                    if (input.Length = 0) then
                        exit 0 //quit
                    else
                        delete ()
                        change ()
```

套用要點：這是移植語意的差異，不能只把 upstream 檔案原封不動放進 implementation。


## 12. M018 採用範圍與不變量

| 項目 | M018 做法 |
|---|---|
| package/project | 現有實作專案，不新增 Windows GUI project 或 UI NuGet |
| key policy | ConsoleInputFodexFunV300；先註冊 quotation，再 eval/pin，透過 FeasibleFun.invoke |
| input owner | 一個阻塞 ReadKey loop，或 redirected TextReader fallback |
| Enter | 傳回整段，一次 IAgent.Send |
| Shift+Enter | 完整當前行 + LF，繼續下一行 |
| alternate newline | Ctrl+J；終端是否傳遞 Shift modifier 取決於 terminal |
| editing | 當前行插入、左右/Home/End、Backspace/Delete、Tab、Escape、Ctrl+U/K；不做 completion/history |
| output | 鍵盤回顯與 wrapper sink 共用 lock；等待按鍵時不持鎖 |
| EOF | 回傳 null，不送半成品，不呼叫 process exit |
| Ctrl+C | 讀鍵期間暫設 TreatControlCAsInput，將 Ctrl+C 映射為 EOF，finally 還原 host 設定 |
| /exit | host command 經既有 Argu/quotation 解析，走 caller 的 cleanup；不進 LLM prompt |
| platform claim | 正式模組使用跨平台 .NET API；本機 Windows 實測不等於 Linux/macOS 驗收 |

現有 Fodex.Supervisor.fsproj 原本就設定 win-x64；這次不把整個 Proc Supervisor/actor 專案遷移成其他 RID，也不新增 WindowsDesktop target。輸入模組本身不使用 P/Invoke。FSI pane、pipes 或不支援游標 API 的 host 會回到逐行輸入，沒有把 fallback 偽裝成 Shift+Enter 可用。

## 13. 先前 Win32 probe 的證據

兩支 Python 只存在 artifacts/M017-analysis，不參與正式 build 或 M018 執行。

- Enter wakeup：Shift+Enter 的 state=16，可辨識 Shift；但 abc + Left + Enter 回傳 ab\r<space>，字元 c 被破壞。
- 普通 cooked read：相同編輯保留 abc\r\n，但 Shift+Enter 的 state=0，缺少修飾鍵資訊。
- 因此沒有把此 Win32 路徑接入正式輸入；不是看到 SHIFT_PRESSED 欄位就推論所有編輯情境都正確。

## 14. 後續套用與驗收清單

1. 先驗證原始 prompt 字串是否完整，再看 UI 是否漂亮；尤其測 abc + Left + Enter。
2. Shift+Enter 期間不應出現新的 HumanInit/StartTurn；普通 Enter 才有一次完整提交。
3. 驗證空白行、中文、結合字、emoji、Backspace/Delete、貼上長文字、換行邊界。
4. actor 輸出插入時不可改動輸入 buffer；恢復輸入時不能另開 reader。
5. 不用 redirected stdin 的測試冒充 Shift modifier 驗證。Windows 專用測試 driver 可以注入 console input records，但它不成為產品相依。
6. 檢查 EOF 走 finally、關閉本次建立的 hub/native，不停止別人的程序。
7. Debug 預設 loader 的 DLL 必須真正建置成功，不能只用 Release 測過就宣稱 launcher 已更新。

## 15. 本次實際驗證與限制

- Release build 通過；完整 Expecto 184/184。按鍵與輸出 quotation 的獨立 FSI 測試 8/8 + 9/9，既有 VSTest adapter 17/17。
- 原生 Windows console driver 送入真正的 input records，結果保留 `假設\n\n測試`、行中 Enter 的 `abc`、刪除結合字後的 `中文😀`、背景輸出前後的 `hold tail`。EOF 回到 caller。這驗證 Unicode 事件與 buffer，沒有宣稱已驗證所有人工 IME 組字行為。
- 畫面驗證按 console cell 取 row；不能把 `ReadConsoleOutputCharacterW` 的結果按固定寬度切字串，因為寬字元的 trailing cell 可能不在字串內，會誤判為每行錯位。`ReadConsoleOutputW` 取 cell 與 attributes 後，prompt／續行位置正確。
- 真正 M018＋app-server 的 18.014 秒測試，Shift+Enter 期間沒有派出新 HumanInit；普通 Enter 後只有一筆整段輸入。SQL 還原文字精確等於兩個 LF 分隔的 prompt，並取得一則完成回覆。此 run 使用 Release 與獨立 automation DB／port。
- 正式 reader 沒有 P/Invoke；Python driver 的 Windows API 只用於測試鍵盤與螢幕。Linux／macOS、終端縮放與各家 IME 仍須各平台實測；未傳遞 Shift 的終端可用 Ctrl+J。超過整個 terminal viewport 的編輯不是這個暫時 console 的驗收範圍。
- 預設 Debug DLL 起初被舊 M018 鎖住；使用者正常結束後，原目的地建置成功（0 warning／0 error），bin/obj DLL SHA256 一致。Debug loader 的獨立 FSI 8/8 + 9/9 與真實 console 鍵盤測試通過；原 `Start-M018.ps1` 可載入新功能，M018 保持腳本。

證據在 `artifacts/M017-analysis/m018-multiline-*`、`m018-key-console-smoke.*` 與 `artifacts/M017-test-results/M018-Multiline.trx`。

後續補 `/exit` 與 Ctrl+C：只把離開意圖交回既有 M018 host，先印可恢復同一 WSID 的命令，再清理 participant/hub/ActorSystem。沒有引入 signal thread、第二個 reader 或新專案。189 項測試通過；實際 sub 執行中退出的 30 個測試程序均結束，印出的命令也實際恢復同一 main CSID。見 `m018-exit-smoke.json` 與 README.M018.md 的完整紀錄；原始上游片段不受這個 Fodex host 政策影響。

## 上游 MIT 授權全文

以下授權涵蓋本文上游片段，以及實作中的 full-width range table/lookup 來源。

```text

The MIT License (MIT)

Copyright (c) Microsoft Corporation.
All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
