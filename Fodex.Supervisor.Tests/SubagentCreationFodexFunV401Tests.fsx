#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module SubagentCreationFodexFunV401Tests
#endif

open System
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

// 先以現行 4.0.0 重現 type 被忽略；新版完成後只換受測版本，保留行為 assertions。
let subjectVersion = "4.0.1"

/// 保留真實 root wrapper、registry、ReActLoop、SQL；只替代外部模型與 child 資源建立。
/// 觀察實際送到 child factory 的 mode/type，不能只以 parser 結果冒充 routing 已正確。
let withWrapper action = withDatabase(fun cs -> withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtin declarations"
    let session = Guid.NewGuid(),None
    let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.0" node FodexFunRegistrationV301.targetLoop
                    (node,cs,session,ctx,ignore) |> required
    let selected = TaskCompletionSource<SubagentModeV300>(TaskCreationOptions.RunContinuationsAsynchronously)
    let factories = {RootSession=session;Models=models;Transport=Mcp
                     Native=fun (p,args) -> Ok((native node loop p args).Agent)
                     Subagents=fun (parent,count,mode) -> task {
                         selected.TrySetResult mode |> ignore
                         let kind = match mode with
                                    | SubagentModeV300.Wrapper | SubagentModeV300.WrapperWithType _ -> ReActParticipantKind.Wrapper
                                    | _ -> ReActParticipantKind.RawExec
                         let children = Array.init count (fun index ->
                             let who = sprintf "sub-%d" (index+1)
                             let child = loop.PinParticipantReservation(Some "3.0.0")(parent,who,kind,None,None) |> required
                             who,child)
                         return Ok(Mcp,children) }
                     ManageParticipants=fun _ -> Task.FromResult(Ok "managed")
                     RawControl=fun _ -> Task.FromResult(Ok "controlled")
                     RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "fixture"))
                     StopOrders=fun _ -> Task.FromResult(Ok ())}
    let wrapper = evaluated<RootCreationV301,IAgent<string,obj>> subjectVersion node FodexFunRegistrationV301.targetRoot
                        (node,loop,arguments,ctx,factories) |> required
    wrapper.Start() |> required |> ignore
    try action wrapper selected.Task
    finally wrapper.Stop() |> required |> ignore))

[<Tests>]
let tests = testList "Subagent creation V401" [
    testCase "wrapper Request preserves explicit agy for raw children" <| fun () ->
        withWrapper <| fun wrapper selected ->
            wrapper.Request "/subagent(2, raw, agy) review" |> wait |> required |> ignore
            Expect.equal (selected |> wait) (SubagentModeV300.RawWithType SubagentTypeV300.Agy) "engine reaches child creation"

    testCase "wrapper Request preserves explicit agy for in-process wrapper children" <| fun () ->
        withWrapper <| fun wrapper selected ->
            wrapper.Request "/subagent(3, wrapper, agy) review" |> wait |> required |> ignore
            Expect.equal (selected |> wait) (SubagentModeV300.WrapperWithType SubagentTypeV300.Agy) "wrapper mode and native engine are separate choices"

    testCase "omitting the type leaves the configured child default in control" <| fun () ->
        withWrapper <| fun wrapper selected ->
            wrapper.Request "/subagent(1, raw)" |> wait |> required |> ignore
            Expect.equal (selected |> wait) SubagentModeV300.Raw "no explicit type is invented from the main provider"

    testCase "raw factory chooses an agy actor without starting an external process" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtin declarations"
            let session = Guid.NewGuid(),None
            let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.0" node FodexFunRegistrationV301.targetLoop
                            (node,cs,session,ctx,ignore) |> required
            let create = evaluated<RawCreationV400,IActorRef> subjectVersion node FodexFunRegistrationV300.targetRaw
            let actor = create(node,loop,cs,ctx,models,((3L,session),"--native-adapter agy --model test --effort high")) |> required
            Expect.stringContains actor.Path.Name "agy" "actual resource is the agy implementation; no Start/RunTurn was sent"))

    testCase "unsupported raw adapter returns Error instead of creating Codex" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtin declarations"
            let session = Guid.NewGuid(),None
            let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.0" node FodexFunRegistrationV301.targetLoop
                            (node,cs,session,ctx,ignore) |> required
            let create = evaluated<RawCreationV400,IActorRef> subjectVersion node FodexFunRegistrationV300.targetRaw
            Expect.isError (create(node,loop,cs,ctx,models,((3L,session),"--native-adapter unknown --model test --effort high")))
                "unsupported adapters do not silently become Codex"))
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "SubagentCreationFodexFunV401Tests failed: %d" testExitCode
#endif
