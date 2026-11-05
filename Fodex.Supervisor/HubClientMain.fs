module Fodex.Supervisor.HubClientMain

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Fodex.Supervisor.Contracts.Concepts

/// 同一實作組件的 CLI 邊界；#r/ProjectReference 不執行 EntryPoint。
/// 例：dotnet Fodex.Supervisor.dll --url http://127.0.0.1:18913/owners/ID --action receive
/// 從 BaseCD pin HubClient.Run 1.0.1，再透過 FeasibleFun.invoke 執行 FAkka.Argu/HTTP。
/// 這是無 node 的 transport 入口，不建立 ActorSystem、wrapper 或 host。
[<EntryPoint>]
let main argv =
    if Console.IsOutputRedirected then Console.OutputEncoding <- Text.UTF8Encoding(false)
    use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
    try
        FodexFunctions.initialize()
        let operation =
            FodexFun<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>,exn>(
                VersionRegistry<FeasibleFun<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>,exn>>.BaseCD,
                HubClientFodexFunV101.targetRun)
            |> FodexFun.pin (Some "1.0.1")
            |> Option.defaultWith(fun () -> invalidOp "Missing HubClient.Run 1.0.1")
        let result =
            FeasibleFun.invoke (http,argv,(fun (text:string) -> Console.Error.WriteLine text),CancellationToken.None) operation
            |> Result.bind(fun work -> work.GetAwaiter().GetResult())
        match result with
        | Ok response ->
            printfn "%s" response
            0
        | Error error -> raise error
    with ex ->
        // 不把 instruction/token 等任意 exception payload 放到 transport log。
        Console.Error.WriteLine("FODEX_CLIENT_FAILED " + ex.GetType().Name)
        1
