// 自動驗證 orchestrator，不是產品 client；只有這份獨立測試腳本會啟停自己建立的 demo host。
// 每個 demo 仍維持 host/client 分檔；Codex process 仍由 demo 的 Proc Supervisor 管理。
// 使用全新 DB、獨立 ports/output；不重用或停止使用者正在執行的 hub。
#load "MilestoneReferences.fsx"
open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Argu
open Microsoft.Data.SqlClient

type RunArgument =
    | Milestone of string
    | Run_Root of string
    | Repeat_Count of int
    | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage = "Isolated Consolidated end-to-end validation"

type DemoOverride =
    | Hub_Url of string
    | Wrapper_Session of string
    | Output_Directory of string
    | Owner_Info of string
    | Stop_On_Stdin of bool
    interface IArgParserTemplate with member _.Usage = "Typed automation overrides; demo consumes them with FAkka.Argu"

let defaultArgumentsText = """
--milestone M010 --milestone M011 --milestone M012 --milestone M013
--milestone M014 --milestone M015 --milestone M016
--run-root F:/fodex/artifacts/Consolidated-validation
--repeat-count 3
--timeout-seconds 360
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length>1 then fsi.CommandLineArgs[1..] else defaults
let options = ArgumentParser.Create<RunArgument>().ParseCommandLine argv
let milestones = options.GetResults Milestone
let repeats = options.GetResult Repeat_Count
if repeats<1 || repeats>3 then invalidArg "repeat-count" "Use 1, 2 (new owner), or 3 (new owner then original owner resume)"
if milestones.IsEmpty || milestones |> List.exists (fun value -> not (List.contains value ["M010";"M011";"M012";"M013";"M014";"M015";"M016"])) then
    invalidArg "milestone" "Expected M010 through M016"
let runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,8)
let directory = Path.GetFullPath(Path.Combine(options.GetResult Run_Root,runId))
Directory.CreateDirectory directory |> ignore
let utf8 = UTF8Encoding(false)
let summaryPath = Path.Combine(directory,"results.jsonl")
let database = "FodexConsolidated_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0,8)
let connection = SqlConnectionStringBuilder()
connection.DataSource <- "localhost"
connection.InitialCatalog <- database
connection.IntegratedSecurity <- true
connection.TrustServerCertificate <- true
let master = SqlConnectionStringBuilder(connection.ConnectionString)
master.InitialCatalog <- "master"
do
    use sql = new SqlConnection(master.ConnectionString)
    sql.Open()
    use create = sql.CreateCommand()
    create.CommandText <- "DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql);"
    create.Parameters.AddWithValue("@name",database) |> ignore
    create.ExecuteNonQuery() |> ignore
// 保留 DB 供失敗 trace/handoff 查驗；不刪除 user DB，也不在檔案寫連線機密。
File.WriteAllText(Path.Combine(directory,"database.txt"),database,utf8)

type OwnedChild =
    { Name: string; Process: Process; Drain: Task; Streams: FileStream array
      Stdout: string; Stderr: string; Lifetime: Stopwatch }
let children = ResizeArray<OwnedChild>()
let append value = File.AppendAllText(summaryPath,JsonSerializer.Serialize(value) + Environment.NewLine,utf8)
let overrideParser = ArgumentParser.Create<DemoOverride>()

/// 讀取腳本明示的 defaultArgumentsText，再用同一個 PL；不另寫 argv/key-value parser。
let argumentsOf (path: string) overrides =
    let source = File.ReadAllText(path,utf8)
    let matched = Regex.Match(source,"let defaultArgumentsText = \"\"\"([\\s\\S]*?)\"\"\"")
    if not matched.Success then invalidOp ("No defaultArgumentsText in " + path)
    Array.append
        (PL.parseLine [|' '|] (Some '"') None true (matched.Groups[1].Value.ReplaceLineEndings(" ")))
        (overrideParser.PrintCommandLineArguments overrides)

let launch name script overrides =
    let path = Path.Combine(__SOURCE_DIRECTORY__,script)
    // Inherit this FSI's SDK instead of letting `dotnet fsi` silently choose another installed SDK.
    let fsiAssembly = Reflection.Assembly.GetEntryAssembly().Location
    if not (Path.GetFileName(fsiAssembly).Equals("fsi.dll", StringComparison.OrdinalIgnoreCase)) then
        invalidOp "ValidateMilestones must run in FSI"
    let start = ProcessStartInfo("dotnet")
    start.UseShellExecute <- false
    start.CreateNoWindow <- true
    start.WorkingDirectory <- __SOURCE_DIRECTORY__
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.RedirectStandardInput <- true
    start.Environment["FODEX_SQL_CONNECTION_STRING"] <- connection.ConnectionString
    for arg in Array.concat [[|fsiAssembly;"--exec";path|]; argumentsOf path overrides] do start.ArgumentList.Add arg
    let output,error = Path.Combine(directory,name+".stdout.log"),Path.Combine(directory,name+".stderr.log")
    // No managed capture buffer: READY/progress must be readable before the child exits.
    let streams = [|new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite,1,FileOptions.Asynchronous);new FileStream(error,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite,1,FileOptions.Asynchronous)|]
    let proc = new Process(StartInfo=start)
    let lifetime = Stopwatch.StartNew()
    if not(proc.Start()) then invalidOp ("Could not start " + name)
    let drain = Task.WhenAll [|proc.StandardOutput.BaseStream.CopyToAsync streams[0];proc.StandardError.BaseStream.CopyToAsync streams[1]|]
    let child = { Name=name; Process=proc; Drain=drain; Streams=streams; Stdout=output; Stderr=error; Lifetime=lifetime }
    children.Add child
    append {| stage="launched"; name=name; pid=proc.Id; utc=DateTime.UtcNow; stdout=output; stderr=error |}
    child

let finish child (seconds: float) =
    // Only this validation runner waits; product actors/HTTP requests remain asynchronous.
    // One sample per second includes FSI startup. 100% means one logical CPU, not the whole machine.
    let wait = Stopwatch.StartNew()
    let mutable previousWall, previousCpu, peak = 0., 0., 0.
    let sample () =
        let wall = child.Lifetime.Elapsed.TotalSeconds
        let cpu = child.Process.TotalProcessorTime.TotalSeconds
        let percent = if wall > previousWall then 100. * (cpu - previousCpu) / (wall - previousWall) else 0.
        peak <- max peak percent
        previousWall <- wall
        previousCpu <- cpu
    while not child.Process.HasExited && wait.Elapsed.TotalSeconds < seconds do
        sample ()
        child.Process.WaitForExit(1000) |> ignore
    sample ()
    append {| stage="process-cost"; name=child.Name; pid=child.Process.Id
              wallSeconds=child.Lifetime.Elapsed.TotalSeconds; cpuSeconds=previousCpu
              peakOneCorePercent=peak; peakMachinePercent=peak / float Environment.ProcessorCount
              logicalProcessors=Environment.ProcessorCount; exited=child.Process.HasExited |}
    if not child.Process.HasExited then raise (TimeoutException(child.Name + " completion timeout"))
    child.Drain.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
    for stream in child.Streams do stream.Dispose()
    if child.Process.ExitCode<>0 then invalidOp (sprintf "%s exited %d; inspect %s / %s" child.Name child.Process.ExitCode child.Stdout child.Stderr)
    File.ReadAllText(child.Stdout,utf8)

let requireFreePort (ip: string) port =
    let probe = new TcpListener(IPAddress.Parse ip,port)
    try probe.Start()
    finally probe.Stop()

let http = new HttpClient(Timeout=TimeSpan.FromSeconds 3.)
let awaitReady child predicate =
    let watch = Stopwatch.StartNew()
    let mutable ready = false
    while not ready && watch.Elapsed < TimeSpan.FromSeconds 90. do
        if child.Process.HasExited then finish child 1. |> ignore; invalidOp (child.Name + " exited before readiness")
        try ready <- predicate()
        with
        | :? HttpRequestException -> ()
        | :? TaskCanceledException -> () // Single HTTP probe timed out; overall 90s readiness budget is unchanged.
        if not ready then Thread.Sleep 100
    if not ready then raise (TimeoutException(child.Name + " readiness timeout"))

let runMain milestone round wrapperSid =
    let watch = Stopwatch.StartNew()
    let name = milestone + ".main" + string round
    let output = Path.Combine(directory,name)
    let overrides = [Output_Directory output] @ (wrapperSid |> Option.map (fun sid -> [Wrapper_Session sid]) |> Option.defaultValue [])
    let overrides =
        if List.contains milestone ["M010";"M011";"M012";"M013"] then
            overrides @ [Hub_Url(sprintf "http://127.0.0.1:%d" (18900 + Int32.Parse(milestone.Substring(1))))]
        else overrides
    let child = launch name ("WrapperIAgentDemo." + milestone + ".fsx") overrides
    let text = finish child (float(options.GetResult Timeout_Seconds))
    if not(text.Contains "COMPLETED_MATCH=") then invalidOp (name + " has no completion comparison evidence")
    if (milestone="M014" || milestone="M015") && not(text.Contains "COT_ORDER_VERIFIED=true") then invalidOp (name + " did not verify explicit COT ordering")
    let identity = Regex.Match(text,"WrapperSessionId\\s*\\(\\s*([a-fA-F0-9-]{36})")
    let sid = if identity.Success then identity.Groups[1].Value else ""
    let health = Regex.Match(text,"(?m)^HUB_HEALTH=(.+)$")
    let subs =
        if not health.Success then [||]
        else
            use document = JsonDocument.Parse health.Groups[1].Value
            document.RootElement.GetProperty("participants").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("who").GetString(),item.GetProperty("sessionIds").EnumerateArray() |> Seq.map _.GetString() |> Seq.exactlyOne)
            |> Seq.sortBy fst |> Seq.toArray
    append {| stage="passed"; name=name; wrapperSid=sid; subSessions=subs; wallSeconds=watch.Elapsed.TotalSeconds; utc=DateTime.UtcNow |}
    printfn "PASS %s wall=%.3fs" name watch.Elapsed.TotalSeconds
    sid,subs

let stopHttpHost child url =
    if not child.Process.HasExited then
        use response = http.PostAsync(url + "/stop",new StringContent("")).GetAwaiter().GetResult()
        response.EnsureSuccessStatusCode() |> ignore
    finish child 30. |> ignore

printfn "RUN_DIRECTORY=%s\nTEST_DATABASE=%s" directory database
let failures = ResizeArray<string>()
try
    for milestone in milestones do
        try
            if List.contains milestone ["M010";"M011";"M012";"M013"] then
                let port = 18900 + Int32.Parse(milestone.Substring(1))
                requireFreePort "127.0.0.1" port
                let url = sprintf "http://127.0.0.1:%d" port
                let host = launch (milestone+".host") ("WrapperIAgentDemo."+milestone+".Hub.fsx") [Hub_Url url; Output_Directory(Path.Combine(directory,milestone+".hub"))]
                let mutable primaryFailure: exn option = None
                try
                    try
                        awaitReady host (fun () -> use response=http.GetAsync(url+"/health").GetAwaiter().GetResult() in response.IsSuccessStatusCode)
                        let firstSid,firstSubs = runMain milestone 1 None
                        if String.IsNullOrWhiteSpace firstSid then invalidOp "Missing first wrapper sid"
                        if repeats>=2 then
                            let newSid,newSubs = runMain milestone 2 None
                            if firstSid=newSid || newSubs |> Array.exists (fun (_,sid) -> firstSubs |> Array.exists (snd >> (=) sid)) then invalidOp "New wrapper reused another owner's session"
                        if repeats>=3 then
                            let resumedSid,resumedSubs = runMain milestone 3 (Some firstSid)
                            if resumedSid<>firstSid || resumedSubs<>firstSubs then invalidOp "Original wrapper did not resume its original sub sessions"
                        append {| stage="lifecycle-passed"; milestone=milestone; repeats=repeats; utc=DateTime.UtcNow |}
                    with ex ->
                        primaryFailure <- Some ex
                        reraise()
                finally
                    try stopHttpHost host url
                    with cleanupError ->
                        match primaryFailure with
                        | Some _ -> append {| stage="cleanup-failed"; name=host.Name; error=cleanupError.Message; utc=DateTime.UtcNow |}
                        | None -> raise cleanupError
            elif milestone="M016" then
                requireFreePort "10.28.112.109" 21916
                requireFreePort "10.28.112.108" 21917
                let manifest = Path.Combine(directory,"M016.owner.json")
                let owner = launch "M016.owner" "WrapperIAgentDemo.M016.Host.fsx" [Output_Directory(Path.Combine(directory,"M016.owner"));Owner_Info manifest;Stop_On_Stdin true]
                try
                    awaitReady owner (fun () -> File.Exists manifest)
                    for round in 1..2 do
                        let client = launch ("M016.client"+string round) "WrapperIAgentDemo.M016.fsx" [Output_Directory(Path.Combine(directory,"M016.client"+string round));Owner_Info manifest]
                        let output = finish client (float(options.GetResult Timeout_Seconds))
                        if not(output.Contains "REMOTE_COMPLETED_MATCH=") then invalidOp "No remote completion/PID evidence"
                        append {| stage="passed"; name="M016.client"+string round; utc=DateTime.UtcNow |}
                        printfn "PASS M016.client%d" round
                        if owner.Process.HasExited then invalidOp "Remote client shutdown also stopped owner"
                finally
                    if not owner.Process.HasExited then owner.Process.StandardInput.WriteLine("stop"); owner.Process.StandardInput.Close()
                    finish owner 30. |> ignore
            else runMain milestone 1 None |> ignore
        with ex ->
            failures.Add(milestone + ": " + ex.Message)
            append {| stage="failed"; milestone=milestone; error=ex.Message; utc=DateTime.UtcNow |}
            eprintfn "FAIL %s: %s" milestone ex.Message
finally
    // 僅處理本 runner 自己 Process.Start 回傳的 handles；絕不以名稱掃描/終止使用者程序。
    for child in children do
        if not child.Process.HasExited then
            failures.Add(child.Name + ": required emergency cleanup")
            child.Process.Kill(true)
            child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
        child.Drain.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
        for stream in child.Streams do stream.Dispose()
        child.Process.Dispose()
    http.Dispose()
append {| stage="finished"; failed=failures.Count; failures=failures.ToArray(); database=database; utc=DateTime.UtcNow |}
printfn "RESULTS=%s\nFAILED=%d" summaryPath failures.Count
if failures.Count>0 then failwithf "%d validation failures; inspect %s" failures.Count summaryPath
