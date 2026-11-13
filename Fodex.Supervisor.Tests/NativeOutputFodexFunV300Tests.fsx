#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Akka.FSharp.dll"
#r "Akka.Remote.dll"
// FSI 沒有實作專案的 ASP.NET shared-framework dependency graph；DotNetty 需要 logging assembly。
// 交給 NuGet resolve 相依，不寫死 lib/TFM 路徑，也不另做 DLL 複製。
#r "nuget: Microsoft.Extensions.Logging, 10.0.14"
#r "Fodex.Supervisor.dll"
#else
module NativeOutputFodexFunV300Tests
#endif

open System
open System.Text
open System.Text.Json
open System.Threading.Channels
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod
open LoggerUtil

let participant=2L,(Guid.NewGuid(),None)
let take (channel:Channel<'T>) = channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
let ready actor = NativeOutputFodexFunV300.ask<bool> actor OutputReady |> ignore
let flush actor = NativeOutputFodexFunV300.ask<bool> actor FlushOutput |> ignore
let relay node target =
    let create=evaluated<FodexNode*UniContext*ReActParticipant*IActorRef,IActorRef> node NativeOutputFodexFunV300.targetRelay
    create(node,ctx,participant,target) |> required

/// probe 只接收新 I/O DU；沒有 ReActLoop、DB 或模型。Register 的 ActorRef 由 Sender 取得。
let probe node show trace =
    let received=Channel.CreateUnbounded<NativeOutputBatchV300>()
    let actor=spawn node ("io-probe-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
        let rec receive () = actor {
            let! message=mailbox.Receive()
            match message with
            | :? NativeOutputCallV300 as (RegisterOutput _) -> mailbox.Sender().Tell(OutputMode(show,trace),mailbox.Self)
            | :? NativeOutputCallV300 as (OutputBatch batch) -> received.Writer.TryWrite batch |> ignore
            | :? NativeOutputCallV300 as OutputReady -> mailbox.Sender().Tell(true,mailbox.Self)
            | _ -> ()
            return! receive () }
        receive ()
    actor,received

[<Tests>]
let tests=testList "M018 native output quotations" [
    testCase "split format and trace quotations preserve original Unicode and distinguish raw source" <| fun () -> withNode(fun node ->
        NativeOutputFodexFunV300.register node
        let split=evaluated<string,string array> node NativeOutputFodexFunV300.targetSplit
        let render=evaluated<NativeOutputBatchV300,string> node NativeOutputFodexFunV300.targetFormat
        let encode=evaluated<NativeOutputBatchV300,string> node NativeOutputFodexFunV300.targetTrace
        let original=String.replicate 4095 "x"+"😀\r\n中文\n"+String.replicate 9000 "字"
        let chunks=split original |> required
        Expect.equal (String.concat "" chunks) original "no trim, newline change, or surrogate loss"
        for chunk in chunks do
            Expect.isLessThanOrEqual (Encoding.UTF8.GetByteCount chunk) (12*1024) "bounded wire chunk"
            Expect.isFalse (Char.IsHighSurrogate chunk[chunk.Length-1]) "no trailing half pair"
        let batch=NativeOutputBatch(participant,Guid.NewGuid(),3L,AppServerNotification,original,true)
        Expect.stringContains (render batch |> required) "[native app-server N=2" "not an AgentWork heading"
        use json=JsonDocument.Parse(encode batch |> required)
        Expect.equal (json.RootElement.GetProperty("text").GetString()) original "queryable JSON retains raw content"
        Expect.equal (json.RootElement.GetProperty("sequence").GetInt64()) 3L "sequence retained")

    testCase "relay quotation batches Unicode with bounded payload and run-local sequence" <| fun () -> withNode(fun node ->
        NativeOutputFodexFunV300.register node
        let target,received=probe node true false
        let source=relay node target
        let run=Guid.NewGuid()
        let original=String.replicate 20000 "字😀\n"
        source.Tell(RawOutput(run,ProcessStdout,original))
        flush source
        let result=StringBuilder()
        let mutable sequence=0L
        while result.Length<original.Length do
            let (NativeOutputBatch(p,id,n,kind,text,trace))=take received
            sequence<-sequence+1L
            Expect.equal (p,id,n,kind,trace) (participant,run,sequence,ProcessStdout,false) "same origin and ordered sequence"
            Expect.isLessThanOrEqual (Encoding.UTF8.GetByteCount text) (32*1024) "bounded batch"
            result.Append text |> ignore
        Expect.equal (result.ToString()) original "full payload preserved"
        source.Tell(RawOutput(Guid.NewGuid(),ProcessStdout,"next"));flush source
        let (NativeOutputBatch(_,_,n,_,text,_))=take received
        Expect.equal (n,text) (1L,"next") "new process has a new sequence scope")

    testCase "trace transition flushes prior mode and does not backfill hidden output" <| fun () -> withNode(fun node ->
        NativeOutputFodexFunV300.register node
        let target,received=probe node true false
        let source=relay node target
        let run=Guid.NewGuid()
        source.Tell(RawOutput(run,ProcessStdout,"visible only"))
        source.Tell(OutputMode(false,true))
        source.Tell(RawOutput(run,ProcessStdout,"trace only"))
        source.Tell(OutputMode(false,false))
        source.Tell(RawOutput(run,ProcessStdout,"discard"))
        flush source
        let (NativeOutputBatch(_,_,_,_,first,firstTrace))=take received
        let (NativeOutputBatch(_,_,_,_,second,secondTrace))=take received
        Expect.equal (first,firstTrace) ("visible only",false) "not retroactively traced"
        Expect.equal (second,secondTrace) ("trace only",true) "pending traced text survives off"
        ready target
        Expect.isFalse (received.Reader.TryPeek() |> fst) "disabled producer sends no raw payload")

    testCase "hub and printor quotations keep hidden participants separate from shown output" <| fun () -> withNode(fun node ->
        NativeOutputFodexFunV300.register node
        let createHub=evaluated<FodexNode*UniContext,IActorRef> node NativeOutputFodexFunV300.targetHub
        let createPrinter=evaluated<FodexNode*UniContext*IActorRef*(string->unit),IActorRef> node NativeOutputFodexFunV300.targetPrinter
        let target=createHub(node,ctx) |> required
        let rendered=Channel.CreateUnbounded<string>()
        let printer=createPrinter(node,ctx,target,fun text -> rendered.Writer.TryWrite text |> ignore) |> required
        NativeOutputFodexFunV300.ask<bool> target (SetOutputVisible [|participant|]) |> ignore
        let source=relay node target
        source.Tell(RawOutput(Guid.NewGuid(),ProcessStdout,"hello\n"));flush source
        Expect.stringContains (take rendered) "hello\n" "separate printor receives stdout"
        NativeOutputFodexFunV300.ask<bool> target (SetOutputVisible [||]) |> ignore
        ready source
        source.Tell(RawOutput(Guid.NewGuid(),ProcessStdout,"hidden"));flush source;ready target;ready printer
        Expect.isFalse (rendered.Reader.TryPeek() |> fst) "hidden raw output never reaches printor"
        let status=NativeOutputFodexFunV300.ask<string> target (SetOutputTrace None)
        Expect.stringContains status "trace=false" "trace defaults off")

    testCase "raw batch crosses real Akka TCP as data and flushes small writes without polling" <| fun () ->
        use owner=RemoteControlFodexFunV301.createNode ("output-owner-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        use client=RemoteControlFodexFunV301.createNode ("output-client-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        let originalSerialization=owner.Serialization
        Expect.equal (originalSerialization.FindSerializerForType(typeof<NativeOutputCallV300>)).Identifier 300 "wire registered before config injection"
        QuotationTestScope.attach owner |> ignore
        QuotationTestScope.attach client |> ignore
        Expect.isFalse (Object.ReferenceEquals(originalSerialization,owner.Serialization)) "settings rebuild the serialization extension"
        Expect.equal (owner.Serialization.FindSerializerForType(typeof<NativeOutputCallV300>)).Identifier 300 "bootstrap setup restores the wire automatically"
        for node in [owner;client] do
            let errors=spawn node "output-error-evidence" (fun mailbox ->
                let rec loop()=actor {
                    let! message:obj=mailbox.Receive()
                    match message with
                    | :? Akka.Event.Error as error -> Console.Error.WriteLine("OUTPUT_ACTOR_ERROR "+string error.Cause)
                    | :? Akka.Event.Warning as warning -> Console.Error.WriteLine("OUTPUT_ACTOR_WARN "+string warning.Message)
                    | _ -> ()
                    return! loop() }
                loop())
            node.EventStream.Subscribe(errors,typeof<Akka.Event.Error>) |> ignore
            node.EventStream.Subscribe(errors,typeof<Akka.Event.Warning>) |> ignore
        NativeOutputFodexFunV300.register owner
        NativeOutputFodexFunV300.register client
        let target=evaluated<FodexNode*UniContext,IActorRef> owner NativeOutputFodexFunV300.targetHub (owner,ctx) |> required
        Console.Error.WriteLine("OUTPUT_STAGE=hub "+string target.Path)
        ready target
        Console.Error.WriteLine("OUTPUT_STAGE=hub-ready")
        let rendered=Channel.CreateUnbounded<string>()
        evaluated<FodexNode*UniContext*IActorRef*(string->unit),IActorRef> owner NativeOutputFodexFunV300.targetPrinter
            (owner,ctx,target,fun text -> rendered.Writer.TryWrite text |> ignore) |> required |> ignore
        Console.Error.WriteLine("OUTPUT_STAGE=printer-ready")
        NativeOutputFodexFunV300.ask<bool> target (SetOutputVisible [|participant|]) |> ignore
        let address=Akka.Serialization.Serialization.SerializedActorPath target
        let remote=client.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        Console.Error.WriteLine("OUTPUT_STAGE=remote-resolved address="+address+" ref="+string remote.Path)
        let outgoing=client.Serialization.FindSerializerForType typeof<NativeOutputCallV300>
        let incoming=owner.Serialization.FindSerializerForType typeof<NativeOutputCallV300>
        let decoded=incoming.FromBinary(outgoing.ToBinary(box OutputReady),typeof<NativeOutputCallV300>)
        Console.Error.WriteLine(sprintf "OUTPUT_CODEC send=%d receive=%d value=%A equal=%b" outgoing.Identifier incoming.Identifier decoded (Object.Equals(decoded,box OutputReady)))
        ready remote
        Console.Error.WriteLine("OUTPUT_STAGE=remote-ready")
        let source=relay client remote
        Console.Error.WriteLine("OUTPUT_STAGE=relay-ready")
        source.Tell(RawOutput(Guid.NewGuid(),AppServerNotification,"remote notification\n"))
        Expect.stringContains (take rendered) "remote notification" "timer flush and wire delivery"
        client.Terminate().GetAwaiter().GetResult() |> ignore
        owner.Terminate().GetAwaiter().GetResult() |> ignore
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "NativeOutputFodexFunV300Tests failed"
#endif
