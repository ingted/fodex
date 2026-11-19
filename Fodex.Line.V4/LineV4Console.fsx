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
    let mutable originalCursorSize:int option=None
    if not Console.IsInputRedirected && not Console.IsOutputRedirected then
        try
            originalCursor<-Some Console.CursorVisible
            Console.CursorVisible<-true
            originalCursorSize<-Some Console.CursorSize
            Console.CursorSize<-15
        with
        | :? PlatformNotSupportedException | :? IO.IOException -> ()
    try
        if json then Console.SetOut Console.Error
        action()
    finally
        Console.SetOut originalOutput
        originalCursorSize |> Option.iter(fun size ->
            try Console.CursorSize<-size
            with :? PlatformNotSupportedException | :? IO.IOException -> ())
        originalCursor |> Option.iter(fun visible ->
            try Console.CursorVisible<-visible
            with :? PlatformNotSupportedException | :? IO.IOException -> ())
