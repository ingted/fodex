module Fodex.Supervisor.HubClientMain

open System
open System.Net.Http
open System.Threading

/// 同一實作組件的 CLI 邊界；#r/ProjectReference 不執行 EntryPoint。
/// 例：dotnet Fodex.Supervisor.dll --url http://127.0.0.1:18913/owners/ID --action receive
/// 只將 argv 交給 HubClientV101 的 FAkka.Argu；不建立 ActorSystem、wrapper 或 host。
[<EntryPoint>]
let main argv =
    if Console.IsOutputRedirected then Console.OutputEncoding <- Text.UTF8Encoding(false)
    use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
    try
        let result = HubClientV101.run http argv Console.Error.WriteLine CancellationToken.None
        printfn "%s" (result.GetAwaiter().GetResult())
        0
    with ex ->
        // 不把 instruction/token 等任意 exception payload 放到 transport log。
        Console.Error.WriteLine("FODEX_CLIENT_FAILED " + ex.GetType().Name)
        1
