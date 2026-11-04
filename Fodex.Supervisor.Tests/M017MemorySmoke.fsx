// 真實 app-server / Proc Supervisor 控制驗證，不送任何模型 turn。
// FSI 全選執行；Debug 須先 build。automation 可指定 --define:RELEASE 使用 Release DLL。
// 使用隔離 CODEX_HOME、合成記憶與不可連線的測試 provider，不讀寫真正的記憶或憑證。
// 此檔列為 None：需要本機 Codex binary 與 SQL Server，不加入 Test Explorer 自動測試。
#if INTERACTIVE
#load "../Fodex.Supervisor/M017References.fsx"
#load "ImplementationTestSupportV300.fsx"
#endif

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let evidence=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../artifacts/M017-analysis/memory-actor-"+Guid.NewGuid().ToString("N")))
let isolatedHome=Path.Combine(evidence,"codex-home")
let workspace=Path.Combine(evidence,"workspace")
let utf8=UTF8Encoding(false,true)
Directory.CreateDirectory(Path.Combine(isolatedHome,"memories")) |> ignore
Directory.CreateDirectory workspace |> ignore
File.WriteAllText(Path.Combine(isolatedHome,"memories/memory_summary.md"),"Synthetic FODEX_MEMORY_SMOKE context.",utf8)
File.WriteAllText(Path.Combine(isolatedHome,"memories/MEMORY.md"),"Synthetic FODEX_MEMORY_SMOKE context.",utf8)
File.WriteAllText(Path.Combine(isolatedHome,"config.toml"),"""
model_provider = "memory_probe"
[model_providers.memory_probe]
name = "memory_probe"
base_url = "http://127.0.0.1:9/v1"
wire_api = "responses"
requires_openai_auth = false
""",utf8)

let tests=testList "M017 real memory lifecycle" [
    testCase "memory reload keeps native sid and stopped settings do not start a process" <| fun () ->
        let previousHome=Environment.GetEnvironmentVariable "CODEX_HOME"
        Environment.SetEnvironmentVariable("CODEX_HOME",isolatedHome)
        try withDatabase(fun cs -> withNode(fun node ->
            use json=JsonDocument.Parse """[{"id":"probe-model","model":"probe-model","supportedReasoningEfforts":[{"reasoningEffort":"low"}],"serviceTiers":[]}]"""
            let models=json.RootElement.EnumerateArray() |> Seq.map(fun row -> row.Clone()) |> Seq.toArray
            let session=Guid.NewGuid(),None
            let loop=FodexFunRegistrationV300.createLoop node cs session ctx ignore |> required
            FodexFunRegistrationV300.registerFactories node loop cs ctx models
            NativeOwnershipFodexFunV209.register node
            let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"memory-smoke",ReActParticipantKind.Native,None,None) |> required
            let create=evaluated<NativeCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetNative
            let args=SupervisorArgs.format [
                SupervisorArgument.Model "probe-model";SupervisorArgument.Effort "low"
                SupervisorArgument.Native_Adapter "app-server";SupervisorArgument.Sandbox "danger-full-access"
                SupervisorArgument.Working_Directory workspace ]
            let native=create(participant,args,dict []) |> required
            let ownership=native.PinNativeOwnership(node,Some "2.0.9")
            let read()=
                let reply=TaskCompletionSource<Result<NativeSettingsV300 * bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                native.Send(box(ReadNativeSettings(fun result -> reply.TrySetResult result |> ignore))) |> required |> ignore
                reply.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> required
            let apply settings=
                let reply=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                native.Send(box(SetNativeSettings(settings,fun result -> reply.TrySetResult result |> ignore))) |> required |> ignore
                reply.Task.WaitAsync(TimeSpan.FromSeconds 60.).GetAwaiter().GetResult() |> required
            let pid()=match ownership() |> required with NativeOwnership(_,_,_,_,pid,_) -> pid
            let stages=ResizeArray<obj>()
            try
                native.Start() |> required |> ignore
                let sid=native.GetSessionId() |> required
                let initial,active=read()
                Expect.isTrue initial.Memory "initial memory enabled"
                Expect.isFalse active "no model turn"
                let initialPid=pid()
                Expect.isSome initialPid "initial process is supervised"
                stages.Add(box {|stage="initial";sid=sprintf "%A" sid;pid=initialPid.Value;memory=initial.Memory|})
                let mutable previousPid=initialPid
                for enabled in [false;true] do
                    apply {initial with Memory=enabled}
                    let current,busy=read()
                    let currentPid=pid()
                    Expect.equal current.Memory enabled "acknowledged setting"
                    Expect.isFalse busy "setting does not fabricate a turn"
                    Expect.equal (native.GetSessionId() |> required) sid "same Codex sid"
                    Expect.isSome currentPid "process re-established"
                    Expect.notEqual currentPid previousPid "new process loaded setting"
                    stages.Add(box {|stage="changed";sid=sprintf "%A" sid;pid=currentPid.Value;memory=current.Memory|})
                    previousPid<-currentPid
                native.Stop() |> required |> ignore
                apply {initial with Memory=false}
                Expect.isNone (pid()) "stopped settings update must not resurrect a process"
                native.Start() |> required |> ignore
                Expect.equal (native.GetSessionId() |> required) sid "start after stop also resumes original sid"
                Expect.isFalse (read() |> fst).Memory "pending setting loaded on Start"
                let flow=loop.ReadDataFlow session |> required
                Expect.isFalse (flow.GetEdges() |> Array.exists(function
                    | _,_,AgentWork _ | _,_,AgentCot _ | _,_,AgentEnd _ -> true | _ -> false)) "no model generation events"
                File.WriteAllText(Path.Combine(evidence,"lifecycle.json"),JsonSerializer.Serialize(stages.ToArray()),utf8)
                printfn "MEMORY_ACTOR_EVIDENCE=%s" evidence
            finally native.Stop() |> ignore))
        finally Environment.SetEnvironmentVariable("CODEX_HOME",previousHome)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "M017MemorySmoke failed"
#endif
