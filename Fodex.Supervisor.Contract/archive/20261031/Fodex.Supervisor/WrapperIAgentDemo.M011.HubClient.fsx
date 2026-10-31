// M011 的 shell-tool client：只連既有 hub；不建立 wrapper、不 spawn/stop host、不使用 MCP。
// instruction 只是 HTTP request 的文字欄位；不把使用者文字當命令執行。
#if INTERACTIVE
#I @"bin\M010.Actor"
#r "Argu.dll"
#load "../Fodex.Supervisor.Demos/DemoHubClientV101.fsx"
#load @"C:\Users\Administrator\.codex\lib\ParseLine.fsx"
#else
module Fodex.Supervisor.Demos.M011HubClient
#endif
open System
open System.Net.Http
open System.Threading
open Fodex.Supervisor.Demos

/// FSI 與已編譯入口共用相同 Argu/HTTP 函數；沒有另一套 parser、actor 或工作狀態。
/// 範例參數：--url http://127.0.0.1:18793 --action receive。
let execute argv =
    use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
    (DemoHubClientV101.run http argv Console.Error.WriteLine CancellationToken.None).GetAwaiter().GetResult()

#if INTERACTIVE
let defaultArgumentsText = """
--url http://127.0.0.1:18793
--action order
--order contract "只原樣回覆 orz，不使用工具。"
--order implementation "只原樣回覆 OGC，不使用工具。"
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1..] else defaults
// FSI 只做送件；同一輪可重複 --order who instruction，hub actor 管理執行狀態。
let run () = execute argv
// 這是程式產生的 transport metadata + 原生完成文字，不是限制 LLM 的輸出 schema。
printfn "%s" (run ())
#else
// 編譯入口只是同一個 transport 的命令列邊界，沒有 host lifecycle 或執行狀態。
// 反覆工具呼叫可直接執行既有 Demos DLL，省去每次 FSI 載入/編譯這份來源碼。
[<EntryPoint>]
let main argv =
    if Console.IsOutputRedirected then Console.OutputEncoding <- Text.UTF8Encoding(false)
    try
        printfn "%s" (execute argv)
        0
    with ex ->
        // 不將任意 exception payload（可能含 instruction）送入 log 或工具結果。
        Console.Error.WriteLine("FODEX_CLIENT_FAILED " + ex.GetType().Name)
        1
#endif
