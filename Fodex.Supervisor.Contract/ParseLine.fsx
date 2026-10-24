module PL
open LoggerUtil

type ParsingState = {
    mutable inQuotes        : bool
    mutable wasEscaped      : bool
    mutable fieldStarted    : bool
    mutable fieldNoQuoteNow : bool
    result : System.Collections.Generic.List<string>
}
    with 
        static member Identity () = {
            inQuotes        = false
            wasEscaped      = false
            fieldStarted    = true
            fieldNoQuoteNow = true
            result          = System.Collections.Generic.List<string>()
        }
        override this.ToString () =
            $"inQ:{this.inQuotes}, escaped:{this.wasEscaped}, fStart:{this.fieldStarted}, noQNow:{this.fieldNoQuoteNow}"


let parseLine (ctx:UniContext) (delimiters: char[]) (quoteChar: char option) (escapeCharOpt:char option) (ifMergeDelimiters: bool) (inputLine: string) : string[] =
    let logger = ctx.logger
    let escapeChar = if escapeCharOpt.IsNone then '\\' else escapeCharOpt.Value
    let s = ParsingState.Identity ()
    let currentField = new System.Text.StringBuilder()
    let seekQuote = new System.Text.StringBuilder()
    let append (c:char) = currentField.Append c |> ignore
    let appendStr (s:string) = currentField.Append s |> ignore
    let appendEmpty () = currentField.Append "" |> ignore
    let seek (c:char) = seekQuote.Append c |> ignore
    let seekNotEmpty () = seekQuote.ToString().Trim() <> ""
    let clear () = currentField.Clear() |> ignore    
    let ifSpaceDelimiter = Array.contains ' ' delimiters

    //let line = seq {for i in 0..(inputLine.Length - 1) do yield inputLine.[i] } |> Seq.toArray
     
    //let lineQ = new Queue<char>(line)

    //let next() = 
    //    if lineQ.Count = 0 then
    //        None
    //    else
    //        Some (lineQ.Dequeue())
    //------------------------------v.0

    let charSeq = seq {
        for c in inputLine do
            yield c
    }

    // 創建一個迭代器，以按需訪問序列中的元素
    let charEnumerator = charSeq.GetEnumerator()

    // 定義 next 函數來返回序列中的下一個元素，或在沒有更多元素時返回 None
    let next() =
        if charEnumerator.MoveNext() then
            Some(charEnumerator.Current)
        else
            None
    //------------------------------v.1
    
    let reset () =
        s.fieldStarted <- true
        s.inQuotes <- false
        s.fieldNoQuoteNow <- true
        s.wasEscaped <- false
        
    let add () = 
        let sqStr = 
            if s.fieldNoQuoteNow then
                let sqStr = seekQuote.ToString()
                //appendStr sqStr
                sqStr
            else
                ""
        seekQuote.Clear () |> ignore
        let curF = currentField.ToString()
        let cfStr = sqStr + curF
#if DEBUG
        logger.debugf "add: cfStr = '%s'+ '%s'" sqStr curF
        logger.debugf "add: '%s' result.Add -> currentField.clear -> reset" cfStr
#endif
        s.result.Add cfStr |> ignore
        currentField.Clear() |> ignore
        reset ()
    let emptyNotIgnored () =
        s.result.Add (seekQuote.ToString()) |> ignore
    let addEmpty () =
        s.result.Add "" |> ignore
        currentField.Clear() |> ignore
        reset ()

    //line |> Seq.iter (fun c -> lineQ.Enqueue c)
    
    let rec inQuote (co:char option) =
        if co.IsNone then
            logger.pErrorf "quote not finished"

        else
            let c = co.Value       
            match c, s.fieldStarted, s.wasEscaped with
            | _, _, false when c = escapeChar -> //escape
#if DEBUG
//#else
                logger.debugf "%O inQuote %c inQuoteEscape" s c
#endif
                s.wasEscaped <- true
                inQuoteEscape (next())
            | _, _, false when Some c = quoteChar ->
#if DEBUG
//#else
                logger.debugf "%O inQuote %c append -> add -> clear -> exitQuote" s c
#endif
                //append c
                add ()
                clear ()
                s.fieldNoQuoteNow <- true
                s.fieldStarted <- false
                exitQuote (next())  
            
            | _, _, _ when Array.exists ((=) c) delimiters ->
#if DEBUG
//#else
                logger.debugf "%O inQuote %c append -> inQuote" s c
#endif
                append c
                inQuote (next())
            | _, true, _ -> //quoted field begin
#if DEBUG
//#else
                logger.debugf "%O inQuote %c clear -> append -> inQuote" s c
#endif
                clear ()
                append c
                s.fieldStarted <- false
                inQuote (next())
            | _, _, _ ->
#if DEBUG
//#else
                logger.debugf "%O inQuote %c append -> inQuote" s c
#endif
                append c
                inQuote (next())
    and notInQuote (co:char option) =
        if co.IsNone then
            add ()
#if DEBUG
//#else
            logger.debugf  "notInQuote, co.IsNone"
#endif
        else
            let c = co.Value
            match c, s.fieldStarted, s.fieldNoQuoteNow with      
            | _, _, _ when c = escapeChar ->
#if DEBUG
//#else
                logger.debugf "%O notInQuote %c escape" s c
#endif
                s.fieldStarted <- false
                escape (next())
            | _, true, _ when Array.exists ((=) c) delimiters -> //field開頭立馬結束
#if DEBUG
//#else
                logger.debugf "%O notInQuote %c delimiters, fieldStarted: true" s c
#endif
                add ()
                //notInQuote (next())

                if ifMergeDelimiters && ifSpaceDelimiter && c = ' ' then
                    let rec skipSpaces (co: char option) =
                        match co with
                        | Some ' ' -> 
#if DEBUG
//#else
                            logger.debugf  "cur is ' ' and if next is ' ' then skip it"
#endif
                            skipSpaces (next())
                        | _ -> co
                    notInQuote (skipSpaces (next()))
                else
                    notInQuote (next())

            | _, false, _ when Array.exists ((=) c) delimiters -> //field結束
#if DEBUG
//#else
                logger.debugf "%O notInQuote %c delimiters, fieldStarted: false, %A" s c s.result
#endif
                add ()
                if ifMergeDelimiters && ifSpaceDelimiter && c = ' ' then
                    let rec skipSpaces (co: char option) =
                        match co with
                        | Some ' ' -> skipSpaces (next())
                        | _ -> co
                    notInQuote (skipSpaces (next()))
                else
                    notInQuote (next())
            | ' ', true, _ -> 
#if DEBUG
//#else
                logger.debugf "%O notInQuote %c empty character, %A" s c s.result
#endif
                seek ' '
                s.fieldStarted <- false
                notInQuote (next())
            | _, _, true when Some c = quoteChar -> 
                if seekNotEmpty () then
                    //printfn $"seekNotEmpty {inputLine}"
                    failwith "Invalid quote occurance"
                else
#if DEBUG
//#else
                    printfn "%O notInQuote %c quote character -> inQuote" s c
#endif
                    s.fieldStarted <- true
                    s.fieldNoQuoteNow <- false
                    s.inQuotes <- true
                    inQuote (next())
            | _, true, true ->
#if DEBUG
//#else
                logger.debugf "%O notInQuote %c -> seek -> notInQuote, fieldStarted: true" s c
#endif
                seek c
                s.fieldStarted <- false
                notInQuote (next())
            | _, false, true ->
#if DEBUG
//#else
                printfn "%O notInQuote %c -> seek -> notInQuote, fieldStarted: false" s c
#endif
                seek c
                notInQuote (next())
            | _, false, _ ->
                s.fieldStarted <- false
#if DEBUG
//#else
                printfn "%O notInQuote %c -> added -> notInQuote" s c
#endif
                add ()
                notInQuote (next())
    and escape (co:char option) =
        if co.IsNone then
            logger.debugf  "escape: escape character should paired with escaped character"
        else
            let c = co.Value
            match c, s.fieldStarted, s.fieldNoQuoteNow with
            | _, _, true when c = escapeChar ->
#if DEBUG
//#else
                logger.debugf "%O escape %c seek" s c
#endif
                s.wasEscaped <- false
                seek c
                notInQuote (next())
            | _, _, false when c = escapeChar ->
#if DEBUG
//#else
                printfn "%O escape %c append" s c
#endif
                s.wasEscaped <- false
                append c
                notInQuote (next())
            | _, _, true when Array.exists ((=) c) delimiters ->
#if DEBUG
//#else
                logger.debugf "%O escape %c seek" s c
#endif
                s.wasEscaped <- false
                seek c
                notInQuote (next())
            | _, _, false when Array.exists ((=) c) delimiters ->
#if DEBUG
//#else
                logger.debugf "%O escape %c append" s c
#endif
                s.wasEscaped <- false
                append c
                notInQuote (next())
            | _, _, true ->
#if DEBUG
//#else
                logger.debugf "%O escape %c seek, not delimiter" s c
#endif
                s.wasEscaped <- false
                if Some c <> quoteChar then
                    seek escapeChar
                seek c
                notInQuote (next())
            | _, _, _ ->
#if DEBUG
//#else
                logger.debugf "%O escape %c append, not delimiter" s c
#endif
                s.wasEscaped <- false
                if Some c <> quoteChar then
                    append escapeChar
                append c
                notInQuote (next())


    and inQuoteEscape (co:char option) =
        if co.IsNone then
            logger.pfErrorf "inQuoteEscape: escape character should paired with escaped character"

        else
            let c = co.Value
            match c, s.fieldStarted, s.fieldNoQuoteNow with
            //| _, false, _ when Array.exists ((=) c) delimiters ->
            | _, false, _ when Some c = quoteChar ->
#if DEBUG
//#else
                logger.debugf "%O inQuoteEscape %c append" s c
#endif
                s.wasEscaped <- false
                append c
                inQuote (next())
            | _, _, _ when c = escapeChar ->
#if DEBUG
//#else
                logger.debugf "%O inQuoteEscape %c append" s c
#endif
                s.wasEscaped <- false
                append c
                inQuote (next())
            | 'r', _, _ ->
#if DEBUG
//#else
                logger.debugf "%O inQuoteEscape \%c append" s c
#endif
                s.wasEscaped <- false
                append '\r'
                inQuote (next())
            | 'n', _, _ ->
#if DEBUG
//#else
                logger.debugf "%O inQuoteEscape \%c append" s c
#endif
                s.wasEscaped <- false
                append '\n'
                inQuote (next())
            
            | _ -> 
                logger.pfErrorf "%O invalid inQuoteEscape %c " s c

    and exitQuote (co:char option) = //leaveQuote
        if co.IsNone then
            //emptyNotIgnored ()
            //printfn $"exitQuote emptyNotIgnored"
#if DEBUG
//#else
            logger.debugf $"exitQuote"
#endif
            ()
        else
            let c = co.Value
            match c, s.fieldStarted, s.fieldNoQuoteNow with
            | _, true, _ 
            | _, _, false ->
                failwithf "%O exitQuote should be end of a field %c" s c
            | ' ', _, _ when Array.contains ' ' delimiters -> 
#if DEBUG
//#else
                logger.debugf "%O exitQuote '%c' exitQuote" s c
#endif
                reset ()
                notInQuote (next())
            | ' ', _, _ -> 
#if DEBUG
//#else
                logger.debugf "%O exitQuote '%c' exitQuote" s c
#endif
                exitQuote (next())

            | _, _, _ when Array.exists ((=) c) delimiters ->
#if DEBUG
//#else
                logger.debugf "%O exitQuote %c add -> reset -> notInQuote, %A" s c s.result
#endif
                //add ()
                reset ()
                notInQuote (next())
            | _ -> 
                logger.pfErrorf "Field already closed"
         
    notInQuote (next())
    s.result |> Seq.toArray



