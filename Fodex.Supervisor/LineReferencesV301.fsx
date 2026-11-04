// V301 uses the M017 DLL/resolver/NLog pattern without loading ParseLine.
// The LINE provider already loads its compatible PL module; a second home PL causes FSI FS0239.
// No changes are made to M017References.fsx or either ParseLine implementation.
// M017 使用本實作專案的正常 output；預設 Debug，隔離驗證可指定 RELEASE。
// 不另建 Demo project、不複製 NuGet TFM DLL。
#if RELEASE
#I @"bin\Release\net10.0\win-x64"
#else
#I @"bin\Debug\net10.0\win-x64"
#endif
#r "Akka.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load "../Fodex.Supervisor.Contract/LoggerAdapter.fsx"
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open NLog

#if RELEASE
let assemblyDirectory=Path.Combine(__SOURCE_DIRECTORY__,"bin","Release","net10.0","win-x64")
#else
let assemblyDirectory=Path.Combine(__SOURCE_DIRECTORY__,"bin","Debug","net10.0","win-x64")
#endif
let resolver=AssemblyDependencyResolver(Path.Combine(assemblyDirectory,"Fodex.Supervisor.dll"))
let sharedRoot=Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspRoot=Path.Combine(sharedRoot,"Microsoft.AspNetCore.App")
let sharedLibraries=
    if not(Directory.Exists aspRoot) then [||] else
    Directory.GetDirectories aspRoot
    |> Array.filter(fun path -> System.Version.Parse((Path.GetFileName path).Split('-')[0]).Major=Environment.Version.Major)
    |> Array.sortByDescending(fun path -> System.Version.Parse((Path.GetFileName path).Split('-')[0]))
AssemblyLoadContext.Default.add_Resolving(Func<AssemblyLoadContext,AssemblyName,Assembly>(fun loader name ->
    let resolved=resolver.ResolveAssemblyToPath name
    let local=Path.Combine(assemblyDirectory,name.Name+".dll")
    if not(isNull resolved) then loader.LoadFromAssemblyPath resolved
    elif File.Exists local then loader.LoadFromAssemblyPath local
    else sharedLibraries |> Array.map(fun directory -> Path.Combine(directory,name.Name+".dll"))
         |> Array.tryFind File.Exists |> Option.map loader.LoadFromAssemblyPath |> Option.defaultValue null))
AssemblyLoadContext.Default.add_ResolvingUnmanagedDll(Func<Assembly,string,nativeint>(fun _ name ->
    match resolver.ResolveUnmanagedDllToPath name with null -> 0n | path -> NativeLibrary.Load path))
NLog.LogManager.Setup().LoadConfigurationFromFile(Path.Combine(__SOURCE_DIRECTORY__,"NLog.M017.conf"),false) |> ignore
