namespace Fodex.Supervisor.Contracts

#if INTERACTIVE
#r "nuget: FAkka.FSharp.Quotations.Evaluator, 2.2.1"
#endif

module AliasMod =
    type 'T lzy = Lazy<'T>
    type CD<'K, 'V> = System.Collections.Concurrent.ConcurrentDictionary<'K, 'V>

module VersionMod =
    open System
    type VersionConverter =
        // 支援直接傳入 Version
        static member ToVersion (v: Version) = v
        // 支援傳入 string 並解析
        static member ToVersion (s: string) = Version.Parse(s)

    // 定義一個 inline 函式，透過靜態多載解析
    let inline getVersion (x: ^T) : Version =
        ((^T or VersionConverter) : (static member ToVersion : ^T -> Version) x)

    if false then
        // 測試呼叫：
        let v1 = getVersion "1.0.1"        // 自動轉成 Version
        ()

module Concepts =
    open System
    open System.Reflection
    open FSharp.Quotations
    open FAkka.FSharp.Quotations.Evaluator
    open VersionMod
    open AliasMod

    let f2 (o:obj) (args:obj[]) = args |> Seq.map string |> String.concat ", " |> box
    let f1 (args:obj[])         = args |> Seq.map string |> String.concat ", " |> box
    
    type MyInvoke2<^T, ^S, ^I, ^O when ^T: (member Invoke: ^S -> ^I[] -> ^O)> (t:^T) =
        member inline this.T = t
        member inline this.Invoke = t.Invoke
        member inline this.InvokeFun = fun sender args -> t.Invoke(sender, args)
    type MyInvoke<^T, ^I, ^O when ^T: (member Invoke: ^I[] -> ^O)> (t:^T) =
        member inline this.T = t
        member inline this.Invoke = t.Invoke
        member inline this.InvokeFun = fun sender args -> t.Invoke(args)

    let inline getInvoke<^T, ^S, ^I, ^O when ^T: (member Invoke: ^S -> ^I[] -> ^O)> (t:^T) =
        MyInvoke2(t).InvokeFun

    //type MyInvoke =
        //static member inline getInvoke<^T, ^S, ^I, ^O when ^T: (member Invoke: ^S -> ^I[] -> ^O)> (t:^T) =
        //    printfn "t4"
        //    MyInvoke2(t).InvokeFun
        //static member inline getInvoke<^T, ^I, ^O when ^T: (member Invoke: ^I[] -> ^O)> (t:^T) =
        //    printfn "t4"
        //    MyInvoke(t).InvokeFun
    type MyInvoke =
        static member inline getInvoke<^T, ^I, ^O when ^T: (member Invoke: ^I[] -> ^O)> (t:^T) =
            printfn "t4"
            MyInvoke(t).InvokeFun
        static member inline getInvoke<^T, ^S, ^I, ^O when ^T: (member Invoke: ^S * ^I[] -> ^O)> (t:^T) =
            printfn "t4"
            MyInvoke2(t).InvokeFun
        static member inline getInvoke<^Sender, ^Input, ^Output> (f: Func<^Sender, ^Input[], ^Output>) : ^Sender -> ^Input[] -> ^Output =
            printfn "t5"
            fun o args -> f.Invoke(o, args)

        static member inline getInvoke<^Input, ^Output> (f: Func<^Input[], ^Output>) : obj -> ^Input[] -> ^Output =
            printfn "t5"
            fun o args -> f.Invoke(args)


    let o : obj -> obj[] -> obj = MyInvoke.getInvoke(f2 ) 

    o null [|"1";"2";"3"|] 
    |> printfn "%A"
    MyInvoke.getInvoke(f1 ) null [|"1";"2";"3"|] |> printfn "%A"
    let fc = Func<_, _>(f1)
    let fc2 = Func<obj, obj[], obj>(f2)
    MyInvoke.getInvoke(fc2) null [|"1";"2";"3"|] |> printfn "%A"
    MyInvoke.getInvoke(fc) null [|"1";"2";"3"|] |> printfn "%A"
    fc.Invoke([|"1";"2";"3"|]) |> printfn "%A"
    fc2.Invoke(null, [|"1";"2";"3"|]) |> printfn "%A"
    MyInvoke.getInvoke(f1 ) null [|"1";"2";"3"|] |> printfn "%A"
    MyInvoke.getInvoke(fc).Invoke(null, [|"1";"2";"3"|]) |> printfn "%A"



    open System
    open System.Reflection

    //type InvocableDispatcher =
    //    // 優先級 1：如果是雙引數 Func
    //    static member inline Dispatch (f: Func<'Sender, 'Input[], 'Output>, _tag: InvocableDispatcher) =
    //        printfn "[Match] Double Func"
    //        f

    //    // 優先級 2：如果是單引數 Func
    //    static member inline Dispatch (f: Func<'Input[], 'Output>, _tag: InvocableDispatcher) =
    //        printfn "[Match] Single Func"
    //        Func<obj, 'Input[], 'Output>(fun _ args -> f.Invoke(args))

    //    // 優先級 3：任意型別！只要有吃 (Sender * Input[]) 的 Invoke 方法即可
    //    // 這裡利用 (^T or InvocableDispatcher) 的特性，完全不需要事先對 ^T 做 extension
    //    static member inline Dispatch (i: ^T, _tag: obj) =
    //        printfn "[Match] Duck Typing (Invoke)"
    //        Func<obj, 'Input[], 'Output>(fun o args ->
    //            let r = (^T : (member Invoke: obj * 'Input[] -> 'Output) (i, o, args))
    //            r
    //            )

    //type InvocableHelper =
    //    // 對外暴露的單一入口：完全沒有同名多載打架的問題
    //    static member inline toInvocable (x: ^T) =
    //        InvocableDispatcher.Dispatch(x, Unchecked.defaultof<InvocableDispatcher>)

    open System
    open System.Reflection

    type InvocableDispatcher =
        // 1. 單引數 Func<T, R>：包裝成雙引數，忽略 sender (傳 null 即可)
        static member inline Dispatch (f: Func<'Input[], 'Output>, _tag: InvocableDispatcher) : Func<obj, 'Input[], 'Output> =
            printfn "t1"
            Func<obj, 'Input[], 'Output>(fun _ args -> f.Invoke(args))

        // 2. 雙引數 Func<T1, T2, R>：如果傳入的本來就是 (sender, args) -> result
        static member inline Dispatch (f: Func<obj, 'Input[], 'Output>, _tag: InvocableDispatcher) : Func<obj, 'Input[], 'Output> =
            printfn "t2"
            f

        // 3. 任意物件 (MethodInfo 或帶有 Invoke(obj, obj[]) 的類別)
        // 透過 SRTP 鴨子型別解析
        static member inline Dispatch (i: ^T, _tag: obj) : Func<obj, 'Input[], 'Output> =
            printfn "t3"
            Func<obj, 'Input[], 'Output>(fun o args ->
                (^T : (member Invoke: obj * 'Input[] -> 'Output) (i, o, args)))

    type InvocableHelper =
        static member inline toInvocable (x: ^T) =
            InvocableDispatcher.Dispatch(x, Unchecked.defaultof<InvocableDispatcher>)

    type CustomService() =
        member this.Invoke(sender: obj, args: string[]) : string =
            sprintf "Custom handled: %s (sender: %A)" (String.concat "+" args) sender


    module ooo =
        open System
        open System.Reflection

        // 1. 定義一個 Adapter，提供給 Func / Delegate 作為預設解析路徑
        type InvocableAdapter =
            // 處理單引數 Func
            static member ToFunc (f: Func<'Input[], 'Output>) =
                printfn "[Match] Single Func"
                Func<obj, 'Input[], 'Output>(fun _ args -> f.Invoke(args))

            // 處理雙引數 Func
            static member ToFunc (f: Func<'Sender, 'Input[], 'Output>) =
                printfn "[Match] Double Func"
                f
        type MethodInfo with
            static member ToFunc (mi: MethodInfo) =
                printfn "[Match] MethodInfo"
                Func<obj, obj[], obj>(fun o args -> mi.Invoke(o, args))
    
        type FSharpFunc<'Input, 'Output> with
            static member ToFunc (f: FSharpFunc<'Input[], 'Output >) =
                printfn "[Match] FSharpFunc"
                Func<obj, 'Input[], 'Output>(fun _ args -> f args)

        // 2. 只有一個單一的 toInvocable 靜態方法，沒有同名多載打架的問題！
        type InvocableHelper =
            // 如果傳入的物件有 Invoke(Sender * Input[])，就直接呼叫其成員（如 MethodInfo）
            // 若沒有，則 fallback 到 InvocableAdapter.ToFunc 解析（如 Func）
            //static member toInvocable (x: obj) =
            //    match box x with
            //    | :? (obj -> obj[] -> obj) as f -> Func<_, _,_> f
            //    | :? (       obj[] -> obj) as f -> Func<_, _,_> (fun o a -> f a)

            static member inline toInvocable (x: ^T) =
                printfn "12321"
                ((^T or InvocableAdapter) : (static member ToFunc : ^T -> _) x)

        // 3. 為 MethodInfo 擴充靜態 ToFunc 成員，讓 SRTP 優先命中

        let f1 = fun (args: obj[]) -> box (String.concat ", " (args |> Seq.map string))
        let f2 = fun _ (args: obj[]) -> box args.Length

        let fc1 = Func<obj[], obj>(f1)
        let fc2 = Func<obj, obj[], obj>(f2)
        let concatMi = typeof<string>.GetMethod("Concat", [| typeof<seq<string>> |])

        // 2. 雙引數 Func -> 命中 InvocableAdapter.ToFunc
        let inv2 = InvocableHelper.toInvocable fc2
        inv2.Invoke(null, [| 1; 2; 3 |]) |> printfn "fc2: %A"

        // 3. MethodInfo -> 命中 MethodInfo.ToFunc
        let inv3 = InvocableHelper.toInvocable concatMi
        inv3.Invoke(null, [| box (seq [| "1"; "2"; "3" |]) |]) |> printfn "concatMi: %A"

        // 1. 單引數 Func -> 命中 InvocableAdapter.ToFunc
        let inv1 = InvocableHelper.toInvocable fc1
        inv1.Invoke(null, [| "A"; "B" |]) |> printfn "fc1: %A"

        let invF1 = InvocableHelper.toInvocable f1
        invF1.Invoke(null, [| "A"; "B" |]) |> printfn "f1: %A"


System.Func`3[System.Object,System.Object[][],System.Object]
System.Func`3[System.Object,System.Object[],System.Object]'.




    let fc1 = Func<obj[], obj>(fun args -> box (String.concat ", " (args |> Seq.map string)))
    let inv1 = InvocableHelper.toInvocable fc1
    inv1.Invoke(null, [| "A"; "B" |]) |> printfn "fc1: %A"
    
    // 2. 雙引數 Func
    let fc2 = Func<obj, obj[], obj>(fun (o:obj) args -> box args.Length)
    let inv2 = InvocableHelper.toInvocable fc2
    inv2.Invoke(null, [| 1; 2; 3 |]) |> printfn "fc2: %A"
    
    // 3. MethodInfo（根本沒寫過 MethodInfo extension）
    let concatMi = typeof<string>.GetMethod("Concat", [| typeof<seq<string>> |])
    let inv3 = InvocableHelper.toInvocable concatMi
    inv3.Invoke(null, [| box (seq [| "1"; "2"; "3" |]) |]) |> printfn "MethodInfo: %A"
    
    // 4. 任意第三方程式庫的物件（純 Duck Typing）
    let custom = CustomService()
    let inv4 = InvocableHelper.toInvocable custom
    inv4.Invoke("Server1", [| "X"; "Y"; "Z" |]) |> printfn "Custom: %A"






    open System
    open System.Reflection

    // 1. 定義預設處理器（Fallback）
    type DefaultInvocable =
        // 當傳入的是單引數 Func
        static member ToInv (f: Func<'Input[], 'Output>) =
            Func<obj, 'Input[], 'Output>(fun _ args -> f.Invoke(args))

        // 當傳入的是雙引數 Func
        static member ToInv (f: Func<obj, 'Input[], 'Output>) =
            f

    // 2. 定義 SRTP 統一呼叫器
    type InvocableHelper =
        // 利用 (DefaultInvocable or ^T) 的靜態解析規則：
        // 若 ^T 具備 Invoke(obj, obj[]) 則直接以 lambda 封裝呼叫
        // 否則走 DefaultInvocable.ToInv 的多載解析
        static member inline toInvocable (f: Func<'Input[], 'Output>) : Func<obj, 'Input[], 'Output> =
            Func<obj, 'Input[], 'Output>(fun _ args -> f.Invoke(args))

        static member inline toInvocable (f: Func<obj, 'Input[], 'Output>) : Func<obj, 'Input[], 'Output> =
            f

        static member inline toInvocable (mi: MethodInfo) : Func<obj, obj[], obj> =
            Func<obj, obj[], obj>(fun o args -> mi.Invoke(o, args))








    type Feasible<'TSuccess, 'TError> = unit -> Result<'TSuccess, 'TError>

    type FAkkaArguArguStringArray = string []

    type JsonStr = string

    //首先: agents wrapper 吃一個 prompt 可以叫 1 或多個 agents 來做事 
    type AgentPrompt = 
        | Prompt of string
        | PromptWithArgs of string * FAkkaArguArguStringArray

    type AgentResponse = 
        | JsonList of JsonStr list
        | Json

    type NativeAgentMessage = string
    type TargetName = string

    type VersionedTarget<'T> = { Version: Version; Value: 'T }

    type Versioned<'T> (target:TargetName) =
        member this.Item
            with get (version: Version) : 'T option =
                failwithf "Not implemented: get %A from VersionRegistry<'T>" version
            and set (version: Version) (value: 'T) =
                failwithf "Not implemented: set %A in VersionRegistry<'T>" version

        member this.Instance = 
            failwithf "Not implemented: Instance of latest version 'T"
        member this.Registry: CD<Version, VersionedTarget<'T>> = 
            VersionRegistry<'T>.RegistryCD[target]

    and VersionRegistry<'T> = 
        static let registry = CD<TargetName, CD<Version, VersionedTarget<'T>>>()
        static let init =
            printfn "Initializing VersionRegistry<'T> for %s" typeof<'T>.FullName
            do
            (* to agent who develop this
            正常來說多數的功能程式碼中會有最原始的一個版本
            這段就是從 DB 讀出來後注入 registry
            *)
                failwithf "Not implemented: init VersionRegistry<'T>"
        static member RegistryCD : CD<TargetName, CD<Version, VersionedTarget<'T>>> = registry
        static member GetVersioned: TargetName -> Versioned<'T> =
            failwithf "Not implemented: GetVersioned for %s"
        static member UpsertVersioned: TargetName -> Versioned<'T> -> unit =
            failwithf "Not implemented: UpsertVersioned for %s"
        static member CleanVersioned: TargetName -> (unit -> Version []) -> unit =
            failwithf "Not implemented: CleanVersioned for %s"
        

    type SimpleFun<'Input, 'Output> (q:Expr<'Input -> 'Output>) = 
        member this.q = q
        member val f:Lazy<'Input -> 'Output> = lazy (QuotationEvaluator.Evaluate q)

    type FeasibleFun<'Input, 'OutputSuccess, 'TError> = 
        SimpleFun<'Input, Feasible<'OutputSuccess, 'TError>>

    type FodexFun<'Input, 'OutputSuccess, 'TError> = Versioned<FeasibleFun<'Input, 'OutputSuccess, 'TError>>

    type WrapperAgentMessageConvert<'T> = FodexFun<'T, string, exn>
        
    type MsgConvert<'T> = WrapperAgentMessageConvert<'T>

    (*
    描述一下 cli 的底層行為，基本上處理 
    1. user input
    2. wrapper output
    3. cli 與 wrapper 基本 1-1
    *)
    type TCliStart = string [] -> unit
    
    let targetCliStart = "CliStart"
    let targetPromptConvert = "PromptConvert"

    [<AbstractClass>]
    type Cli () =
        //entrypoint 會吃 cli arguments 起動 cli process
        abstract member CliStart: TCliStart
        default this.CliStart = 
            fun (args: string []) ->
                printfn "Starting CLI with arguments: %A" args
                // 這裡可以加入更多的邏輯來處理 CLI 啟動
                let v100 = getVersion "1.0.0"
                let versionedCliStart = new Versioned<TCliStart> (targetCliStart)
                let f = 
                    match versionedCliStart[v100] with
                    | Some v -> v
                    | None -> 
                        printfn "Version 1.0.0 not found, default is applied"
                        let r = versionedCliStart.Registry
                        let cliStartFun =
                            fun args -> printfn "Default CLI Start for version 1.0.0 with args: %A" args 
                        r[v100] <- { Version = v100; Value = cliStartFun}
                        VersionRegistry<TCliStart>.UpsertVersioned targetCliStart versionedCliStart
                        cliStartFun

                f args

    //描述一下 wrapper 與 natives 互動
    [<AbstractClass>]
    type ReActLoop =
        


    [<AbstractClass>]
    type IAgent<'T> () = 
        abstract member Start: unit -> unit
        abstract member Stop: unit -> unit
        abstract member Send: 'T -> unit

    //Wrapped agent (簡稱 Native Agent，natives) 與 agents wrapper (簡稱 wrapper) 都是一種 Agent
    [<AbstractClass>]
    type FodexAgent<'T> () =
        static member createManagedAgent<'T> () = 
            {
                new IAgent<'T>() with
                    member this.Start() = printfn "Starting agent of type %s" typeof<'T>.FullName
                    member this.Stop() = printfn "Stopping agent of type %s" typeof<'T>.FullName
                    member this.Send(msg) = printfn "Sending message to agent of type %s: %A" typeof<'T>.FullName msg

            }





















open System
open System.Reflection

// 1. 定義一個 Adapter，提供給 Func / Delegate 作為預設解析路徑
type InvocableAdapter =
    // 處理單引數 Func
    static member ToFunc (f: Func<'Input[], 'Output>) =
        printfn "[Match] Single Func"
        Func<obj, 'Input[], 'Output>(fun _ args -> f.Invoke(args))

    // 處理雙引數 Func
    static member ToFunc (f: Func<'Sender, 'Input[], 'Output>) =
        printfn "[Match] Double Func"
        f

type FSharpFunc<'Input, 'Output> with
    static member ToFunc (f: FSharpFunc<'Input[], 'Output >) =
        printfn "[Match] FSharpFunc"
        Func<obj, 'Input[], 'Output>(fun _ args -> f args)

    static member ToFunc (f: FSharpFunc<'Sender, FSharpFunc<'Input[], 'Output >>) =
        printfn "[Match] FSharpFunc"
        Func<'Sender, 'Input[], 'Output>(fun o args -> (f o) args)

//type MethodInfo with
//    static member ToFunc (mi: MethodInfo) =
//        printfn "[Match] MethodInfo"
//        Func<obj, obj[], obj>(fun o args -> mi.Invoke(o, args))

//type CustomService() =
//    member this.Invoke(sender: obj, args: obj[]) : string =
//        sprintf "Custom handled: %s (sender: %A)" (String.concat "+" (args |> Array.map string)) sender    
//    static member ToFunc (cs: CustomService) =
//        printfn "[Match] MethodInfo"
//        Func<obj, obj[], obj>(fun o args -> cs.Invoke(o, args))
    


type InvocableHelper =
    static member inline toInvocable (x: ^T) =
        printfn "12321"
        ((^T or InvocableAdapter) : (static member ToFunc : ^T -> _) x)

let f1 = fun (args: obj[]) -> box (String.concat ", " (args |> Seq.map string))
let f2 = fun (o:obj) (args: obj[]) -> box args.Length

let fc1 = Func<obj[], obj>(f1)
let fc2 = Func<obj, obj[], obj>(f2)


// 2. 雙引數 Func -> 命中 InvocableAdapter.ToFunc
let inv2 = InvocableHelper.toInvocable fc2
inv2.Invoke(null, [| 1; 2; 3 |]) |> printfn "fc2: %A"

// 1. 單引數 Func -> 命中 InvocableAdapter.ToFunc
let inv1 = InvocableHelper.toInvocable fc1
inv1.Invoke(null, [| "A"; "B" |]) |> printfn "fc1: %A"

let invF1 = InvocableHelper.toInvocable f1
invF1.Invoke(null, [| "FU"; "CK" |]) |> printfn "f1: %A"

let invF2:Func<obj,obj array,(obj array -> objnull)> = InvocableHelper.toInvocable f2
//let invF2 = InvocableHelper.toInvocable f2
invF2.Invoke(null, [| null |])([| "FU1"; "CK2"; "UU3" |]) |> printfn "f2: %A"

let csi = InvocableHelper.toInvocable (CustomService())
csi.Invoke(null, [| "cFU"; "CKc" |]) |> printfn "csi: %A"