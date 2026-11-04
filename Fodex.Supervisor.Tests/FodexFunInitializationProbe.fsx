// 此檔由 Expecto 在獨立 FSI 啟動，--lib 指向本次測試輸出；不讀正在運作的 M019 DLL。
// 沒有業務 argv；測試 DB 只經本子程序的環境變數傳入，不輸出連線資訊。
#r "Akka.dll"
#r "Akka.Logger.NLog.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "FAkka.FSharp.Quotations.Evaluator.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "nuget: Microsoft.Extensions.ObjectPool, 10.0.14"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load "../Fodex.Supervisor.Contract/LoggerAdapter.fsx"

open System
open System.Text.Json
open Akka.Actor
open Akka.Configuration
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let check condition message=if not condition then failwith message
let required=function Ok value -> value | Error(error:exn) -> raise error
let cs=Environment.GetEnvironmentVariable "FODEX_TEST_QUOTATION_DB"
check (not(String.IsNullOrWhiteSpace cs)) "The isolated test database was not supplied"

// 唯一一次明確 entrypoint 初始化；重複呼叫不改 baseline、node cache 或已 pin 的 function。
FodexFunctions.initialize()
FodexFunctions.initialize()
check (FodexFunctions.builtins.Length>=116) "The entrypoint did not evaluate every declared module"

let ctx={logger=toUL(UniversalLogger<_>(NLog.LogManager.GetLogger "QuotationProbe"));usage=["probe"];caller=[]}
let target=WrapperCommandFodexFunV300.target
let version=System.Version(4,0,0)
let definition prefix = FeasibleFun<UniContext*string,WrapperCommandV300,exn>(
    <@ fun _ -> fun (_,text) -> Ok(WrapperCommandV300.Immediate(prefix+text)) @>)
let invoke fn text=FeasibleFun.invoke (ctx,text) fn |> required

// DB 寫入在此才開始；這筆 override 必須壓過編譯時的 parser q。
QuotationStore.write<UniContext*string,WrapperCommandV300,exn> cs
    [|target,version,Some(definition "stored:")|] |> required
let config=ConfigurationFactory.ParseString(
    "akka.loglevel=OFF\nakka.stdout-loglevel=OFF\nfodex.quotations.connection-string="+JsonSerializer.Serialize cs)
let nodes=ResizeArray<ActorSystem>()
let node () =
    let value=ActorSystem.Create("quotation-probe-"+Guid.NewGuid().ToString("N"),config)
    nodes.Add value
    value

try
    let first=node()
    let versions=FodexFun<UniContext*string,WrapperCommandV300,exn>(first,target)
    let pinned=versions[version].Value
    pinned.f.Value |> ignore
    check (invoke pinned "one"=WrapperCommandV300.Immediate "stored:one") "SQL did not override baseline"
    versions.Register(version,definition "updated:")
    check (invoke pinned "two"=WrapperCommandV300.Immediate "stored:two") "An existing pin changed"
    let second=node()
    let restored=FodexFun<UniContext*string,WrapperCommandV300,exn>(second,target).[version].Value
    check (invoke restored "three"=WrapperCommandV300.Immediate "updated:three") "Register did not persist for another node"
    let original=FodexFun<UniContext*string,WrapperCommandV300,exn>(VersionRegistry<FeasibleFun<UniContext*string,WrapperCommandV300,exn>>.BaseCD,target).[version].Value
    check (invoke original "/list_participant"=WrapperCommandV300.ListParticipants) "Runtime update changed BaseCD"
    VersionRegistry<FeasibleFun<UniContext*string,WrapperCommandV300,exn>>(first).CleanVersioned(target,fun () -> [|version|])
    let third=node()
    check (FodexFun<UniContext*string,WrapperCommandV300,exn>(third,target).[version].IsNone) "Deleted definition was resurrected"
    printfn "FODEX_BUILTIN_SQL_PROBE_OK declarations=%d" FodexFunctions.builtins.Length
finally
    for value in nodes do value.Terminate().GetAwaiter().GetResult() |> ignore
