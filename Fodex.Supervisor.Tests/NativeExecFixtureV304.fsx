#if INTERACTIVE
#load "NativeBootstrapFixtureV100.fsx"
#else
module NativeExecFixtureV304
#endif
open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu

/// 既有 Tests.exe 的 exec 子程序模式；Argu 按真正 exec/resume 形狀解析，prompt 只讀 stdin。
type ResumeArgumentV304 =
    | Json
    | Model of string
    | [<AltCommandLine("-c")>] Config of string
    | [<MainCommand;ExactlyOnce>] Input of sid:string * source:string
    interface IArgParserTemplate with member _.Usage="Resume test-owned native session"
type ExecArgumentV304 =
    | [<CliPrefix(CliPrefix.None)>] Resume of ParseResults<ResumeArgumentV304>
    | Json
    | Model of string
    | [<AltCommandLine("-c")>] Config of string
    | [<MainCommand>] Input of source:string
    interface IArgParserTemplate with member _.Usage="Read one prompt from stdin"
type RootArgumentV304 =
    | [<AltCommandLine("-c")>] Config of string
    | [<CliPrefix(CliPrefix.None)>] Exec of ParseResults<ExecArgumentV304>
    interface IArgParserTemplate with member _.Usage="Test-owned exec CLI terminal peer"

let environmentName="FODEX_TEST_EXEC_V304"
let tryRun argv:Task<int> option=
    let folder=Environment.GetEnvironmentVariable environmentName
    if String.IsNullOrWhiteSpace folder then None else
    let options=ArgumentParser.Create<RootArgumentV304>("NativeExecFixtureV304").ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)
    match options.TryGetSubCommand() with
    | Some(Exec exec) -> Some(task {
        let retained,source,model=
            match exec.TryGetSubCommand() with
            | Some(Resume resume) ->
                let sid,source=resume.GetResult ResumeArgumentV304.Input
                Some sid,source,resume.GetResult ResumeArgumentV304.Model
            | _ -> None,exec.GetResult ExecArgumentV304.Input,exec.GetResult ExecArgumentV304.Model
        if source<>"-" then invalidOp "Fixture only accepts native stdin prompt mode"
        let! prompt=Console.In.ReadToEndAsync()
        let sid=retained |> Option.defaultWith(fun () -> Guid.NewGuid().ToString("D"))
        let metadata=JsonSerializer.Serialize {|pid=Environment.ProcessId;resumeSid=Option.toObj retained;sid=sid;model=model;prompt=prompt;argv=argv|}
        File.WriteAllText(Path.Combine(folder,"started.json"),metadata,UTF8Encoding(false))
        File.WriteAllText(Path.Combine(folder,"started.ready"),"",UTF8Encoding(false))
        Console.Out.WriteLine(JsonSerializer.Serialize {|``type``="thread.started";thread_id=sid|})
        Console.Out.Flush()
        if prompt="WAIT" then
            use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 30.)
            while not(File.Exists(Path.Combine(folder,"release"))) do do! Task.Delay(10,timeout.Token)
        Console.Out.WriteLine(JsonSerializer.Serialize {|``type``="item.completed";item={|id="fixture-message";``type``="agent_message";text=NativeBootstrapFixtureV100.answer folder prompt|}|})
        Console.Out.WriteLine(JsonSerializer.Serialize {|``type``="turn.completed";usage={|input_tokens=1;output_tokens=1|}|})
        return 0 })
    | _ -> None
