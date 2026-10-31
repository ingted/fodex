// M011.R001 host：owner 隔離修正版；原 host 不改，見 README.R001.Validation.md。
// M011 host：只 hosting。先啟動本檔，再執行 WrapperIAgentDemo.M011.R001.fsx。
// 不在 client 自動 build/spawn/stop host。--block false 可在 VS FSI 啟動後繼續互動。
// 操作者 stop() 或手動 POST /stop 收尾；main participant 不取得這個 lifecycle 操作。
// M011 不註冊 MCP；HTTP /order /receive 供原生 shell tool 的 client 使用。
#load "DemoReferencesR001.fsx"
open System
open System.IO
do if Console.IsOutputRedirected then Console.OutputEncoding <- System.Text.UTF8Encoding(false)
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu
open Fodex.Supervisor
open Fodex.Supervisor.Demos

type HostArgument =
    | Hub_Url of string
    | Output_Directory of string
    | Block of bool
    interface IArgParserTemplate with member _.Usage = "M011 hub setting"

// 子 participant 明確指定 exec/model/effort；每個 who 首輪新 sid，後續自動 resume。
// SQL 只用於 M008 wrappers；M011 raw native sub 不建立 wrapper/DB/graph。
let defaultArgumentsText = """
--hub-url http://127.0.0.1:18911
--output-directory F:/fodex/artifacts/M011.R001-hub
--block true
--native-adapter exec
--model gpt-6.1-sol
--effort high
--working-directory F:/fodex
--sandbox danger-full-access
--wrapperagent 2.0.3
--reactloophandler 2.0.3
--start 2.0.0
--stop 2.0.0
--send 2.0.0 ""
--getsessionid 2.0.0
--acquireparticipant 2.0.0
--exportreactdatastreamjsonl 2.0.0
--reactloopupsertparticipant 2.0.0
--reactloopremoveparticipant 2.0.0
--reactloopresolveparticipant 2.0.0
--reactlooplistparticipants 2.0.0
--reactloopposthumanprompt 2.0.0
--reactloopdispatchtoparticipant 2.0.0
--reactlooproutereactdatastream 2.0.0
--reactloopcollect 2.0.0
--reactloopreaddataflow 2.0.0
--reactloopgetstatus 2.0.0
--reactloopgetparticipantcapabilities 2.0.0
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1..] else defaults
let settings = ArgumentParser.Create<HostArgument>().ParseCommandLine(argv,ignoreUnrecognized=true)
let argumentJson = JsonSerializerOptions(Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let evidenceDirectory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory evidenceDirectory |> ignore
let createParticipants (owner: string) (names: string array) =
    let ownerEvidence = Path.Combine(evidenceDirectory,HubScopeFodexFunV210.scopeId owner)
    Directory.CreateDirectory ownerEvidence |> ignore
    let created = ResizeArray<Akka.Actor.ActorSystem * (string * (string -> string -> CancellationToken -> Task<Result<string*string,exn>>)) * (unit -> Task)>()
    try
        for who in names do
            let node,perform,stop = DemoParticipantsV101.createNativeExec who arguments ownerEvidence
            created.Add(node,(who,(fun _order instruction ct -> perform instruction ct)),stop)
        let node,_,_ = created[0]
        node, (created |> Seq.map (fun (_,participant,_) -> participant) |> Map.ofSeq), (created |> Seq.map (fun (_,_,stop) -> stop) |> Seq.toList)
    with ex ->
        for _,_,stop in created do stop().GetAwaiter().GetResult()
        raise ex
let lifetime = new CancellationTokenSource()
Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; lifetime.Cancel())
let namesFor count =
    if count <> 2 then invalidArg "count" "This milestone uses exactly contract and implementation"
    [|"contract";"implementation"|]
let hosting = DemoHostingV105.run (settings.GetResult Hub_Url) false evidenceDirectory namesFor createParticipants lifetime.Token
let stop () = lifetime.Cancel(); hosting.GetAwaiter().GetResult()
if settings.GetResult Block then hosting.GetAwaiter().GetResult()
