namespace Fodex.Supervisor

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Encodings.Web

[<CLIMutable>]
type ConsolePromptHistoryFileV100 = { Version:int; Prompts:string array }

/// One local CLI history, shared across its selected wrappers. No LINE/model output.
module ConsolePromptHistoryV100 =
    [<Literal>]
    let fileName="prompt-history.json"
    let defaultPath ()=Path.Combine(AppContext.BaseDirectory,fileName)
    let jsonOptions=JsonSerializerOptions(WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    type History(path:string) =
        let path=Path.GetFullPath path
        let entries=ResizeArray<string>()
        let mutable position:int option=None
        let mutable writable=true
        let mutable error:exn option=None
        do
            try
                if File.Exists path then
                    let saved=JsonSerializer.Deserialize<ConsolePromptHistoryFileV100>(File.ReadAllText(path,UTF8Encoding(false,true)),jsonOptions)
                    if isNull(box saved) || saved.Version<>1 || isNull saved.Prompts || saved.Prompts |> Array.exists isNull then
                        raise(InvalidDataException "Invalid prompt history schema; existing file preserved")
                    entries.AddRange(saved.Prompts |> Array.filter(not << String.IsNullOrWhiteSpace))
            with failure -> writable<-false;error<-Some failure
        member _.Path=path
        member _.Entries=entries.ToArray()
        member _.ResetBrowse()=position<-None
        member _.TakeError()=
            let value=error
            error<-None
            value
        /// Up starts at newest; Down starts at oldest. Both ends wrap, with no draft sentinel.
        member _.Browse(previous:bool) =
            if entries.Count=0 then None else
            let next=match position with
                     | None -> if previous then entries.Count-1 else 0
                     | Some index -> (index+entries.Count+(if previous then -1 else 1))%entries.Count
            position<-Some next
            Some entries[next]
        member _.Record(text:string) =
            position<-None
            if not(String.IsNullOrWhiteSpace text) then
                entries.Add(text.ReplaceLineEndings "\n")
                if writable then
                    let temporary=path+"."+Guid.NewGuid().ToString("N")+".pending"
                    try
                        try
                            let data={Version=1;Prompts=entries.ToArray()}
                            File.WriteAllText(temporary,JsonSerializer.Serialize(data,jsonOptions),UTF8Encoding(false))
                            File.Move(temporary,path,true)
                        finally
                            if File.Exists temporary then File.Delete temporary
                    with failure -> error<-Some failure
