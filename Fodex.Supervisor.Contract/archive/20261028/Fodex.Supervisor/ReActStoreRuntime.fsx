namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module ReActStoreRuntime =
    let persist<'TypedCmdToWrapper>:
        FeasibleT<ReActDataStream<'TypedCmdToWrapper>, int, exn> =
        fun _ -> failwith "ReActStoreRuntime.persist is not implemented"

    let collect<'TypedCmdToWrapper>:
        FeasibleT<
            ReActParticipantSessionId,
            ReActDataStream<'TypedCmdToWrapper> [],
            exn
        > =
        fun _ -> failwith "ReActStoreRuntime.collect is not implemented"
