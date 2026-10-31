#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV200.fsx"
#load "../Fodex.Supervisor/WrapperTurnTimingV100.fsx"
#else
module WrapperTurnTimingV100Tests
#endif

open System
open System.Diagnostics
open Expecto
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor
open ImplementationTestSupportV200

[<Tests>]
let tests = testList "Whole wrapper turn timing" [
    testCase "fixture wait consumes an already signalled AutoResetEvent exactly once" <| fun () ->
        use signal = new System.Threading.AutoResetEvent(true)
        Expect.isTrue (waitForSignal signal (TimeSpan.FromSeconds 10.)) "existing signal is not lost"
        Expect.isFalse (waitForSignal signal TimeSpan.Zero) "one signal cannot complete another turn"
    testCase "fixture wait returns false on timeout without inventing a signal" <| fun () ->
        use signal = new System.Threading.AutoResetEvent(false)
        Expect.isFalse (waitForSignal signal (TimeSpan.FromMilliseconds 20.)) "same timeout result as WaitOne"
    testCase "fixture wait observes a later asynchronous signal" <| fun () ->
        use signal = new System.Threading.AutoResetEvent(false)
        let completion = task {
            do! System.Threading.Tasks.Task.Delay 20
            signal.Set() |> ignore
        }
        Expect.isTrue (waitForSignal signal (TimeSpan.FromSeconds 10.)) "actual completion required"
        completion.GetAwaiter().GetResult()
    testCase "Completed commentary/work messages do not finish the turn" <| fun () ->
        let watch = Stopwatch.StartNew()
        let messages: ReActEvent<string> list =
            [ AgentCot(1L,0L,MessageCompleted("commentary","working"))
              AgentWork(2L,0L,MessageCompleted("answer","orz")) ]
        for event in messages do
            Expect.isNone (WrapperTurnTimingV100.tryFinish watch event) "message completion is not turn completion"
            Expect.isTrue watch.IsRunning "stopwatch continues until AgentEnd"
        watch.Stop()
    testCase "Delta and error do not fake a completed turn" <| fun () ->
        let watch = Stopwatch.StartNew()
        let events: ReActEvent<string> list =
            [ AgentCot(1L,0L,MessageDelta("id","o")); StdOut(2L,0L,"tool output"); StdErr(3L,0L,"failure") ]
        for event in events do Expect.isNone (WrapperTurnTimingV100.tryFinish watch event) "await the terminal boundary"
        Expect.isTrue watch.IsRunning "failure reporting alone is not AgentEnd"
        watch.Stop()
    testCase "Only AgentEnd stops once and returns the complete elapsed duration" <| fun () ->
        let watch = Stopwatch.StartNew()
        let ending: ReActEvent<string> = AgentEnd(4L,3L,Message "orz")
        let elapsed = WrapperTurnTimingV100.tryFinish watch ending |> Option.get
        Expect.isFalse watch.IsRunning "terminal event stopped the clock"
        Expect.equal elapsed watch.Elapsed "duration is the original stopwatch interval"
        Expect.isGreaterThanOrEqual elapsed TimeSpan.Zero "monotonic elapsed is nonnegative"
        Expect.isNone (WrapperTurnTimingV100.tryFinish watch ending) "duplicate end cannot report a second completion"
    testCase "Console distinguishes message kind without changing the original event" <| fun () ->
        let commentary: ReActEvent<string> = AgentCot(1L,0L,MessageCompleted("id","still waiting"))
        let work: ReActEvent<string> = AgentWork(2L,1L,MessageCompleted("id2","OGC"))
        Expect.equal (WrapperTurnTimingV100.completedMessage commentary) (Some("CotMessage","still waiting")) "not TurnCompleted"
        Expect.equal (WrapperTurnTimingV100.completedMessage work) (Some("WorkMessage","OGC")) "work message also not TurnCompleted"
        Expect.isNone (WrapperTurnTimingV100.completedMessage (AgentEnd(3L,2L,Message "OGC"))) "terminal has a separate display path"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "WrapperTurnTimingV100Tests failed: %d" testExitCode
#endif
