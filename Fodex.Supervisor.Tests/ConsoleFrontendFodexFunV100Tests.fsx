#if INTERACTIVE
#load "LineV4CoreTestSupport.fsx"
#else
module ConsoleFrontendFodexFunV100Tests
#endif
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open LineV4CoreTestSupport

[<Tests>]
let tests = testSequenced <| testList "Console frontend V100" [
    testCase "multiline submission uses callback; exit disposes display without closing borrowed node" <| fun () -> withNode(fun node ->
        let options=coreSettings "E:/deletable/fodex/v4-tests/frontend"
        use input=new StringReader("first\nsecond\n;;\n/exit\n;;\n")
        use output=new StringWriter()
        use diagnostics=new StringWriter()
        let calls=ResizeArray<string>()
        let mutable attached=0
        let mutable detached=0
        let attach post=attached<-attached+1;post true "observed";{new IDisposable with member _.Dispose()=detached<-detached+1}
        let submit (text,_) = calls.Add text;Task.FromResult(Ok(Some "accepted"))
        let run=evaluated<ConsoleFrontendInputV100,Task<Result<int,exn>>> "1.0.0" node "Console.RunFrontend"
        Expect.equal (run(node,ctx,options,input,output,diagnostics,submit,attach,CancellationToken.None) |> required |> wait |> required) 0 "exit"
        Expect.equal (calls.ToArray()) [|"first\nsecond"|] "one multiline operation"
        Expect.equal (attached,detached) (1,1) "one display lifecycle"
        Expect.isFalse node.WhenTerminated.IsCompleted "caller still owns node")
    testCase "EOF and cancelled reads release display" <| fun () -> withNode(fun node ->
        let options=coreSettings "E:/deletable/fodex/v4-tests/frontend"
        for cancelled in [false;true] do
            use input=new StringReader("")
            use output=new StringWriter()
            use diagnostics=new StringWriter()
            use cts=new CancellationTokenSource()
            if cancelled then cts.Cancel()
            let mutable detached=false
            let attach _={new IDisposable with member _.Dispose()=detached<-true}
            let submit _=failwith "EOF cannot submit"
            let run=evaluated<ConsoleFrontendInputV100,Task<Result<int,exn>>> "1.0.0" node "Console.RunFrontend"
            Expect.equal (run(node,ctx,options,input,output,diagnostics,submit,attach,cts.Token) |> required |> wait |> required) (if cancelled then 130 else 0) "exit code"
            Expect.isTrue detached "subscription released")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleFrontendFodexFunV100Tests failed"
#endif
