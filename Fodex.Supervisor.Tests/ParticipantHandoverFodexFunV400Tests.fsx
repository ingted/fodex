#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantHandoverFodexFunV400Tests
#endif
open System
open System.IO
open System.Text
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

let graphWith rows=ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),root,Array.append topology rows) |> required
let withSelect action=withNode(fun node ->
    FodexFunctions.initialize()
    let select=evaluated<WrappedReActDataFlow<string>*ReActParticipant*int64,ReActDataStream<string> array> "4.0.0" node ParticipantHandoverFodexFunV400.targetSelect
    action select)
[<Tests>]
let tests=testSequenced <| testList "Participant handover V400" [
    testCase "60KiB takes newest whole Unicode events then returns chronological order" <| fun () -> withSelect(fun select ->
        let rows=[|for i in 5L..30L -> endpoint,root,AgentWork(i,4L,Message(String.replicate 1500 (sprintf "界%d" i)))|]
        let selected=select(graphWith rows,native,29L) |> required
        let ids=selected |> Array.map ReActJsonV301.streamIdOf
        Expect.equal ids (Array.sort ids) "prepend order"
        Expect.equal (Array.last ids) 29L "fixed H"
        Expect.isLessThan selected.Length rows.Length "bounded tail"
        let bytes=selected |> Array.sumBy(fun (_,_,e) -> match e with AgentWork(_,_,Message text) -> Encoding.UTF8.GetByteCount text | _ -> 0)
        Expect.isGreaterThanOrEqual bytes (60*1024-2000) "approximate budget incl header")
    testCase "process IO excluded before budget and completed replaces fragments and duplicate AgentEnd body" <| fun () -> withSelect(fun select ->
        let rows=[|endpoint,root,StdOut(5L,4L,String.replicate 80000 "x"); endpoint,root,StdErr(6L,4L,"stderr-only"); endpoint,root,AgentWork(7L,4L,MessageDelta("a","fr")); endpoint,root,AgentWork(8L,4L,MessageCompleted("a","fresh")); endpoint,root,AgentEnd(9L,4L,Message "fresh"); endpoint,root,AgentWork(10L,4L,MessageDelta("b","partial"))|]
        let selected=select(graphWith rows,native,10L) |> required
        Expect.equal (selected |> Array.map ReActJsonV301.streamIdOf) [|8L;9L;10L|] "only authoritative body plus terminal marker and partial"
        let text=ParticipantHandoverFodexFunV400.render(graphWith rows,native,"agy",10L,selected) |> required
        Expect.isFalse (text.Contains "stderr-only") "no process IO"
        Expect.equal (text.Split("fresh").Length-1) 1 "terminal body not duplicated"
        Expect.stringContains text "partial" "unfinished output retained")
    testCase "a single oversized event is retained intact" <| fun () -> withSelect(fun select ->
        let content=String.replicate 70000 "a"
        let rows=[|endpoint,root,AgentWork(5L,4L,Message content)|]
        Expect.equal (select(graphWith rows,native,5L) |> required) rows "no truncation or omission")
    testCase "raw leaf sees delivered work and its own results without unrelated sibling history" <| fun () -> withSelect(fun select ->
        let child=3L,session
        let sibling=4L,session
        let sub=ReActEndpoint.Participant child
        let other=ReActEndpoint.Participant sibling
        let rows=[|sub,sub,ParticipantMetadataChanged(5L,info 3L "sub-1" ReActParticipantKind.RawExec None)
                   root,sub,ParticipantRelationChanged(6L,ReActRelation.Owns,true)
                   other,other,ParticipantMetadataChanged(7L,info 4L "sub-2" ReActParticipantKind.RawExec None)
                   root,other,ParticipantRelationChanged(8L,ReActRelation.Owns,true)
                   root,sub,AgentWork(9L,4L,ParticipantInput(StartTurn,"own instruction",Some owner))
                   sub,root,AgentWork(10L,9L,Message "own result")
                   root,other,AgentWork(11L,4L,ParticipantInput(StartTurn,"sibling secret work",Some owner))|]
        let selected=select(graphWith rows,child,11L) |> required
        Expect.equal (selected |> Array.map ReActJsonV301.streamIdOf) [|9L;10L|] "context scope is delivery/ownership based")
    testCase "immutable artifact uses no BOM LF hash and ready token only in footer" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let prepare=evaluated<string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64,ParticipantHandoverArtifact> "4.0.0" node ParticipantHandoverFodexFunV400.targetPrepare
        let directory=Path.Combine(Path.GetTempPath(),"FodexHandover_"+Guid.NewGuid().ToString("N"))
        try
            let graph=graphWith [|ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt "latest question")|]
            let artifact=prepare(directory,graph,native,Some oldIdentity,"agy",transition,5L) |> required
            let bytes=File.ReadAllBytes artifact.Path
            Expect.isFalse (bytes.Length>=3 && bytes[..2]=[|0xEFuy;0xBBuy;0xBFuy|]) "UTF8 no BOM"
            let text=UTF8Encoding(false,true).GetString bytes
            Expect.isFalse (text.Contains "\r") "LF"
            Expect.stringEnds text (artifact.ReadyToken+"\n") "footer token"
            let prompt=ParticipantHandoverFodexFunV400.bootstrapPrompt artifact
            Expect.isFalse (prompt.Contains artifact.ReadyToken) "read file to obtain token"
            Expect.isLessThan prompt.Length 2000 "no giant command line"
            Expect.isError (prepare(directory,graph,native,Some oldIdentity,"agy",transition,5L)) "cannot overwrite artifact"
        finally
            // This fixture owns the exact unique directory it created.
            let resolved=Path.GetFullPath directory
            let expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),Path.GetFileName directory))
            if resolved<>expected || not(Path.GetFileName(resolved).StartsWith("FodexHandover_")) then failwith "Refuse fixture cleanup outside its unique temp directory"
            if Directory.Exists resolved then Directory.Delete(resolved,true))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantHandoverFodexFunV400Tests failed"
#endif
