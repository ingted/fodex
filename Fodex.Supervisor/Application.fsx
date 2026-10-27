namespace Fodex.Supervisor

open System

module Application =
    let run (_options: SupervisorOptions) : Result<int, exn> =
        Error (
            NotImplementedException(
                "Fodex.Supervisor runtime has not been implemented"
            ) :> exn
        )
