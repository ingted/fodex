#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Lifecycle
#endif
open System
open System.Threading.Tasks

/// Cleanup consumes one budget in ownership order. Failure is retained while later
/// independent resources still get a cleanup attempt; no caller-owned service is stopped.
let shutdown seconds (resources:(string*(TimeSpan -> Task)) list)=task {
    let timer=Diagnostics.Stopwatch.StartNew()
    let budget=TimeSpan.FromSeconds(float seconds)
    let failures=ResizeArray<exn>()
    for name,release in resources do
        let remaining=max (TimeSpan.FromMilliseconds 1.) (budget-timer.Elapsed)
        try do! (release remaining).WaitAsync remaining
        with error -> failures.Add(InvalidOperationException("Owned resource cleanup not confirmed: "+name,error))
    if failures.Count>0 then return raise(AggregateException("LINE v4 shutdown incomplete",failures)) }
