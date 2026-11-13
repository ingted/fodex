#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ParticipantRestartTestSupport
#endif

open System
open Fodex.Supervisor.Contracts.CoreMod

// Ordinary fixture data. Production projection/codec are always invoked through evaluated quotations.
let session:ReActParticipantSessionId=Guid.Parse "fc260000-0000-0000-0000-000000000001",None
let owner:ReActParticipant=1L,session
let native:ReActParticipant=2L,session
let root=ReActEndpoint.Participant owner
let endpoint=ReActEndpoint.Participant native
let transition=Guid.Parse "fc260000-0000-0000-0000-000000000002"
let artifact=Guid.Parse "fc260000-0000-0000-0000-000000000003"
let bootstrap=Guid.Parse "fc260000-0000-0000-0000-000000000004"
let oldIdentity:NativeSessionIdentity="codex","old-native"
let newIdentity:NativeSessionIdentity="agy","new-native"
let info n name kind sid:ReActParticipantInfo=
    {Number=n;Name=name;Kind=kind;SessionId=sid;ActorAddress=None;Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UnixEpoch}
let topology:ReActDataStream<string> array=[|
    root,root,ParticipantMetadataChanged(1L,info 1L "root" ReActParticipantKind.Wrapper (Some(WrapperSessionId session)))
    endpoint,endpoint,ParticipantMetadataChanged(2L,info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId oldIdentity)))
    root,endpoint,ParticipantRelationChanged(3L,ReActRelation.Owns,true)
    root,endpoint,ParticipantRelationChanged(4L,ReActRelation.Main,true)|]
let phase id value:ReActDataStream<string>=endpoint,endpoint,ParticipantRestartChanged(id,4L,value)
let restart:ReActDataStream<string> array=[|
    phase 5L (ParticipantRestartPhase.Requested(transition,0L,"agy","--native-adapter agy --model gemini-3.8-flash-high --effort high"))
    phase 6L (ParticipantRestartPhase.HandoverPrepared(transition,4L,artifact,String.replicate 64 "a"))
    phase 7L (ParticipantRestartPhase.CandidateStarted(transition,"agy",None,bootstrap))
    phase 8L (ParticipantRestartPhase.CandidateStarted(transition,"agy",Some "new-native",bootstrap))
    phase 9L (ParticipantRestartPhase.Activated(transition,1L,Some oldIdentity,newIdentity,artifact))|]
