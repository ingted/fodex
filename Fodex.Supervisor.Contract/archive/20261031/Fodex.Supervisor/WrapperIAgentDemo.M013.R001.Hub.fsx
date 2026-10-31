// M013.R001 host：owner 隔離修正版；原 host 不改，見 README.R001.Validation.md。
// M013 host：只 hosting。先啟動本檔，再執行 WrapperIAgentDemo.M013.R001.fsx。
// 不在 client 自動 build/spawn/stop host。--block false 可在 VS FSI 啟動後繼續互動。
// 操作者 stop() 或手動 POST /stop 收尾；main participant 不取得這個 lifecycle 操作。
#load "DemoReferencesR001.fsx"
open System
open System.IO
do if Console.IsOutputRedirected then Console.OutputEncoding <- System.Text.UTF8Encoding(false)
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Demos

type HostArgument =
    | Hub_Url of string
    | Output_Directory of string
    | Block of bool
    interface IArgParserTemplate with member _.Usage = "M013 hub setting"

// 子 participant 明確指定 exec/model/effort；每個 who 首輪新 sid，後續自動 resume。
// raw native sub 不建立 wrapper/IAgent/DB；完成事實由 main wrapper 經 ReActLoop 匯入主 graph。
let defaultArgumentsText = """
--hub-url http://127.0.0.1:18913
--output-directory F:/fodex/artifacts/M013.R001-hub
--block true
--native-adapter exec
--model gpt-6.1-sol
--effort high
--working-directory F:/fodex
--sandbox danger-full-access
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1..] else defaults
let settings = ArgumentParser.Create<HostArgument>().ParseCommandLine(argv,ignoreUnrecognized=true)
let argumentJson = JsonSerializerOptions(Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let evidenceDirectory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory evidenceDirectory |> ignore
// Who 由 main wrapper 的 /subagent(N) 要求決定；host 不在啟動時預建固定兩個。
// factory 只建立缺少的 who，已存在的 performer/sid 由 V104 host 保留。
let createParticipants (owner: string) (names: string array) =
    let ownerEvidence = Path.Combine(evidenceDirectory,HubScopeFodexFunV210.scopeId owner)
    Directory.CreateDirectory ownerEvidence |> ignore
    let created = ResizeArray<Akka.Actor.ActorSystem * (string * NativePerformerV104) * (unit -> Task)>()
    try
        for who in names do
            let node,perform,stop = DemoParticipantsV103.createNativeExec who arguments ownerEvidence
            created.Add(node,(who,perform),stop)
        let node,_,_ = created[0]
        node, (created |> Seq.map (fun (_,participant,_) -> participant) |> Map.ofSeq), (created |> Seq.map (fun (_,_,stop) -> stop) |> Seq.toList)
    with ex ->
        for _,_,stop in created do stop().GetAwaiter().GetResult()
        raise ex
let lifetime = new CancellationTokenSource()
Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; lifetime.Cancel())
let hosting = DemoHostingV106.run (settings.GetResult Hub_Url) evidenceDirectory createParticipants lifetime.Token
let stop () = lifetime.Cancel(); hosting.GetAwaiter().GetResult()
if settings.GetResult Block then hosting.GetAwaiter().GetResult()
