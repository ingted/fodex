#if INTERACTIVE
// 純 V300 quotation 測試先載入 Contract，不依賴尚在移植的舊 implementation DLL。
#I "../Fodex.Supervisor.Contract.Tests/bin/Debug/net10.0"
#r "Expecto.dll"
#r "Akka.dll"
#r "Akka.Logger.NLog.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "FAkka.FSharp.Quotations.Evaluator.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "nuget: Microsoft.Extensions.ObjectPool, 10.0.14"
// SqlClient 的 FSI NuGet resolver 在目前 SDK 會載到 PNSE stub；使用 MSBuild 選好的 runtime asset。
// 只 reference 正常 build output，不手工複製套件內部 lib/netX 檔案。
#I "bin/Debug/net10.0/win-x64"
#r "Microsoft.Data.SqlClient.dll"
#r "Fodex.Supervisor.Contracts.dll"
#load "../Fodex.Supervisor.Contract/LoggerAdapter.fsx"
#load "../Fodex.Supervisor/FodexFunRegistry.fsx"
#load "QuotationTestScope.fsx"
#else
module ImplementationTestSupportV300
#endif

open System
open Akka.Actor
open Akka.Configuration
open Expecto
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor.Contracts.Concepts

let ctx : UniContext =
    { logger = toUL (UniversalLogger<_>(NLog.LogManager.GetLogger "Fodex.M017.Tests"))
      usage = ["M017.tests"]; caller = [] }

let required (result: Result<'T,exn>) =
    match result with
    | Ok value -> value
    | Error ex -> failtestf "%O" ex

/// Intentional test hot-plugs use the fixture's real SQL authority, just like production.
/// Install before the first typed registry access; never use a shared production database.
let useQuotationDatabase (node:ActorSystem) (cs:string) =
    node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
        "fodex.quotations.connection-string="+System.Text.Json.JsonSerializer.Serialize cs))

/// 每個測試使用自己的 registry 分區與 quotation SQL；不啟動 Codex/native process。
let withNode action =
    let node = ActorSystem.Create("m017-test-" + Guid.NewGuid().ToString("N"),
                                ConfigurationFactory.ParseString "akka.loglevel=OFF\nakka.stdout-loglevel=OFF")
    try
        QuotationTestScope.attach node |> ignore
        try action node
        with error ->
            Console.Error.WriteLine("FIXTURE_ACTION_FAILED "+error.GetType().FullName+" "+error.StackTrace)
            reraise()
    finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore

/// 先 eval 真正 production quotation，再取得 pinned operation；不以 test stub 取代它。
let evaluated<'Input,'Output> (node: ActorSystem) (target: string) =
    let fn = FodexFun<'Input,'Output,exn>(node,target) |> FodexFun.pin (Some "3.0.0")
             |> Option.defaultWith (fun () -> failtestf "Missing V300 target: %s" target)
    fn.f.Value |> ignore
    fun input -> FeasibleFun.invoke input fn

/// 沿用本機 SQL integration fixture：只建立及清除自己生成的測試 DB，不接受使用者 DB 名稱。
let withDatabase action =
    // Schema 仍由 production initializer 建立；scope 只擁有 database 與 node 設定。
    QuotationTestScope.withDatabase "FodexTestsV300_" action
