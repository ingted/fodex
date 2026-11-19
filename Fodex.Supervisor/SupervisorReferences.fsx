// 共用 FSI 相依；預設 Release。要使用 Debug 請指定 --define:DEBUG。
// 不另建 Demo project、不複製 NuGet TFM DLL。
#if DEBUG
#I @"bin\Debug\net10.0\win-x64"
#else
#I @"bin\Release\net10.0\win-x64"
#endif
#r "Akka.dll"
#r "Akka.FSharp.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load "../Fodex.Supervisor.Contract/LoggerAdapter.fsx"
#load @"C:\Users\Administrator\.codex\lib\ParseLine.fsx"
#load "FsiRuntimeResolver.fsx"
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open NLog

#if DEBUG
let assemblyDirectory=Path.Combine(__SOURCE_DIRECTORY__,"bin","Debug","net10.0","win-x64")
#else
let assemblyDirectory=Path.Combine(__SOURCE_DIRECTORY__,"bin","Release","net10.0","win-x64")
#endif
FsiRuntimeResolver.install(Path.Combine(assemblyDirectory,"Fodex.Supervisor.dll"))
NLog.LogManager.Setup().LoadConfigurationFromFile(Path.Combine(__SOURCE_DIRECTORY__,"NLog.M017.conf"),false) |> ignore
