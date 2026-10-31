#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV210.fsx"
#else
module HubScopeFodexFunV210Tests
#endif

open System
open System.Net
open System.Net.Http
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 先 eval 正式 registry quotation，再 invoke；不以測試自己重寫的 URL 演算法代替產品。
let withFunctions action =
    withNode <| fun node ->
        HubScopeFodexFunV210.register node
        let scoped = SubagentFodexFunV206.bind<Uri * ReActParticipant,Uri> node HubScopeFodexFunV210.targetScope (Some "2.0.10")
        scoped.f.Value |> ignore
        action node scoped

[<Tests>]
let tests = testList "Owner scoped hub FodexFun V210" [
    testCase "Logical owner has a stable URL; another wrapper never shares its hub" <| fun () ->
        withFunctions <| fun _ scoped ->
            let root = Uri "http://127.0.0.1:18813"
            let first = FeasibleFun.invoke (root,wrapper) scoped |> expectOk
            Expect.equal (FeasibleFun.invoke (first,wrapper) scoped |> expectOk) first "idempotent when already scoped"
            Expect.equal (FeasibleFun.invoke (root,wrapper) scoped |> expectOk) first "reconnect retains owner identity"
            let second = FeasibleFun.invoke (root,(1L,(Guid.NewGuid(),None))) scoped |> expectOk
            Expect.notEqual first second "a new main cannot inherit old sub sessions/results"
            Expect.isError (FeasibleFun.invoke (first,(99L,session)) scoped) "cannot reinterpret another owner's URL"
    testCase "Inner input determines the owner; explicit missing versions do not fall back" <| fun () ->
        withFunctions <| fun node scoped ->
            let root = Uri "http://127.0.0.1:18813"
            let actual = root,wrapper
            Expect.equal (scoped.f.Value (root,(99L,session)) actual |> expectOk)
                         (FeasibleFun.invoke actual scoped |> expectOk) "FeasibleFun double-input semantics"
            Expect.throws (fun () -> SubagentFodexFunV206.bind<Uri * ReActParticipant,Uri> node HubScopeFodexFunV210.targetScope (Some "2.0.99") |> ignore) "no unrequested latest version"
    testCase "Scope validation happens before network IO and does not accept query or remote host" <| fun () ->
        withFunctions <| fun _ scoped ->
            for address in ["https://example.invalid";"http://127.0.0.1:1/?owner=other";"http://127.0.0.1:1/unrelated";"http://127.0.0.1:1/#other"] do
                Expect.isError (FeasibleFun.invoke (Uri address,wrapper) scoped) "invalid scope address"
    testCase "HTTP error includes status and server reason, instead of a bare 400" <| fun () ->
        use response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        response.Content <- new StringContent("Subagent initialization rejected: pending orders must be received")
        let error =
            try HubScopeFodexFunV210.readResponse response |> ignore; None
            with ex -> Some ex
        let error = error |> Option.defaultWith (fun () -> failtest "Expected HTTP failure")
        Expect.stringContains error.Message "400" "status retained"
        Expect.stringContains error.Message "pending orders must be received" "root cause retained"
    testCase "Successful HTTP body is preserved and error detail is bounded" <| fun () ->
        use ok = new HttpResponseMessage(HttpStatusCode.OK)
        ok.Content <- new StringContent("{\"owner\":\"unchanged\"}")
        Expect.equal (HubScopeFodexFunV210.readResponse ok) "{\"owner\":\"unchanged\"}" "no reserialization"
        use bad = new HttpResponseMessage(HttpStatusCode.Conflict)
        bad.Content <- new StringContent(String('x',20000))
        try HubScopeFodexFunV210.readResponse bad |> ignore; failtest "Expected conflict"
        with :? HttpRequestException as ex -> Expect.isLessThan ex.Message.Length 4300 "bounded diagnostic detail"
    testCase "Initialization quotations validate inputs without starting a model or a host" <| fun () ->
        withFunctions <| fun node _ ->
            let initialize = SubagentFodexFunV206.bind<SubagentStartupInputV206,string array> node SubagentFodexFunV206.targetInitialize (Some "2.0.10")
            let raw = SubagentFodexFunV206.bind<NativeSubagentStartV207,string array> node NativeSubagentFodexFunV207.targetInitialize (Some "2.0.10")
            let collect = SubagentFodexFunV206.bind<NativeSubagentReadV207,NativeOrderEvidenceV205 array> node NativeSubagentFodexFunV207.targetCollect (Some "2.0.10")
            initialize.f.Value |> ignore
            raw.f.Value |> ignore
            collect.f.Value |> ignore
            Expect.isError (FeasibleFun.invoke (Uri "http://127.0.0.1:1",wrapper,0) initialize) "invalid N"
            Expect.isError (FeasibleFun.invoke (Uri "http://127.0.0.1:1",wrapper,2,0L) raw) "uncommitted cause"
            Expect.isError (FeasibleFun.invoke (Uri "http://127.0.0.1:1",wrapper,0L,[|"sub-1"|]) collect) "invalid read scope"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "HubScopeFodexFunV210Tests: %d" code
#endif
