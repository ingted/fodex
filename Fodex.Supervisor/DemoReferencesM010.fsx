// M010/M011 actor 修正版共用 FSI reference；先依 README.M010-M011.md build。
// deps.json + installed shared framework resolver，不複製或寫死 NuGet TFM DLL。
#I @"bin\M010.Actor"
#r "Akka.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load @"C:\Users\Administrator\.codex\lib\ParseLine.fsx"

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open System.Text.Json
open System.Threading
open Akka.Actor
open Argu
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

// 由 build 的 deps.json 解相依組件，不複製硬編碼 NuGet lib/netX 路徑。
// FSI 不是產品 host；shared framework fallback 保留 M003 已可用的載入方式。
let assemblyDirectory = Path.Combine(__SOURCE_DIRECTORY__,"bin","M010.Actor")
let dependencyResolver = AssemblyDependencyResolver(Path.Combine(assemblyDirectory,"Fodex.Supervisor.Demos.dll"))
let sharedDirectory = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspNetDirectory = Path.Combine(sharedDirectory,"Microsoft.AspNetCore.App")
let sharedLibraries =
    if Directory.Exists aspNetDirectory then
        Directory.GetDirectories aspNetDirectory
        // .NET 10 FSI 不載入較新的 .NET 11 shared-framework DLL。
        |> Array.filter (fun path -> System.Version.Parse((Path.GetFileName path).Split('-')[0]).Major <= Environment.Version.Major)
        |> Array.sortByDescending (fun path -> System.Version.Parse((Path.GetFileName path).Split('-')[0]))
    else [||]
AssemblyLoadContext.Default.add_Resolving(Func<AssemblyLoadContext,AssemblyName,Assembly>(fun loader name ->
    let resolved = dependencyResolver.ResolveAssemblyToPath name
    let local = Path.Combine(assemblyDirectory,name.Name + ".dll")
    if not (isNull resolved) then loader.LoadFromAssemblyPath resolved
    elif File.Exists local then loader.LoadFromAssemblyPath local
    else
        sharedLibraries |> Array.map (fun directory -> Path.Combine(directory,name.Name + ".dll"))
        |> Array.tryFind File.Exists |> Option.map loader.LoadFromAssemblyPath |> Option.defaultValue null))
AssemblyLoadContext.Default.add_ResolvingUnmanagedDll(Func<Assembly,string,nativeint>(fun _ name ->
    match dependencyResolver.ResolveUnmanagedDllToPath name with
    | null -> 0n
    | path -> NativeLibrary.Load path))


#r "Fodex.Supervisor.Demos.dll"
