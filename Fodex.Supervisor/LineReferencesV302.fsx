// M019 LINE v2 authority R2: committed F source 8c9b80e, SDK 11 RC2, isolated output.
// This version owns only references/resolution. It starts no actors, native processes or services.
// Product references may alias: module LineReferencesV301 = LineReferencesV302
#I @"G:\PulseTrade.fs\temp\m019-line-v2-authority-fixture\F-8c9b80e-gitbytes\Fodex.Supervisor\bin\M019.Authority.R2\Debug\net10.0\win-x64"
#r "Akka.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "FAkka.FCell2.dll"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#r "Akka.Proc.Supervisor.dll"
#r "ModelContextProtocol.Core.dll"
#r "ModelContextProtocol.dll"
#r "ModelContextProtocol.AspNetCore.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load @"G:\PulseTrade.fs\temp\m019-line-v2-authority-fixture\F-8c9b80e-gitbytes\Fodex.Supervisor.Contract\LoggerAdapter.fsx"
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open NLog

let sourceCommit = "8c9b80e435f6ec0ffba4a478cc69c4cd1674a3d3"
let assemblyDirectory = @"G:\PulseTrade.fs\temp\m019-line-v2-authority-fixture\F-8c9b80e-gitbytes\Fodex.Supervisor\bin\M019.Authority.R2\Debug\net10.0\win-x64"
let supervisorSha256 = "C4B8E2330D039D10CCF3C5823E3014C64DEAE281F6AF177C3AC4D9B672C75DA7"
let contractSha256 = "7CF7E69AC7315BB21C63B042CC284CD7AC3DA78A3D426446EAD3E04E3BFD7900"
let digest path = Security.Cryptography.SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString
let requireHash name expected =
    let path = Path.Combine(assemblyDirectory,name+".dll")
    if digest path <> expected then invalidOp ("line-v2-authority-hash-mismatch: "+name)
    path
let supervisorPath = requireHash "Fodex.Supervisor" supervisorSha256
let contractPath = requireHash "Fodex.Supervisor.Contracts" contractSha256
let resolver = AssemblyDependencyResolver(supervisorPath)
let sharedRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspRoot = Path.Combine(sharedRoot,"Microsoft.AspNetCore.App")
let sharedLibraries =
    if not(Directory.Exists aspRoot) then [||] else
    Directory.GetDirectories aspRoot
    |> Array.filter(fun path -> Version.Parse((Path.GetFileName path).Split('-')[0]).Major=Environment.Version.Major)
    |> Array.sortByDescending(fun path -> Version.Parse((Path.GetFileName path).Split('-')[0]))
AssemblyLoadContext.Default.add_Resolving(Func<AssemblyLoadContext,AssemblyName,Assembly>(fun loader name ->
    let resolved = resolver.ResolveAssemblyToPath name
    let local = Path.Combine(assemblyDirectory,name.Name+".dll")
    if not(isNull resolved) then loader.LoadFromAssemblyPath resolved
    elif File.Exists local then loader.LoadFromAssemblyPath local
    else sharedLibraries |> Array.map(fun directory -> Path.Combine(directory,name.Name+".dll"))
         |> Array.tryFind File.Exists |> Option.map loader.LoadFromAssemblyPath |> Option.defaultValue null))
AssemblyLoadContext.Default.add_ResolvingUnmanagedDll(Func<Assembly,string,nativeint>(fun _ name ->
    match resolver.ResolveUnmanagedDllToPath name with null -> 0n | path -> NativeLibrary.Load path))
let canonicalDependencyEvidence =
    [ "Argu"; "ModelContextProtocol.Core"; "ModelContextProtocol"; "ModelContextProtocol.AspNetCore"
      "FAkka.Shared"; "FAkka.FCell2"; "FsPickler"; "FsPickler.Json"; "Akka.Proc.Supervisor"
      "Akka"; "NLog"; "Microsoft.Data.SqlClient"; "FSharp.FGL"; "FSharp.FGL.ArrayAdjacencyGraph"
      "Fodex.Supervisor.Contracts"; "Fodex.Supervisor" ]
    |> List.map(fun name ->
        let expectedPath = Path.Combine(assemblyDirectory,name+".dll") |> Path.GetFullPath
        let loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath expectedPath
        if not(String.Equals(Path.GetFullPath loaded.Location,expectedPath,StringComparison.OrdinalIgnoreCase)) then
            invalidOp ("line-v2-authority-loaded-path-mismatch: "+name)
        $"{name} version={loaded.GetName().Version} path={loaded.Location} sha256={digest expectedPath}")
NLog.LogManager.Setup().LoadConfigurationFromFile(Path.Combine(assemblyDirectory,"NLog.M017.conf"),false) |> ignore
