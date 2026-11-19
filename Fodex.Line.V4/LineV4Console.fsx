#if !INTERACTIVE
module LineV4Console
#endif
open System

/// Keep the physical editor on its real stdout in normal mode. Only JSON mode
/// redirects incidental console output to diagnostics. Restore the caller's
/// terminal state on every exit, including failed startup.
let withSession json action =
    let originalOutput=Console.Out
    let mutable originalCursor:bool option=None
    if not Console.IsInputRedirected && not Console.IsOutputRedirected then
        try
            originalCursor<-Some Console.CursorVisible
            Console.CursorVisible<-true
        with
        | :? PlatformNotSupportedException | :? IO.IOException -> ()
    try
        if json then Console.SetOut Console.Error
        action()
    finally
        Console.SetOut originalOutput
        originalCursor |> Option.iter(fun visible ->
            try Console.CursorVisible<-visible
            with :? PlatformNotSupportedException | :? IO.IOException -> ())
