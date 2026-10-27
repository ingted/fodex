namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module NativeRuntime =
    let route<'TypedCmdToWrapper>
        (runtime: NativeRuntimeKind)
        : FeasibleT<
            ReActData<'TypedCmdToWrapper>,
            ReActDataStream<'TypedCmdToWrapper> [],
            exn
          > =
        match runtime with
        | CodexExec -> CodexExecRuntime.route<'TypedCmdToWrapper>
        | CodexAppServer -> CodexAppServerRuntime.route<'TypedCmdToWrapper>
