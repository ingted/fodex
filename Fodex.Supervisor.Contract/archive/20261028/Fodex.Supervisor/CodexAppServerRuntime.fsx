namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module CodexAppServerRuntime =
    // app-server adapter 必須維持非阻塞 reader、serialized writer、pending request map，
    // 並將 thread/turn/item correlation 轉為 ReActDataStream。
    let route<'TypedCmdToWrapper>:
        FeasibleT<
            ReActData<'TypedCmdToWrapper>,
            ReActDataStream<'TypedCmdToWrapper> [],
            exn
        > =
        fun _ -> failwith "CodexAppServerRuntime.route is not implemented"
