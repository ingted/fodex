namespace Fodex.Supervisor

open System
open Fodex.Supervisor.Contracts.CoreMod

module WrapperOutputV101 =
    // Display only: SQL and the retained queue keep the original text/DU unchanged.
    // Width counts Unicode scalars, not terminal display cells; 0 disables soft wrapping.
    let createSink width (write: string -> unit) (onEnd: unit -> unit) =
        let mutable phase = ""
        let mutable column = 0
        let mutable previousCR = false
        let mutable responseStreamed = false

        let newLine () =
            write Environment.NewLine
            column <- 0

        let finishLine () =
            if column > 0 then newLine ()
            previousCR <- false

        let print phaseName (text: string) =
            if not (String.IsNullOrEmpty text) then
                if phase <> phaseName then
                    finishLine ()
                    write ("[" + phaseName + "]" + Environment.NewLine)
                    phase <- phaseName
                for rune in text.EnumerateRunes() do
                    match rune.Value with
                    | 13 -> newLine (); previousCR <- true
                    | 10 ->
                        if not previousCR then newLine ()
                        previousCR <- false
                    | _ ->
                        previousCR <- false
                        if width > 0 && column >= width then newLine ()
                        write (rune.ToString())
                        column <- column + 1

        fun (stream: ReActDataStream<string>) ->
            match stream with
            | AgentCot(_, _, data) | AgentWork(_, _, data) ->
                let isResponse = match stream with AgentWork _ -> true | _ -> false
                for item in data do
                    match item with
                    | MainWrapperToHuman text ->
                        if isResponse && not (String.IsNullOrEmpty text) then responseStreamed <- true
                        print (if isResponse then "Response" else "COT") text
                    | _ -> ()
            | AgentEnd(_, _, _, MainWrapperToHuman text) ->
                if not responseStreamed then print "Response" text
                finishLine ()
                newLine ()
                phase <- ""
                responseStreamed <- false
                onEnd ()
            // Match M002: raw adapter/tool JSON is available through showHis, not normal output.
            | StdOut _ -> ()
            | StdErr(_, _, text) -> print "Error" text; finishLine ()
            | _ -> ()
