#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#r "nuget: FAkka.Proc.Supervisor, 1.572.10-fodex002"
#load "ImplementationTestSupportV200.fsx"

// FSI 不是 dotnet test host；以建置產物的 deps/local assemblies 解 runtime 動態載入。
// 不從 NuGet cache 猜 lib/net6.0 等路徑、不改產品的 assembly loading。
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
let assemblyDirectory = Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64")
let dependencyResolver = AssemblyDependencyResolver(Path.Combine(assemblyDirectory,"Fodex.Supervisor.dll"))
// NuGet 的 framework reference 由正常 host 啟用；FSI 不會替 #r 啟動 ASP.NET runtime。
// 與 M005 一樣從已安裝 shared framework 解決，不把 NuGet lib/netX 路徑寫死。
let sharedDirectory = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspNetDirectory = Path.Combine(sharedDirectory,"Microsoft.AspNetCore.App")
let sharedLibraries =
    if Directory.Exists aspNetDirectory then
        Directory.GetDirectories aspNetDirectory
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
#else
module ImplementationTestSupportV201
#endif

// FSI 的共同參考；測試資料、SQL fixture、ActorSystem fixture 沿用 V200，不複製一套。
let milestone = "V201"
