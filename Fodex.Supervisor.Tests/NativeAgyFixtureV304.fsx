#if INTERACTIVE
#load "NativeBootstrapFixtureV100.fsx"
#else
module NativeAgyFixtureV304
#endif
open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu

/// 只供既有Tests.exe的子程序模式，取代AGY後端；不新增demo project或偽稱真模型生成。
type FixtureArgumentV304=
    | [<AltCommandLine("-p")>] Prompt of string
    | Output_Format of string
    | Model of string
    | Effort of string
    | Conversation of string
    | Dangerously_Skip_Permissions
    | Disable_Slash_Commands
    interface IArgParserTemplate with member _.Usage="Test-owned AGY CLI terminal peer"

let environmentName="FODEX_TEST_AGY_V304"
let tryRun argv : Task<int> option=
    let folder=Environment.GetEnvironmentVariable environmentName
    if String.IsNullOrWhiteSpace folder then None else
    let parser=ArgumentParser.Create<FixtureArgumentV304>("NativeAgyFixtureV304")
    let options=parser.ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)
    match options.TryGetResult Prompt with
    | None -> None
    | Some prompt -> Some(task {
        if not(Directory.Exists folder) then invalidOp "AGY fixture requires its test-owned directory"
        if options.GetResult Output_Format<>"json" then invalidOp "Fixture expects AGY native JSON terminal"
        let retained=options.TryGetResult Conversation
        let sid=retained |> Option.defaultWith(fun () -> Guid.NewGuid().ToString("D"))
        let metadata=JsonSerializer.Serialize {|pid=Environment.ProcessId;resumeSid=Option.toObj retained;model=options.GetResult Model;effort=options.GetResult Effort|}
        File.WriteAllText(Path.Combine(folder,"started.json"),metadata,UTF8Encoding(false))
        File.WriteAllText(Path.Combine(folder,"started.ready"),"",UTF8Encoding(false))
        if prompt="WAIT" then
            use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 30.)
            while not(File.Exists(Path.Combine(folder,"release"))) do do! Task.Delay(10,timeout.Token)
        Console.Out.WriteLine(JsonSerializer.Serialize {|status="SUCCESS";conversation_id=sid;response=NativeBootstrapFixtureV100.answer folder prompt|})
        return 0 })
