#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantHandoverFodexFunV401Tests
#endif
open System
open System.IO
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

let withFiles action =
    let directory=Path.Combine(Path.GetTempPath(),"FodexHandoverAssets_"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    try action directory
    finally
        let resolved=Path.GetFullPath directory
        if Path.GetDirectoryName(resolved)<>Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
           not(Path.GetFileName(resolved).StartsWith("FodexHandoverAssets_",StringComparison.Ordinal)) then
            failwith "Refuse cleanup outside fixture directory"
        Directory.Delete(resolved,true)

let graphWith rows = ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),root,Array.append topology rows) |> required
let prepare node = evaluated<string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64,ParticipantHandoverArtifact> "4.0.1" node ParticipantHandoverFodexFunV401.targetPrepare

[<Tests>]
let tests=testSequenced <| testList "Participant handover V401 assets and identities" [
    testCase "artifact copies selected assets and keeps original graph immutable" <| fun () -> withNode(fun node -> withFiles(fun directory ->
        FodexFunctions.initialize()
        let source=Path.Combine(directory,"source.jpg")
        let pixels=[|0xffuy;0xd8uy;0xffuy;1uy;2uy|]
        File.WriteAllBytes(source,pixels)
        let prompt=JsonSerializer.Serialize {|payloadKind="image";localImagePaths=[|source|];userPayload="pixel question"|}
        let input=ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt prompt)
        let graph=graphWith [|input|]
        let artifact=prepare node (directory,graph,native,Some oldIdentity,"agy",transition,5L) |> required
        let asset=Directory.GetFiles(Path.Combine(Path.GetDirectoryName artifact.Path,"assets")) |> Array.exactlyOne
        Expect.sequenceEqual (File.ReadAllBytes asset) pixels "actual attachment bytes were copied"
        File.Delete source
        Expect.isTrue (File.Exists asset) "artifact owns its attachment independently of route cleanup"
        let text=File.ReadAllText artifact.Path
        Expect.stringContains text "N=1; who=root" "parent identity is preserved"
        Expect.stringContains text "N=2; who=main" "selected participant identity is preserved"
        Expect.stringContains text "parent=N=1" "ownership remains interpretable"
        Expect.stringContains text (JsonSerializer.Serialize(asset).Trim('"')) "prompt uses the artifact-owned path"
        Expect.sequenceEqual (graph.GetEdges() |> Array.filter(fun (_,_,event) -> match event with HumanInit _ -> true | _ -> false)) [|input|] "no graph message was rewritten"
        use manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName artifact.Path,"manifest.json")))
        Expect.equal (manifest.RootElement.GetProperty("root").GetGuid()) (fst session) "manifest root identity"
        Expect.equal (manifest.RootElement.GetProperty("assets").GetArrayLength()) 1 "copied attachment has durable metadata"))

    testCase "missing required local image refuses artifact with stream identity" <| fun () -> withNode(fun node -> withFiles(fun directory ->
        FodexFunctions.initialize()
        let prompt=JsonSerializer.Serialize {|localImagePaths=[|Path.Combine(directory,"gone.jpg")|]|}
        let graph=graphWith [|ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt prompt)|]
        match prepare node (directory,graph,native,Some oldIdentity,"agy",transition,5L) with
        | Ok _ -> failtest "missing pixels must not be silently handed over as text"
        | Error error ->
            Expect.stringContains error.Message "ContextAttachmentUnavailable" "explicit cause"
            Expect.stringContains error.Message "StreamId=5" "operator can identify the original event"
            Expect.isFalse (Directory.Exists(Path.Combine(directory,"participants"))) "preflight failed before artifact creation"))

    testCase "discussion mentioning stderr or filenames is ordinary business text" <| fun () -> withNode(fun node -> withFiles(fun directory ->
        FodexFunctions.initialize()
        let body="請檢查 stderr；foo.jpg 是說明文字，不是附件欄位。"
        let graph=graphWith [|ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt body)|]
        let artifact=prepare node (directory,graph,native,Some oldIdentity,"agy",transition,5L) |> required
        Expect.stringContains (File.ReadAllText artifact.Path) body "typed exclusion must not remove human discussion"))

    testCase "Prepared recovery reuses immutable artifact and detects later tampering" <| fun () -> withNode(fun node -> withFiles(fun directory ->
        FodexFunctions.initialize()
        let graph=graphWith [|ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt "historical input")|]
        let artifact=prepare node (directory,graph,native,Some oldIdentity,"agy",transition,5L) |> required
        let phases=[|phase 6L (ParticipantRestartPhase.Requested(transition,0L,"agy","args"));phase 7L (ParticipantRestartPhase.HandoverPrepared(transition,5L,artifact.ArtifactId,artifact.Sha256))|]
        let recovered=ReActTopologyFodexFunV301.applyBatch(graph,root,phases) |> required
        let resumed=prepare node (directory,recovered,native,Some oldIdentity,"agy",transition,5L) |> required
        Expect.equal resumed artifact "same H/artifact/token, no regeneration"
        File.AppendAllText(artifact.Path,"changed",System.Text.UTF8Encoding(false))
        Expect.isError (prepare node (directory,recovered,native,Some oldIdentity,"agy",transition,5L)) "corruption never triggers a fresh candidate"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantHandoverFodexFunV401Tests failed"
#endif
