#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module NativeOwnershipLeaseFodexFunV302Tests
#endif

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

// These are ownership/registry tests, NOT a claim of Windows Job or live native acceptance.
// Every operation evaluates the registered quotation before Contract FeasibleFun.invoke.
let fixture action = withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "framework builtin declarations initialize before direct registry lookup"
    let scope:NativeOwnerScopeV302=
        {Root=Path.Combine("E:/deletable/fodex/native-owner-tests",Guid.NewGuid().ToString("N"))
         WrapperId=Guid.NewGuid();ParticipantNumber=2L;Purpose="main"}
    let command:NativeOwnerCommandV302={Executable=Environment.ProcessPath;WorkingDirectory=Environment.CurrentDirectory;Arguments=["--model";"test"]}
    let claim=evaluated<NativeOwnerClaimInputV302,NativeOwnerLeaseV302> "3.0.2" node NativeOwnershipLeaseFodexFunV302.targetClaim
    let complete=evaluated<NativeOwnerCompleteInputV302,unit> "3.0.2" node NativeOwnershipLeaseFodexFunV302.targetComplete
    action scope command claim complete)

let stale (lease:NativeOwnerLeaseV302) =
    let path=lease.Path
    let receipt={lease.Receipt with HostPid=Int32.MaxValue;HostBirthUtc=DateTime.MinValue}
    (lease :> IDisposable).Dispose()
    File.WriteAllText(path,JsonSerializer.Serialize receipt,UTF8Encoding(false,true))
    path,receipt

[<Tests>]
let tests=testSequenced <| testList "Native ownership lease V302" [
    testCase "canonical argv hash preserves argument boundaries and hides values" <| fun () -> fixture(fun scope command claim _ ->
        let first={command with Arguments=["sample-sensitive-value";"b c"]}
        use lease=claim(scope,first,(fun _ -> Ok())) |> required
        let text=File.ReadAllText(lease.Path,UTF8Encoding(false,true))
        Expect.isFalse (text.Contains "sample-sensitive-value") "argv content must not appear in a receipt"
        Expect.notEqual lease.Receipt.CommandHash (NativeOwnershipLeaseFodexFunV302.commandHash {command with Arguments=["sample-sensitive-value b";"c"]}) "array boundaries are part of command identity")
    testCase "exclusive lease and live host both prohibit another incarnation" <| fun () -> fixture(fun scope command claim _ ->
        let lease=claim(scope,command,(fun _ -> failtest "fresh scope must not reconcile")) |> required
        let before=File.ReadAllBytes lease.Path
        Expect.isError (claim(scope,command,(fun _ -> failtest "must not clean a live owner"))) "simultaneous writer blocked"
        (lease :> IDisposable).Dispose()
        Expect.isError (claim(scope,command,(fun _ -> failtest "released lock does not imply dead host"))) "disposed lease is not a completed resource"
        Expect.equal (File.ReadAllBytes lease.Path) before "old receipt is retained")
    testCase "stale owner must reconcile before a new run replaces its receipt" <| fun () -> fixture(fun scope command claim complete ->
        let old=claim(scope,command,(fun _ -> Ok())) |> required
        let _,previous=stale old
        let mutable reconciled=0
        let reconcile receipt =
            Expect.equal receipt previous "reconcile the exact previous authority"
            reconciled<-reconciled+1
            Ok()
        use current=claim(scope,command,reconcile) |> required
        Expect.equal reconciled 1 "cleanup acknowledgement precedes replacement"
        Expect.notEqual current.Receipt.RunId previous.RunId "new incarnation has its own run identity"
        complete(current,(fun _ -> Ok())) |> required)
    testCase "failed stale cleanup preserves original bytes and allows an explicit retry" <| fun () -> fixture(fun scope command claim complete ->
        let old=claim(scope,command,(fun _ -> Ok())) |> required
        let path,_=stale old
        let before=File.ReadAllBytes path
        Expect.isError (claim(scope,command,(fun _ -> Error(InvalidOperationException "fixture-job-still-live")))) "cannot overwrite failed cleanup"
        Expect.equal (File.ReadAllBytes path) before "no prepared replacement on failure"
        use retry=claim(scope,command,(fun _ -> Ok())) |> required
        complete(retry,(fun _ -> Ok())) |> required)
    testCase "a receipt from another scope cannot authorize cleanup" <| fun () -> fixture(fun scope command claim _ ->
        let old=claim(scope,command,(fun _ -> Ok())) |> required
        let path,receipt=stale old
        let foreign={receipt with WrapperId=Guid.NewGuid()}
        File.WriteAllText(path,JsonSerializer.Serialize foreign,UTF8Encoding(false,true))
        Expect.isError (claim(scope,command,(fun _ -> failtest "foreign identity must fail before cleanup"))) "scope includes wrapper and N")
    testCase "failed zero-process verification cannot mark a lease closed" <| fun () -> fixture(fun scope command claim complete ->
        use lease=claim(scope,command,(fun _ -> Ok())) |> required
        let before=File.ReadAllBytes lease.Path
        Expect.isError (complete(lease,(fun _ -> Error(InvalidOperationException "fixture-child-still-running")))) "Close ACK is insufficient"
        Expect.equal (File.ReadAllBytes lease.Path) before "verification failure preserves pending ownership"
        complete(lease,(fun _ -> Ok())) |> required
        Expect.equal lease.Receipt.Status "closed" "only actual authority confirmation permits closed")
    testCase "confirmed closed receipt permits actor restart in the same host" <| fun () -> fixture(fun scope command claim complete ->
        let old=claim(scope,command,(fun _ -> Ok())) |> required
        let run=old.Receipt.RunId
        complete(old,(fun _ -> Ok())) |> required
        (old :> IDisposable).Dispose()
        use next=claim(scope,{command with Arguments=["--model";"next"]},(fun _ -> failtest "closed resource needs no stale kill")) |> required
        Expect.notEqual next.Receipt.RunId run "new actor incarnation, same logical owner"
        complete(next,(fun _ -> Ok())) |> required)
    testCase "verification cannot overwrite a receipt whose run changed during the callback" <| fun () -> fixture(fun scope command claim complete ->
        use lease=claim(scope,command,(fun _ -> Ok())) |> required
        let changed={lease.Receipt with RunId=Guid.NewGuid()}
        let verify _=
            File.WriteAllText(lease.Path,JsonSerializer.Serialize changed,UTF8Encoding(false,true))
            Ok()
        Expect.isError (complete(lease,verify)) "fence again after the resource query"
        let retained=NativeOwnerReceiptStorageV302.read lease.Path
        Expect.equal retained changed "never write a stale completed receipt over another run")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeOwnershipLeaseFodexFunV302Tests failed"
#endif
