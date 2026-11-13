#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ReActDataStoreV402Tests
#endif
open System
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ParticipantRestartTestSupport

let invoke<'I,'O> target (input:'I) =
    FodexFunctions.initialize()
    let f=FodexFun<'I,Task<Result<'O,exn>>,exn>(VersionRegistry<FeasibleFun<'I,Task<Result<'O,exn>>,exn>>.BaseCD,target).[System.Version(4,0,2)] |> Option.get
    f.f.Value |> ignore
    FeasibleFun.invoke input f |> required
let run name action=testCase name <| fun () -> withDatabase(fun cs -> action cs |> fun (work:Task<unit>) -> work.WaitAsync(TimeSpan.FromSeconds 45.).GetAwaiter().GetResult())

[<Tests>]
let tests=testSequenced <| testList "Restart atomic SQL V402" [
    run "reserved native with no previous SID can activate exactly once" <| fun cs -> task {
        let! init=ReActDataStoreV402.initialize cs
        init |> required
        let empty=topology |> Array.map(fun (s,t,event) ->
            match event with
            | ParticipantMetadataChanged(id,p) when p.Number=2L -> s,t,ParticipantMetadataChanged(id,{p with SessionId=None})
            | _ -> s,t,event)
        let! initial=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,empty))
        initial |> required |> ignore
        let phases=restart |> Array.map(fun (s,t,event) ->
            match event with
            | ParticipantRestartChanged(id,cause,ParticipantRestartPhase.Activated(tid,g,_,current,a)) -> s,t,ParticipantRestartChanged(id,cause,ParticipantRestartPhase.Activated(tid,g,None,current,a))
            | _ -> s,t,event)
        let! activated=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,4L,phases))
        Expect.equal (activated |> required) 5 "first native binding created atomically"
        let! binding=ReActDataStoreV402.readBinding cs native
        Expect.equal (binding |> required) (Some(newIdentity,1L)) "fresh provider owns its observed SID"
        let! retry=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,4L,phases))
        Expect.equal (retry |> required) 0 "exact journal retry inserts no new events"
    }
    run "activation updates event binding args atomically and supports exact retry" <| fun cs -> task {
        let! init=ReActDataStoreV402.initialize cs
        init |> required
        let! start=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,topology))
        start |> required |> ignore
        let! prepare=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,4L,restart[..3]))
        prepare |> required |> ignore
        let! old=ReActDataStoreV402.readBinding cs native
        Expect.equal (old |> required) (Some(oldIdentity,0L)) "candidate not yet bound"
        let! activate=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,8L,[|restart[4]|]))
        Expect.equal (activate |> required) 1 "commit"
        let! binding=ReActDataStoreV402.readBinding cs native
        Expect.equal (binding |> required) (Some(newIdentity,1L)) "provider/SID/generation switched"
        let! args=ParticipantConfigurationFodexFunV403.load(cs,session,native)
        Expect.stringContains (args |> required |> Option.get) "--native-adapter agy" "effective args changed in same transaction"
        let! retry=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,8L,[|restart[4]|]))
        Expect.equal (retry |> required) 0 "idempotent retry does not advance generation"
    }
    run "projection failure rolls back event append and keeps old binding and settings" <| fun cs -> task {
        let! init=ReActDataStoreV402.initialize cs
        init |> required
        let! seed=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,Array.append topology restart[..3]))
        seed |> required |> ignore
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-"ALTER TABLE dbo.FodexParticipantConfiguration ADD CONSTRAINT CK_RejectRestart CHECK(Arguments NOT LIKE '%--native-adapter agy%')"
        command.ExecuteNonQuery() |> ignore
        let! failed=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,8L,[|restart[4]|]))
        Expect.isError failed "transaction failed at settings projection"
        let! binding=ReActDataStoreV402.readBinding cs native
        Expect.equal (binding |> required) (Some(oldIdentity,0L)) "no partial binding"
        let! history=invoke<_,ReActDataStream<string> array> ReActDataStoreV300.targetCollect (cs,session)
        Expect.equal (history |> required |> Array.length) 8 "Activated rolled back too"
    }
    run "keyset pages descend under H and exclude topology and process IO" <| fun cs -> task {
        let! init=ReActDataStoreV402.initialize cs
        init |> required
        let rows=[|yield! topology; yield ReActEndpoint.Human,endpoint,HumanInit(5L,HumanPrompt "older"); yield endpoint,root,StdErr(6L,5L,"excluded"); yield endpoint,root,AgentWork(7L,5L,Message "answer"); yield ReActEndpoint.Human,endpoint,HumanInit(8L,HumanPrompt "after H")|]
        let! saved=invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,rows))
        saved |> required |> ignore
        let! page=invoke<_,ReActDataStream<string> array> ReActDataStoreV402.targetContextPage (cs,(session,7L,Int64.MaxValue,1))
        Expect.equal (page |> required |> Array.map ReActJsonV301.streamIdOf) [|7L|] "first bounded page"
        let! next=invoke<_,ReActDataStream<string> array> ReActDataStoreV402.targetContextPage (cs,(session,7L,7L,10))
        Expect.equal (next |> required |> Array.map ReActJsonV301.streamIdOf) [|5L|] "no duplicate, IO excluded"
        let! crossingIo=invoke<_,ReActDataStream<string> array> ReActDataStoreV402.targetContextPage (cs,(session,6L,7L,1))
        Expect.equal (crossingIo |> required |> Array.map ReActJsonV301.streamIdOf) [|5L|] "an IO-only raw page cannot conceal older context"
    }
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActDataStoreV402Tests failed"
#endif
