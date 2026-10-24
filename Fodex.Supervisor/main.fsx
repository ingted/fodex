namespace Fodex.Supervisor

#if INTERACTIVE
#r @"bin\Debug\net10.0\Fodex.Supervisor.Contracts.dll"
#endif

module Main =
    open System.Diagnostics
    open Fodex.Supervisor.Contracts
    open Concepts

    //type Fodex =
    //    static member inline start<'T when 'T: (new: unit -> 'T) and 'T: (member Start: unit -> unit)> () =
    //        let instance = new 'T()
    //        instance.Start()
    


    [<EntryPoint>]
    //類似 Codex cli 吃 argv 起動
    let main argv =
        //最核心的需求就是起動一個 cluster actor 可以接收 msg 並且處理
        let cli = { new Cli () }
        cli.CliStart argv
        0