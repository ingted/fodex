#r "nuget: FAkka.Chiron"

open System.IO
open Chiron

let jsonlPath = @"F:\fodex\notes\ptcs-chat-20261105032841.jsonl"
let outputPath = @"F:\fodex\notes\ptcs-chat-20261105032841.txt"


// 讀取每行 JSONL 並提取 "body" 欄位
let bodyValues =
    File.ReadLines(jsonlPath)
    |> Seq.filter (not << System.String.IsNullOrWhiteSpace)
    |> Seq.map (fun line ->
        // 1. 解析該行 JSON
        let json = Json.parse line |> JsonResult.getOrThrow
        
        // 2. 存取 "body" 欄位
        // 若 "body" 是字串欄位，可用 Json.jstr 轉為 string：
        Json.jstr json["body"]

        // 附註：
        // 若 "body" 可能是物件、陣列或其他非純字串型態，可改用：
        // json["body"].ToString()
    )

// 輸出結果
bodyValues
|> Seq.map (sprintf "%s\r\n")
|> Seq.toArray
// 直接將 sequence 逐行寫入檔案（若檔案已存在會直接覆蓋）
|> fun a -> File.WriteAllLines(outputPath, a)