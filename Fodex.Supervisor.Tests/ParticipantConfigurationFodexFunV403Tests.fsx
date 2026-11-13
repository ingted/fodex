#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantConfigurationFodexFunV403Tests
#endif
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testSequenced <| testList "Participant configuration V403 async quotations" [
    testCase "quoted async settings are isolated by full participant identity" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        ReActDataStoreV402.initialize cs |> wait |> required
        let save=evaluated<string*ReActParticipantSessionId*ReActParticipant*string,Task<Result<unit,exn>>> "4.0.3" node ParticipantConfigurationFodexFunV402.targetSave
        let load=evaluated<string*ReActParticipantSessionId*ReActParticipant,Task<Result<string option,exn>>> "4.0.3" node ParticipantConfigurationFodexFunV402.targetLoad
        save(cs,session,native,"--native-adapter codex --effort high") |> required |> wait |> required
        let actual=load(cs,session,native) |> required |> wait |> required
        Expect.equal actual (Some "--native-adapter codex --effort high") "committed settings"
        let different=2L,(System.Guid.NewGuid(),None)
        Expect.equal (load(cs,session,different) |> required |> wait |> required) None "same N does not impersonate another identity"))
    testCase "legacy verification uses persisted provider and changes neither SID nor generation" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        ReActDataStoreV402.initialize cs |> wait |> required
        ReActDataStoreV402.persist cs (session,0L,topology) |> wait |> required |> ignore
        ParticipantConfigurationFodexFunV403.save(cs,session,native,"--native-adapter exec --model gpt-6.1-sol --effort high") |> wait |> required
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-"UPDATE dbo.FodexNativeSession SET AgentType=NULL"
        command.ExecuteNonQuery() |> ignore
        let verify=evaluated<string*ReActParticipantSessionId*ReActParticipant*LoggerUtil.UniContext,Task<Result<(NativeSessionIdentity*int64) option,exn>>> "4.0.3" node ParticipantConfigurationFodexFunV403.targetVerifyLegacy
        Expect.equal (verify(cs,session,native,ctx) |> required |> wait |> required) (Some(oldIdentity,0L)) "verified migration retains binding"
        Expect.equal (ReActDataStoreV402.readBinding cs native |> wait |> required) (Some(oldIdentity,0L)) "projection repaired, no replacement"))
    testCase "legacy binding without evidence fails and remains unmigrated" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        ReActDataStoreV402.initialize cs |> wait |> required
        ReActDataStoreV402.persist cs (session,0L,topology) |> wait |> required |> ignore
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-"UPDATE dbo.FodexNativeSession SET AgentType=NULL"
        command.ExecuteNonQuery() |> ignore
        let verify=evaluated<string*ReActParticipantSessionId*ReActParticipant*LoggerUtil.UniContext,Task<Result<(NativeSessionIdentity*int64) option,exn>>> "4.0.3" node ParticipantConfigurationFodexFunV403.targetVerifyLegacy
        Expect.isError (verify(cs,session,native,ctx) |> required |> wait) "no GUID/name/default guessing"
        command.CommandText<-"SELECT COUNT(*) FROM dbo.FodexNativeSession WHERE AgentType IS NULL AND SessionGeneration=0"
        Expect.equal (unbox<int>(command.ExecuteScalar())) 1 "no mutation on ambiguity"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantConfigurationFodexFunV403Tests failed"
#endif
