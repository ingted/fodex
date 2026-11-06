#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ConsolePrinterFodexFunV403Tests
#endif

open System
open System.Threading
open Expecto
open Akka.Actor
open LoggerUtil
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

[<Tests>]
let tests = testList "Console printer V403" [
    testCase "blocked display does not block the caller and drain has a deadline" <| fun () -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic definitions"
        use entered = new ManualResetEventSlim()
        use release = new ManualResetEventSlim()
        let write _ _ = entered.Set(); release.Wait()
        let post,drain = evaluated<ActorSystem * UniContext * (bool -> string -> unit),ConsolePrinterV403>
                            "4.0.3" node "Console.Printer.Create" (node,ctx,write) |> required
        try
            post true "output"
            Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 3.)) "dedicated printer entered"
            Expect.isError (drain(TimeSpan.FromMilliseconds 50.) |> wait) "no indefinite shutdown drain"
            release.Set()
            drain(TimeSpan.FromSeconds 3.) |> wait |> required
        finally release.Set())
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsolePrinterFodexFunV403Tests failed"
#endif
