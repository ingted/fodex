// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_commands.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Commands
#endif
// LINE的**只是transport syntax；參數數量、引號與位置一律由既有ParseLine + FAkka.Argu處理。
open System
open System.Text
open System.Text.RegularExpressions
open Argu

/// Argu input is separate from the semantic command: --force is optional,
/// not a second positional argument of Dropagent.
type Argument =
    | [<CustomCommandLine("--newagent")>] InputNewagent of provider:string * topic:string
    | [<CustomCommandLine("--listagent")>] InputListagent
    | [<CustomCommandLine("--setagent")>] InputSetagent of target:string
    | [<CustomCommandLine("--setdefault")>] InputSetdefault of target:string
    | [<CustomCommandLine("--whoami")>] InputWhoami
    | [<CustomCommandLine("--dropagent")>] InputDropagent of target:string
    | [<CustomCommandLine("--force")>] Force
    | [<CustomCommandLine("--model")>] InputModel of target:string * model:string * effort:string
    | [<CustomCommandLine("--models")>] InputModels of provider:string
    | [<CustomCommandLine("--cache-c")>] InputCacheC of key:string * value:string
    | [<CustomCommandLine("--cache-r")>] InputCacheR of key:string
    | [<CustomCommandLine("--cache-u")>] InputCacheU of key:string * value:string
    | [<CustomCommandLine("--cache-d")>] InputCacheD of key:string
    | [<CustomCommandLine("--to")>] InputTo of target:string * text:string
    interface IArgParserTemplate with
        member _.Usage="LINE controls: **whoami, **listagent, **setagent target, **setdefault target, **dropagent target [--force], **newagent agy|codex Topic, **[Topic] prompt."

type Command =
    | Newagent of provider:string * topic:string
    | Listagent
    | Setagent of target:string
    | Setdefault of target:string
    | Whoami
    | Dropagent of target:string * force:bool
    | Model of target:string * model:string * effort:string
    | Models of provider:string
    | Cache_C of key:string * value:string
    | Cache_R of key:string
    | Cache_U of key:string * value:string
    | Cache_D of key:string
    | To of target:string * text:string
let parser=ArgumentParser.Create<Argument>("LINE Fodex v3 commands")
// ParseLine的反斜線是一般字元escape，不是JSON escape；保留實際換行，不能把LF寫成\n。
let quote (value:string)="\""+value.Replace("\\","\\\\").Replace("\"","\\\"")+"\""
let isPrefix (value:string)=value.Length>=6 && value.Length<=32 && value |> Seq.forall Uri.IsHexDigit

/// 顯示值保留原字串，lookup只用Trim→NFC→UpperInvariant，不依SQL預設collation。
/// 例：" 綜合分析 "與"綜合分析"查找同一Topic；不接受GUID或六碼hex假裝Topic。
let topic (value:string)=
    if isNull value || value.Length>128 || value.Trim().Length=0 || value |> Seq.exists(fun ch -> Char.IsControl ch || ch='[' || ch=']') then
        invalidArg "topic" "Topic must contain 1..128 characters, without control characters, newlines or brackets"
    let normalized=value.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant()
    if fst(Guid.TryParse normalized) || isPrefix normalized then invalidArg "topic" "Topic cannot be a wrapper SID or hexadecimal SID prefix"
    value,normalized

let provider value=if value="agy" || value="codex" then value else invalidArg "provider" "Expected agy or codex"
let key (value:string)=
    if isNull value || value.Length=0 || value.Length>256 || value.Contains('\000') || Char.IsWhiteSpace value[value.Length-1] then
        invalidArg "key" "Cache key must contain 1..256 characters, no NUL or trailing whitespace"
let cacheValue (value:string)=
    if UTF8Encoding(false,true).GetByteCount value>1024*1024 then invalidArg "value" "Cache value exceeds 1MiB UTF-8"

/// Transport normalization只認固定前綴，不拆prompt/body；model list優先於model set。
let normalize (text:string)=
    let input=text.TrimStart()
    let target=Regex.Match(input,@"^\*\*\[([^\]]+)\]([\s\S]*)$")
    if target.Success then "--to "+quote target.Groups[1].Value+" "+quote(target.Groups[2].Value.TrimStart())
    elif Regex.IsMatch(input,@"^\*\*model\s+list(?:\s|$)") then Regex.Replace(input,@"^\*\*model\s+list","--models")
    elif Regex.IsMatch(input,@"^\*\*cache\s+[crdu](?:\s|$)") then
        Regex.Replace(input,@"^\*\*cache\s+([crdu])",MatchEvaluator(fun matched -> "--cache-"+matched.Groups[1].Value))
    else Regex.Replace(input,@"^\*\*([A-Za-z_]+)","--$1")

/// None表示普通模型輸入；Some為deterministic control或一次性指定目標。
/// **reset與/next仍交現有明確流程，不把它們當成unknown command或普通生成。
let parse (text:string)=
    try
        if isNull text || not(text.TrimStart().StartsWith("**",StringComparison.Ordinal)) || text.Trim()="**reset" then Ok None else
        let result=parser.ParseCommandLine(PL.parseLine [|' '|] (Some '"') None true (normalize text),raiseOnUsage=false)
        let force=result.Contains Force
        let commands=result.GetAllResults() |> List.filter((<>) Force)
        let command=
            match commands with
            | [InputNewagent(a,b)] when not force -> Newagent(a,b)
            | [InputListagent] when not force -> Listagent
            | [InputSetagent value] when not force -> Setagent value
            | [InputSetdefault value] when not force -> Setdefault value
            | [InputWhoami] when not force -> Whoami
            | [InputDropagent value] -> Dropagent(value,force)
            | [InputModel(a,b,c)] when not force -> Model(a,b,c)
            | [InputModels value] when not force -> Models value
            | [InputCacheC(a,b)] when not force -> Cache_C(a,b)
            | [InputCacheR value] when not force -> Cache_R value
            | [InputCacheU(a,b)] when not force -> Cache_U(a,b)
            | [InputCacheD value] when not force -> Cache_D value
            | [InputTo(a,b)] when not force -> To(a,b)
            | _ -> invalidArg "command" "Exactly one command is required; --force is valid only with dropagent"
        match command with
        | command ->
            match command with
            | Newagent(engine,name) -> provider engine |> ignore;topic name |> ignore
            | Models engine -> provider engine |> ignore
            | Cache_C(name,value) | Cache_U(name,value) -> key name;cacheValue value
            | Cache_R name | Cache_D name -> key name
            | To(target,text) when String.IsNullOrWhiteSpace target || String.IsNullOrWhiteSpace text -> invalidArg "text" "A target and nonempty prompt are required"
            | _ -> ()
            Ok(Some command)
    with
    | :? ArguParseException ->
        // Argu's full usage is a developer diagnostic, not a LINE reply. Keep
        // the established **[Topic] body syntax; do not reinterpret malformed
        // controls as a prompt, which could accidentally trigger native work.
        Error(ArgumentException "LINE 指令格式錯誤。一次指定目標請使用 **[Topic 或 SID] message；控制參數含空白時請加雙引號。" :> exn)
    | error -> Error error

/// 呼叫端必須先限定同一LINE conversation，再交此函數；只回傳唯一root WSID。
/// Topic重用後不能透過已drop的舊alias找到新root，所以rows由SQL限定未tombstone名冊。
let resolve (rows:(Guid*string) array) (selector:string)=
    let text=selector.Trim()
    let matches=
        match Guid.TryParse text with
        | true,sid -> rows |> Array.filter(fun (value,_) -> value=sid)
        | _ when isPrefix text -> rows |> Array.filter(fun (value,_) -> value.ToString("N").StartsWith(text,StringComparison.OrdinalIgnoreCase))
        | _ -> let _,canonical=topic selector in rows |> Array.filter(fun (_,name) -> snd(topic name)=canonical)
    match matches with
    | [|sid,_|] -> sid
    | [||] -> raise(Collections.Generic.KeyNotFoundException "No matching Topic or root wrapper SID in this LINE conversation")
    | _ -> invalidArg "selector" "Ambiguous wrapper SID prefix; use Topic or the full wrapper SID"

/// Service create的root argv邊界；與LINE命令分開，避免用字串replace誤改model或未知參數。
type RootArgument =
    | [<CustomCommandLine("--mainagent-type")>] Root_Mainagent_Type of string
    | [<CustomCommandLine("--native-adapter")>] Root_Native_Adapter of string
    | [<CustomCommandLine("--model")>] Root_Model of string
    | [<CustomCommandLine("--effort")>] Root_Effort of string
    | [<CustomCommandLine("--working-directory")>] Root_Working_Directory of string
    | [<CustomCommandLine("--subagent-type")>] Root_Subagent_Type of string
    | [<CustomCommandLine("--subagent-mode")>] Root_Subagent_Mode of string
    | [<CustomCommandLine("--agy-exe")>] Root_Agy_Exe of string
    | [<CustomCommandLine("--fast")>] Root_Fast of bool
    | [<CustomCommandLine("--memory")>] Root_Memory of bool
    | [<CustomCommandLine("--sub-codex-model")>] Root_Sub_Codex_Model of string
    | [<CustomCommandLine("--sub-codex-effort")>] Root_Sub_Codex_Effort of string
    | [<CustomCommandLine("--sub-agy-model")>] Root_Sub_Agy_Model of string
    | [<CustomCommandLine("--sub-agy-effort")>] Root_Sub_Agy_Effort of string
    | [<CustomCommandLine("--role")>] Root_Role of string
    interface IArgParserTemplate with member _.Usage="Persisted native configuration; role is read only for legacy provenance and is never sent as create override."
let rootParser=ArgumentParser.Create<RootArgument>()
let updatePair (arguments:string array) model effort=
    let parsed=rootParser.ParseCommandLine(arguments,raiseOnUsage=false)
    let retained=parsed.GetAllResults() |> List.filter(function Root_Model _ | Root_Effort _ | Root_Role _ -> false | _ -> true)
    rootParser.PrintCommandLineArguments(retained@[Root_Model model;Root_Effort effort])

/// Deterministic catalog output; no model summarization or invented per-model efforts.
/// 900字元是目前LINE extension可直接閱讀的預覽預算，不宣稱是LINE API限制。
/// SQL保留完整頁；清單只顯示選模型需要的欄位，避免塞進upgrade廣告等原始JSON。
let catalogPages (catalog:System.Text.Json.JsonElement) =
    let text (element:System.Text.Json.JsonElement) (name:string) =
        let mutable value=Unchecked.defaultof<System.Text.Json.JsonElement>
        if element.TryGetProperty(name,&value) && value.ValueKind=System.Text.Json.JsonValueKind.String then value.GetString() else ""
    let header=sprintf "Provider=%s capturedAt=%s fresh=%s" (text catalog "provider") (text catalog "capturedAt") (catalog.GetProperty("fresh").GetRawText())
    let lines=ResizeArray<string>()
    lines.Add header
    let mutable efforts=Unchecked.defaultof<System.Text.Json.JsonElement>
    if catalog.TryGetProperty("cliEfforts",&efforts) && efforts.GetArrayLength()>0 then
        lines.Add("CLI efforts: "+String.concat ", " (efforts.EnumerateArray() |> Seq.map(fun x -> x.GetString()))+" (not per-model evidence)")
    for model in catalog.GetProperty("models").EnumerateArray() do
        let id=match text model "id" with "" -> text model "model" | value -> value
        let display=text model "displayName"
        let mutable supported=Unchecked.defaultof<System.Text.Json.JsonElement>
        let knownEfforts=
            if model.TryGetProperty("supportedReasoningEfforts",&supported) && supported.ValueKind=System.Text.Json.JsonValueKind.Array then
                supported.EnumerateArray() |> Seq.map(fun value -> if value.ValueKind=System.Text.Json.JsonValueKind.String then value.GetString() else text value "reasoningEffort") |> Seq.filter((<>) "") |> String.concat ","
            else ""
        let defaultEffort=text model "defaultReasoningEffort"
        lines.Add(id+(if display="" || display=id then "" else " | "+display)+(if knownEfforts="" then "" else " | efforts="+knownEfforts)+(if defaultEffort="" then "" else " | default="+defaultEffort))
    let error=text catalog "error"
    if error<>"" then lines.Add("Catalog error: "+error)
    let pages=ResizeArray<string>()
    let mutable current=""
    for line in lines do
        let mutable remaining=line
        while remaining.Length>0 do
            let count=min 900 remaining.Length
            // Do not split a UTF-16 surrogate pair at the page boundary.
            let count=if count<remaining.Length && Char.IsHighSurrogate remaining[count-1] then count-1 else count
            let piece=remaining.Substring(0,count)
            if current.Length+piece.Length+1>900 then pages.Add current;current<-""
            current<-(if current="" then piece else current+"\n"+piece)
            remaining<-remaining.Substring(count)
    if current<>"" then pages.Add current
    pages |> Seq.mapi(fun index page -> sprintf "[%d/%d]\n%s%s" (index+1) pages.Count page (if index+1<pages.Count then "\n/next 取下一頁" else "")) |> Seq.toArray
