namespace Fodex.Supervisor

open System
open System.IO
open Akka.Actor
open Akka.Proc.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

module ProcSupervisorV100 =
    type StartState = {
        Node: FodexNode
        Spec: ProcStartSpec
        Registry: IActorRef option
        Supervisor: IActorRef option
    }

    let validate : ProcessSopStep<StartState> = fun state ->
        if not (File.Exists state.Spec.fileName) then Error(FileNotFoundException("Codex executable not found", state.Spec.fileName))
        elif state.Spec.workingDir |> Option.exists (Directory.Exists >> not) then Error(DirectoryNotFoundException("Working directory not found"))
        else Ok state

    let bind : ProcessSopStep<StartState> = fun state ->
        try
            let registry = state.Node.ActorOf(Props.Create(fun () -> ProcRegistryActor()))
            let supervisor = state.Node.ActorOf(Props.Create(fun () -> ProcNodeActor(ProcSupervisorSettings.Default, registry, None)))
            Ok { state with Registry = Some registry; Supervisor = Some supervisor }
        with ex -> Error ex

    let start : ProcessSopStep<StartState> = fun state ->
        try
            let supervisor = state.Supervisor.Value
            let snapshot = supervisor.Ask<ProcSnapshot>(({ procId = state.Spec.procId; spec = state.Spec }: StartProc), TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
            match snapshot.pid with
            | Some _ -> Ok state
            | None -> Error(InvalidOperationException(sprintf "Proc Supervisor failed: %A" snapshot.lastError))
        with ex -> Error ex

    let startSop : ProcessSop<StartState> = [| validate; bind; start |]

    let stop state =
        try
            match state.Supervisor with
            | Some supervisor ->
                supervisor.Ask<ProcSnapshot>(({ procId = state.Spec.procId; force = true }: StopProc), TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
                state.Node.Stop supervisor
            | None -> ()
            state.Registry |> Option.iter state.Node.Stop
            Ok ()
        with ex -> Error ex

    let launch node executable directory endpoint =
        let initial = {
            Node = node
            Spec = { procId = "fodex-" + Guid.NewGuid().ToString("N"); fileName = executable
                     args = [ "app-server"; "--listen"; endpoint ]; workingDir = Some directory
                     role = Some "codex"; probeMessage = None; probeCron = None; probeIntervalMs = None }
            Registry = None; Supervisor = None }
        let mutable latest = initial
        let result = (Ok initial, startSop) ||> Array.fold (fun current step ->
            current |> Result.bind (fun state -> step state |> Result.map (fun next -> latest <- next; next)))
        match result with
        | Ok state -> Ok state
        | Error ex -> stop latest |> ignore; Error ex
