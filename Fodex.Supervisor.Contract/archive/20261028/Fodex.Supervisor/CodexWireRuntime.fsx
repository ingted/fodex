namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts

type CodexWireJson = string

module CodexWireRuntime =
    // Wire DTO／JSON schema 與 ReActData domain conversion 必須保持分離。
    let deserialize<'T>: FeasibleT<CodexWireJson, 'T, exn> =
        fun _ -> failwith "CodexWireRuntime.deserialize is not implemented"

    let serialize<'T>: FeasibleT<'T, CodexWireJson, exn> =
        fun _ -> failwith "CodexWireRuntime.serialize is not implemented"
