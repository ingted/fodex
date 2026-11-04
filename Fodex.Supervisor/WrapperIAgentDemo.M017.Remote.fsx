// 純 remote console client：先在另一個 FSI 執行 M017 owner。
// 此檔不建立 wrapper/native/hub，不 build/spawn/stop owner，只建立一個 client Akka node。
#load "M017References.fsx"
open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Argu
open Akka.Actor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor

type RemoteArguments =
    | Owner_File of string
    | Bind_Ip of string
    | Port of int
    | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage="M017 remote control setting"
let defaultArgumentsText="""
--owner-file F:/fodex/artifacts/M017/latest.owner.json
--bind-ip 127.0.0.1
--port 21818
--timeout-seconds 1800
"""
let argv=if fsi.CommandLineArgs.Length>1 then fsi.CommandLineArgs[1..] else PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let options=ArgumentParser.Create<RemoteArguments>().ParseCommandLine argv
let required=function Ok value -> value | Error(ex:exn) -> raise ex
use document=JsonDocument.Parse(File.ReadAllText(options.GetResult Owner_File))
let address=document.RootElement.GetProperty("owner").GetString()
if ActorPath.Parse(address).Address.HasLocalScope then invalidArg "owner" "Expected remote Akka address"
let node=ActorSystem.Create("m017-control-client-"+Guid.NewGuid().ToString("N"),RemoteFodexFunV209.config (options.GetResult Bind_Ip) (options.GetResult Port))
let remote=node.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
RemoteControlFodexFunV300.register node
let call=ReActLoopHandlerV300.bind<RemoteCommandV300,Task<Result<string,exn>>> node RemoteControlFodexFunV300.target
let send text=call(remote,text,TimeSpan.FromSeconds(float(options.GetResult Timeout_Seconds))) |> required
let print text=lock Console.Out (fun () -> Console.WriteLine(text:string))
print("Connected to "+address+". Generation output remains on the owner console; control replies are printed here. EOF disconnects only this client.")
try
    let mutable reading=true
    while reading do
        Console.Write("M017 remote> ")
        match Console.ReadLine() with
        | null -> reading<-false
        | text when String.IsNullOrWhiteSpace text -> ()
        | text ->
            (send text).ContinueWith(Action<Task<Result<string,exn>>>(fun result ->
                if result.IsFaulted then print("ERROR: "+result.Exception.Message) else
                match result.Result with Ok reply -> print reply | Error ex -> print("ERROR: "+ex.Message))) |> ignore
finally node.Terminate().GetAwaiter().GetResult() |> ignore
