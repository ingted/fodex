# 01. FeasibleFun 與 FodexFun 基底

本段保留既定的雙層函數語意，不把它簡化成單層 `Input -> Result`。

```fsharp
type FeasibleT<'Input, 'Success, 'Error> =
    'Input -> Result<'Success, 'Error>

type SimpleFun<'Input, 'Output>
    (q: Expr<'Input -> 'Output>) =

    member this.q = q
    member val f = lazy (QuotationEvaluator.Evaluate q)

    member this.Invoke(input: 'Input) : 'Output =
        this.f.Value input

type FeasibleFun<'Input, 'Success, 'Error> =
    SimpleFun<
        'Input,
        FeasibleT<'Input, 'Success, 'Error>
    >

type FodexFun<'Input, 'Success, 'Error> =
    Versioned<FeasibleFun<'Input, 'Success, 'Error>>
```

`FeasibleFun` 展開後就是：

```fsharp
'Input -> ('Input -> Result<'Success, 'Error>)
```

統一執行語意如下。外層先以 `input` 準備一個吃 `input` 的函數；內層再收到同一個 `input` 並執行。

```fsharp
module FeasibleFun =
    let invoke
        (input: 'Input)
        (feasibleFun: FeasibleFun<'Input, 'Success, 'Error>)
        : Result<'Success, 'Error> =

        let inputToResult = feasibleFun.Invoke input
        inputToResult input
```

版本選擇只在建立 runtime object 時執行一次。未指定版本就取當時最大版本；後續 registry hot-load 不替換已 pin 的 `FeasibleFun`。

```fsharp
module FodexFun =
    let pin
        (versionText: string option)
        (fodexFun: FodexFun<'Input, 'Success, 'Error>)
        : FeasibleFun<'Input, 'Success, 'Error> option =

        match versionText with
        | Some version -> fodexFun[Version.Parse version]
        | None -> fodexFun.Instance
```

### 本段不做的事

- 不預先 `QuotationEvaluator.Evaluate` 後再註冊 raw function。
- 不建立 `Versioned<'Input -> Result<...>>`。
- 不把雙層 input 視為錯誤。
- 不讓 hot-load 偷換既有 instance 已 pin 的 function。
