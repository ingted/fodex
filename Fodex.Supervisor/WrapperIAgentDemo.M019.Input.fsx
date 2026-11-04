module WrapperIAgentDemoM019Input

open System

/// M019 的 console 提交規則，只由 demo #load；不編進 Supervisor DLL，也不持有 actor 狀態。
/// readLine 只表示一次行編輯完成，不能把它直接當成一次 IAgent.Send。
/// 將終端、滑鼠貼上或 Shift+Enter 取得的文字累積起來，最後一行獨立的 ;; 才結束一筆輸入。
///
/// 例：readLine 依序回傳「第一行」、「第二行」、「;;」，結果是 "第一行\n第二行"。
/// Shift+Enter 可能一次回傳 "第一行\n第二行\n;;"，結果相同，不需要另一套讀鍵迴圈。
/// 一般文字中的 "let x = 1;;" 不算提交；標記行前後可有空白，標記本身不送給 agent。
/// 此規則同時適用 /help、/exit 等命令，讓貼上的換行不會提早執行部分內容。
///
/// null 表示 EOF／既有 reader 的 Ctrl+C：丟棄未提交內容，讓外層 finally 正常清理。
/// 空白行、縮排與段落都保留；只把不同平台的換行正規化為 LF，不 Trim 使用者內容。
/// 這是明確的文字協定：如果貼上的內容本身含獨立的 ;; 提交行，也會提交，與 FSI 同理。
let readSubmission (readLine: unit -> string) : string =
    let parts=ResizeArray<string>()
    let mutable reading=true
    let mutable result: string=null
    while reading do
        match readLine() with
        | null -> reading<-false
        | chunk ->
            let text=chunk.ReplaceLineEndings("\n")
            let boundary=text.LastIndexOf '\n'
            let lastLine=text.Substring(boundary+1)
            if lastLine.Trim()=";;" then
                // Shift+Enter 的多行 chunk：只去掉最後的標記行與其分隔 LF。
                // 例："甲\n\n;;" 保留 "甲\n"，而不是把有意輸入的空白行一起刪掉。
                if boundary>=0 then parts.Add(text.Substring(0,boundary))
                result<-String.concat "\n" parts
                reading<-false
            else parts.Add text
    result
