#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
// Debug DLL 被使用中的 FSI 鎖住時，可用 --define:RELEASE 驗證同一份 quotation。
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Fodex.Supervisor.dll"
#else
module ParticipantListingFodexFunV300Tests
#endif

open System
open System.Net
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let sid=Guid.Parse "0ebcc931-4e91-4799-a261-a739e1a49127"
let graph address =
    let session=sid,address
    let root=1L,session
    let native=2L,session
    let info n kind sessionId =
        {Number=n;Name=(if n=1L then "wrapper" else "main");Kind=kind;SessionId=sessionId
         ActorAddress=Some(sprintf "akka://test/user/participant-%d" n)
         Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UnixEpoch}
    let p=ReActEndpoint.Participant
    let view=ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),p root,[|
        p root,p root,ParticipantMetadataChanged(1L,info 1L ReActParticipantKind.Wrapper (Some(WrapperSessionId session)))
        p native,p native,ParticipantMetadataChanged(2L,info 2L ReActParticipantKind.Native (Some(NativeCodexSessionId "codex-session")))
        p root,p native,ParticipantRelationChanged(3L,ReActRelation.Owns,true)
        p root,p native,ParticipantRelationChanged(4L,ReActRelation.Main,true) |]) |> required
    view,root,native

let render action = withNode(fun node ->
    ParticipantListingFodexFunV300.register node
    action(evaluated<ParticipantListingInputV300,string> node ParticipantListingFodexFunV300.target))

[<Tests>]
let tests=testList "M017 participant listing quotation" [
    testCase "stopped participants stay visible; retired history nodes do not" <| fun () -> render(fun format ->
        let view,root,native=graph None
        let endpoint=ReActEndpoint.Participant
        let info=match view.GetLabel(endpoint native) with ReActNode.Participant value -> value | _ -> failwith "missing node"
        let stopped=ReActTopologyFodexFunV300.applyBatch(view,endpoint root,[|
            endpoint native,endpoint native,ParticipantMetadataChanged(5L,{info with Status=ReActParticipantStatus.Stopped}) |]) |> required
        let settings=Ok({Model="a";Effort="high";Fast=false;Memory=true},false)
        Expect.stringContains (format(stopped,[|native,settings;root,settings|]) |> required) "status : Stopped" "startable identity remains visible"
        let retired=ReActTopologyFodexFunV300.applyBatch(stopped,endpoint root,[|
            endpoint native,endpoint native,ParticipantMetadataChanged(6L,{info with Status=ReActParticipantStatus.Retired;ActorAddress=None})
            endpoint root,endpoint native,ParticipantRelationChanged(7L,ReActRelation.Main,false)
            endpoint root,endpoint native,ParticipantRelationChanged(8L,ReActRelation.Owns,false) |]) |> required
        let text=format(retired,[|native,settings;root,settings|]) |> required
        Expect.isFalse (text.Contains "codex-session") "retired SID hidden even if stale settings supplied"
        Expect.stringContains text "name : wrapper" "remaining identities intact")

    testCase "session display unwraps options and preserves parent identity" <| fun () -> render(fun format ->
        let view,root,native=graph None
        let settings=Ok({Model="a";Effort="high";Fast=false;Memory=true},false)
        let text=format(view,[|native,settings;root,settings|]) |> required
        Expect.stringContains text (sprintf "WSID (%O, )" sid) "empty optional IP stays in the second slot"
        Expect.stringContains text "CSID (codex-session)" "native keeps its actual Codex sid"
        Expect.stringContains text (sprintf "parent : WSID (%O, )" sid) "parent uses the same readable format"
        Expect.isFalse (text.Contains "Some" || text.Contains "WrapperSessionId") "no F# debug wrappers"
        Expect.stringContains text "effort : high" "explicit effort field"
        Expect.stringContains text "ifFast : false" "explicit fast field"
        Expect.stringContains text "memory : true" "default on is visible"
        let blocks=text.Split([|"\n\n"|],StringSplitOptions.None)
        Expect.equal blocks.Length 2 "one block per participant"
        Expect.isTrue (blocks[0].TrimStart().StartsWith "N : 1\n" && blocks[1].TrimStart().StartsWith "N : 2\n") "sort by durable participant number"
        Expect.stringContains blocks[0] "name : wrapper" "wrapper gets its own block"
        Expect.stringContains blocks[1] "name : main" "native gets its own block"
        Expect.stringContains blocks[1] "actor : akka://test/user/participant-2" "full actor address remains readable"
        Expect.isFalse (text.Contains " | ") "no wide table")

    testCase "IP and current settings come from supplied observations" <| fun () -> render(fun format ->
        let view,root,native=graph(Some IPAddress.Loopback)
        let settings=Ok({Model="changed-model";Effort="low";Fast=true;Memory=false},true)
        let text=format(view,[|root,settings;native,settings|]) |> required
        Expect.stringContains text (sprintf "WSID (%O, 127.0.0.1)" sid) "optional IP rendered without Some"
        Expect.stringContains text "model : changed-model" "not stale construction arguments"
        Expect.stringContains text "effort : low" "current effort"
        Expect.stringContains text "ifFast : true" "current fast setting"
        Expect.stringContains text "memory : false" "current opt out is visible"
        Expect.stringContains text "active : true" "next-turn settings distinguished from activity")

    testCase "one unavailable reader leaves other rows and the graph intact" <| fun () -> render(fun format ->
        let view,root,native=graph None
        let before=view.GetEdges()
        let error=Error(TimeoutException("settings read timed out") :> exn)
        let text=format(view,[|root,Ok({Model="a";Effort="high";Fast=false;Memory=true},false);native,error|]) |> required
        Expect.stringContains text "effort : ?" "unknown effort is explicit"
        Expect.stringContains text "ifFast : ?" "unknown fast setting is explicit"
        Expect.stringContains text "memory : ?" "read failure does not pretend memory is off"
        Expect.stringContains text "settings read timed out" "failure remains attributable"
        Expect.stringContains text "name : wrapper" "other participant retained"
        let lines=text.Split '\n' |> Array.filter(fun line -> line.Length>0)
        let columns=lines |> Array.map(fun line -> line.IndexOf(" : ",StringComparison.Ordinal)) |> Array.distinct
        Expect.equal columns [|"settingsError".Length|] "every block shares the same key width, including long error keys"
        for line in lines do
            let key=line.Substring(0,columns[0])
            Expect.equal (key.TrimEnd()) key "padding belongs to the left of the key"
            let value=line.Substring(columns[0]+3)
            Expect.equal (value.TrimStart()) value "value begins directly after the separator"
        Expect.equal (view.GetEdges()) before "formatting does not mutate graph")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ParticipantListingFodexFunV300Tests failed"
#endif
