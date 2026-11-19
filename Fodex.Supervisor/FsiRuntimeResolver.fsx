// Shared FSI runtime loading, extracted unchanged from SupervisorReferences.fsx.
// Executables use their runtimeconfig/deps files; FSI needs the shared-framework fallback.
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader

let install assemblyPath =
    let assemblyDirectory=Path.GetDirectoryName(Path.GetFullPath assemblyPath)
    let resolver=AssemblyDependencyResolver assemblyPath
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
