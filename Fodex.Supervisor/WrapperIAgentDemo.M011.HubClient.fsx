// 選用的 FSI client；正式派工使用同一實作組件的 compiled CLI，兩者共用 FAkka.Argu/HTTP。
// 此檔只連既有 host，不 build/spawn/stop host，不建立 ActorSystem 或保存執行狀態。
#load "MilestoneReferences.fsx"
open System
open System.Net.Http
open System.Threading
open Fodex.Supervisor

let defaultArgumentsText = """
--url http://127.0.0.1:18911/owners/REPLACE_WITH_OWNER_SCOPE
--action order
--order contract "只原樣回覆 orz，不使用工具。"
--order implementation "只原樣回覆 OGC，不使用工具。"
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length>1 then fsi.CommandLineArgs[1..] else defaults

/// 修改 defaultArgumentsText 的 owner URL 後執行；receive 空時真正 async 等結果。
let run () = task {
    use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
    return! HubClientV101.run http argv Console.Error.WriteLine CancellationToken.None
}
printfn "%s" (run().GetAwaiter().GetResult())
