#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantHandoverFodexFunV402Tests
#endif
open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

let picture=[|0xFFuy;0xD8uy;0xFFuy;0xE0uy;1uy;2uy;3uy|]
let fixture action=withNode(fun node ->
    FodexFunctions.initialize()
    let directory=Path.Combine("E:/deletable/fodex/1114-handover-402",Guid.NewGuid().ToString("N"))
    let incoming=Path.Combine(directory,"line-images")
    let artifacts=Path.Combine(directory,"service")
    Directory.CreateDirectory incoming |> ignore
    let source=Path.Combine(incoming,"download.jpg")
    File.WriteAllBytes(source,picture)
    let request:SharedAssetImportV100={sourcePath=source;sha256=Convert.ToHexString(SHA256.HashData picture).ToLowerInvariant();bytes=int64 picture.Length;mediaType="image/jpeg"}
    let import=evaluated<SharedAssetInputV100,Task<Result<SharedAssetReferenceV100,exn>>> "1.0.0" node SharedAssetFodexFunV100.target
    let reference=import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,CancellationToken.None) |> required |> wait |> required
    action node source artifacts reference)
let graphWith text=ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),root,Array.append topology [|
    ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt text)
    endpoint,ReActEndpoint.Human,StdOut(6L,5L,"RAW_STDIO_MUST_NOT_ENTER_HANDOVER")|]) |> required
let prepare node=evaluated<string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64,ParticipantHandoverArtifact> "4.0.2" node ParticipantHandoverFodexFunV402.targetPrepare
let validate node=evaluated<ReActDataStream<string> array,unit> "4.0.2" node ParticipantHandoverFodexFunV402.targetValidateAssets
let prompt source reference=JsonSerializer.Serialize {|localImagePaths=[|source|];fodexAssets=[|reference|];instruction="read the provided pixels"|}

[<Tests>]
let tests=testSequenced <| testList "Participant handover V402 durable assets" [
    testCase "source cleanup leaves durable reference available to all quoted handover operations" <| fun () -> fixture(fun node source artifacts reference ->
        let text=prompt source reference
        let graph=graphWith text
        File.Delete source
        validate node (graph.GetEdges()) |> required
        let select=evaluated<WrappedReActDataFlow<string>*ReActParticipant*int64,ReActDataStream<string> array> "4.0.2" node ParticipantHandoverFodexFunV402.targetSelect
        let rows=select(graph,native,6L) |> required
        let render=evaluated<WrappedReActDataFlow<string>*ReActParticipant*string*int64*ReActDataStream<string> array,string> "4.0.2" node ParticipantHandoverFodexFunV402.targetRender
        let markdown=render(graph,native,"agy",6L,rows) |> required
        Expect.stringContains markdown reference.assetId "durable identity in business history"
        Expect.isFalse (markdown.Contains "RAW_STDIO_MUST_NOT_ENTER_HANDOVER") "stdio exclusion preserved"
        let artifact=prepare node (artifacts,graph,native,Some oldIdentity,"agy",transition,6L) |> required
        let copied=Directory.GetFiles(Path.Combine(Path.GetDirectoryName artifact.Path,"assets")) |> Array.exactlyOne
        Expect.sequenceEqual (File.ReadAllBytes copied) picture "actual durable pixels copied after LINE deletes download"
        let preserved=graph.GetEdges() |> Array.pick(function _,_,HumanInit(5L,HumanPrompt value) -> Some value | _ -> None)
        Expect.equal preserved text "artifact rewriting does not mutate graph history")
    testCase "missing durable asset fails instead of silently falling back to a surviving download" <| fun () -> fixture(fun node source artifacts reference ->
        let graph=graphWith(prompt source reference)
        File.Delete reference.path
        Expect.isTrue (File.Exists source) "legacy download still exists"
        Expect.isError (validate node (graph.GetEdges())) "durable reference is authoritative"
        Expect.isError (prepare node (artifacts,graph,native,Some oldIdentity,"agy",transition,6L)) "failure before native bootstrap")
    testCase "tampered reference metadata or asset bytes fail integrity validation" <| fun () -> fixture(fun node source _ reference ->
        for changed in [{reference with bytes=reference.bytes+1L};{reference with assetId="sha256:"+String.replicate 64 "0"};{reference with mediaType="text/plain"}] do
            Expect.isError (validate node ((graphWith(prompt source changed)).GetEdges())) "reference must match accepted object"
        let damaged=Array.copy picture
        damaged[damaged.Length-1]<-99uy
        File.WriteAllBytes(reference.path,damaged)
        Expect.isError (validate node ((graphWith(prompt source reference)).GetEdges())) "modified bytes are never handed over")
    testCase "existing legacy image paths remain supported and missing paths still fail" <| fun () -> fixture(fun node source artifacts _ ->
        let text=JsonSerializer.Serialize {|localImagePaths=[|source|];instruction="legacy history"|}
        let graph=graphWith text
        let artifact=prepare node (artifacts,graph,native,Some oldIdentity,"codex",transition,6L) |> required
        let copied=Directory.GetFiles(Path.Combine(Path.GetDirectoryName artifact.Path,"assets")) |> Array.exactlyOne
        Expect.sequenceEqual (File.ReadAllBytes copied) picture "old history still readable"
        File.Delete source
        Expect.isError (validate node (graph.GetEdges())) "no silent loss of legacy images")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantHandoverFodexFunV402Tests failed"
#endif
