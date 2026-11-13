#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Collections.Immutable
open Akka.Actor
open Akka.Actor.Setup
open Akka.Serialization
open MBrace.FsPickler
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 與 V300 的控制 wire 相同，只替換 node bootstrap 的 pickler cache 生命週期。
/// 不增加 ActorSystem；每個 service/console 仍只建立自己的唯一 node。
module RemoteControlFodexFunV301 =
    /// Settings.InjectTopLevelFallback 會重建 Serialization extension。把 factory 放在
    /// Akka 官方 SerializationSetup，讓 SQL／dispatcher 設定套用後仍重建相同 wire map；
    /// 不靠 caller 再手動 register，也不重新建立 PicklerCache。
    let topologyDetails (system:ExtendedActorSystem) =
        let codec=FsPickler.CreateBinarySerializer(picklerResolver=ReActJsonV200.picklerCache.Value)
        let serializer=
            {new Serializer(system) with
                override _.Identifier=300
                override _.IncludeManifest=false
                override _.ToBinary value=codec.Pickle value
                override _.FromBinary(bytes,_)=codec.UnPickle<obj> bytes}
        let types=[typeof<ReActTopologyCallV300>;typeof<NativeOutputCallV300>;typeof<Result<string,exn>>;typeof<Result<ReActParticipant,exn>>;typeof<Result<ReActDataStream<string>,exn>>]
        SerializerDetails.Create("fodex-topology-v300",serializer,ImmutableHashSet.CreateRange types)

    let createNode name ip port =
        let serializers=SerializationSetup.Create(Func<ExtendedActorSystem,ImmutableHashSet<SerializerDetails>>(fun system ->
            ImmutableHashSet.CreateRange [RemoteFodexFunV210.serializerDetails system;topologyDetails system]))
        let config=ActorLogging.config.WithFallback(RemoteFodexFunV210.config ip port)
        let bootstrap=BootstrapSetup.Create().WithConfig config
        ActorSystem.Create(name,ActorSystemSetup.Create([|bootstrap :> Setup;serializers :> Setup|]))
