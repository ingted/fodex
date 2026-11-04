open System
open System.Threading
open System.Threading.Tasks

type Requester =
    { RequestAsync: Guid * string * CancellationToken -> Task<string>
      Retire: unit -> unit
      IsRetired: unit -> bool }

/// Each callback closes over one request. Cancellation owns the session gate until acknowledged.
let createRequester (submit: string * (Result<string,exn> -> unit) -> Result<unit,exn>) (cancel: unit -> Task<unit>) =
    let gate = new SemaphoreSlim(1,1)
    let mutable retired = false
    let request (_,prompt,ct: CancellationToken) = task {
        do! gate.WaitAsync ct
        try
            ct.ThrowIfCancellationRequested()
            if Volatile.Read(&retired) then invalidOp "line-v301-session-retired"
            let response = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            let callback (result: Result<string,exn>) =
                match result with
                | Ok text -> response.TrySetResult text |> ignore
                | Error error -> response.TrySetException error |> ignore
            match submit(prompt,callback) with
            | Ok () -> ()
            | Error error -> response.TrySetException error |> ignore
            let! completed = Task.WhenAny(response.Task :> Task, Task.Delay(Timeout.Infinite,ct))
            if obj.ReferenceEquals(completed,response.Task) then return! response.Task
            else
                try
                    // No caller cancellation token here: cancellation acknowledgement is the safety barrier.
                    do! (cancel()).WaitAsync(TimeSpan.FromSeconds 30.)
                with error ->
                    Volatile.Write(&retired,true)
                    raise error
                return raise (OperationCanceledException(ct))
        finally
            gate.Release() |> ignore
    }
    { RequestAsync=request; Retire=(fun () -> Volatile.Write(&retired,true)); IsRetired=fun () -> Volatile.Read(&retired) }
