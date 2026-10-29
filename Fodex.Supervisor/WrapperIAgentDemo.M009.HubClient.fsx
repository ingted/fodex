// M009 的 shell-tool client：只連既有 hub；不建立 wrapper、不 spawn/stop host、不使用 MCP。
// instruction 只是 HTTP request 的文字欄位；不把使用者文字當命令執行。
#I @"bin\M008"
#r "Argu.dll"
#load @"C:\Users\Administrator\.codex\lib\ParseLine.fsx"
open System
open System.Net.Http
open System.Text
open System.Text.Json
open Argu

type ClientArgument =
    | Url of string
    | Action of string
    | Who of string
    | Instruction of string
    interface IArgParserTemplate with member _.Usage = "M009 order/receive client"

let defaultArgumentsText = """
--url http://127.0.0.1:18789
--action order
--who contract
--instruction "請唯讀 review contract，最多一個確證 bug。"
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1..] else defaults
let options = ArgumentParser.Create<ClientArgument>().ParseCommandLine argv
let url = Uri(options.GetResult Url)
if not url.IsLoopback || url.Scheme <> "http" then invalidArg "url" "Only the local demo hub is supported"
let run () =
    use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
    let path,body =
        match options.GetResult Action with
        | "order" -> "/order", JsonSerializer.Serialize {| who=options.GetResult Who; instruct=options.GetResult Instruction |}
        | "receive" -> "/receive", ""
        | _ -> invalidArg "action" "Use order or receive"
    use content = new StringContent(body,Encoding.UTF8,"application/json")
    use response = http.PostAsync(url.AbsoluteUri.TrimEnd('/') + path,content).GetAwaiter().GetResult()
    let result = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    response.EnsureSuccessStatusCode() |> ignore
    result
// 這是程式產生的 transport metadata + 原生完成文字，不是限制 LLM 的輸出 schema。
printfn "%s" (run ())
