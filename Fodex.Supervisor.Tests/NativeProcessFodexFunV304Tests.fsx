#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#r "nuget: FAkka.Proc.Supervisor, [1.572.17]"
#else
module NativeProcessFodexFunV304Tests
#endif
open System
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open System.Text
open Akka.Actor
open Akka.FSharp
open Akka.Proc.Supervisor
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testSequenced <| testList "1116 root resource ownership V304" [
    for deadActor in [false;true] do
      testCase (sprintf "root owner closes actual native Job and child when native actor dead=%b" deadActor) <| fun () -> withDatabase(fun _ -> withNode(fun node ->
        let owner=evaluated<unit,NativeOwnershipV304> "3.0.4" node "NativeProcess.CreateOwnership" () |> required
        let native=spawn node "unresponsive-native" (fun (mailbox:Actor<obj>) ->
            let rec receive()=actor { let! _=mailbox.Receive()
                                      return! receive() }
            receive())
        let configuration=NativeProcessFodexFunV304.configuration (Some owner) (dict [])
        let binding=NativeProcessBindingV304.bind node configuration (Some native)
        let rootPid=TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let childPid=TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let shell="C:/Program Files/PowerShell/7/pwsh.exe"
        let encoded s=Convert.ToBase64String(Encoding.Unicode.GetBytes(s:string))
        let script=sprintf "$p=Start-Process -FilePath '%s' -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand','%s') -WindowStyle Hidden -PassThru; [Console]::WriteLine('CHILD:'+$p.Id); Start-Sleep -Seconds 120" shell (encoded "Start-Sleep -Seconds 120")
        let io:ProcLocalIo={Input="";OnEvent=fun e ->
            match e with
            | ProcLocalIoEvent.Started(_,pid) -> rootPid.TrySetResult pid |> ignore
            | ProcLocalIoEvent.Stdout(_,line) when line.StartsWith "CHILD:" -> childPid.TrySetResult(Int32.Parse(line.Substring 6)) |> ignore
            | _ -> ()}
        let state=binding.Start(node,shell,__SOURCE_DIRECTORY__,["-NoProfile";"-NonInteractive";"-EncodedCommand";encoded script],Some io) |> required
        use root=Process.GetProcessById(wait rootPid.Task)
        use child=Process.GetProcessById(wait childPid.Task)
        root.SafeHandle |> ignore
        child.SafeHandle |> ignore
        try
            if deadActor then node.Stop native
            owner.CloseAsync() |> waitUnit
            Expect.isTrue (root.HasExited && child.HasExited) "success follows actual root and descendant exit"
            binding.Stop state |> required
        finally
            for resource in [root;child] do
                if not resource.HasExited then resource.Kill(true);resource.WaitForExit(5000) |> ignore))
    testCase "quotation creates independent owners and failed cleanup remains retryable" <| fun () -> withDatabase(fun _ -> withNode(fun node ->
        let create=evaluated<unit,NativeOwnershipV304> "3.0.4" node "NativeProcess.CreateOwnership"
        let a=create() |> required
        let b=create() |> required
        let mutable attempts=0
        let mutable other=0
        a.Register(Guid.NewGuid(),fun () -> task {
            attempts<-attempts+1
            if attempts=1 then failwith "physical cleanup not confirmed" })
        b.Register(Guid.NewGuid(),fun () -> task { other<-other+1 })
        Expect.throws(fun () -> a.CloseAsync() |> waitUnit) "error must be observable"
        Expect.equal other 0 "another wrapper is untouched"
        a.CloseAsync() |> waitUnit
        a.CloseAsync() |> waitUnit
        Expect.equal attempts 2 "only unconfirmed resource is retried"
        Expect.throws(fun () -> a.Register(Guid.NewGuid(),fun () -> Task.CompletedTask)) "closing owner cannot accept a new native"
        b.CloseAsync() |> waitUnit
        Expect.equal other 1 "separate owner still closes normally"))
    testCase "force waits for an in flight Start and permanently fences late launches" <| fun () ->
        use entered=new ManualResetEventSlim(false)
        use release=new ManualResetEventSlim(false)
        let mutable cleanups=0
        let original:NativeProcessBindingV303=
            { Start=fun _ -> entered.Set();release.Wait();Ok(Unchecked.defaultof<_>)
              Stop=fun _ -> Ok()
              StopPending=fun () -> Interlocked.Increment(&cleanups) |> ignore;Ok()
              Release=ignore }
        let binding,close=NativeProcessBindingV304.fromBinding original
        let start=Task.Run(fun () -> binding.Start(Unchecked.defaultof<_>))
        Expect.isTrue (entered.Wait 5000) "start is in flight"
        let stop=Task.Run(fun () -> close())
        try
            Expect.isFalse (stop.Wait 50) "cannot declare cleanup before in flight start finishes"
        finally release.Set()
        start |> wait |> required |> ignore
        stop |> wait |> required
        Expect.equal cleanups 1 "late process included in cleanup"
        Expect.isError (binding.Start(Unchecked.defaultof<_>)) "closed incarnation never launches again"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "1116 resource ownership tests failed"
#endif
