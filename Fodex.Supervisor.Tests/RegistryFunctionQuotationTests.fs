namespace Fodex.Supervisor.Tests

open System
open Expecto
open FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.AliasMod
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

module RegistryFunctionQuotationTests =
    let testContext =
        let logger =
            {
                new UniLogger() with
                    member _.Info _ = ()
                    member _.Debug _ = ()
                    member _.Warn _ = ()
                    member _.Error _ = ()
                    member _.infof format = Printf.kprintf ignore format
                    member _.debugf format = Printf.kprintf ignore format
                    member _.warnf format = Printf.kprintf ignore format
                    member _.errorf format = Printf.kprintf ignore format
                    member _.pInfof format = Printf.kprintf ignore format
                    member _.pDebugf format = Printf.kprintf ignore format
                    member _.pWarnf format = Printf.kprintf ignore format
                    member _.pErrorf format = Printf.kprintf ignore format
                    member _.fInfof format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.fDebugf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.fWarnf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.fErrorf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
                    member _.pfErrorf format =
                        Printf.kprintf (fun message -> raise (Exception message)) format
            }

        {
            logger = logger
            usage = ["RegistryFunctionQuotationTests"]
            caller = []
        }

    let expectOk label result =
        match result with
        | Ok value -> value
        | Error error ->
            failtestf "%s returned Error: %A" label error

    let twiceInputQuotation:
        Expr<int -> FeasibleT<int, int, string>> =
        <@
            fun outerInput ->
                fun innerInput ->
                    Ok (outerInput * 100 + innerInput)
        @>

    let addQuotation amount:
        Expr<int -> FeasibleT<int, int, string>> =
        <@
            fun outerInput ->
                let preparedAmount = outerInput + amount

                fun innerInput ->
                    Ok (preparedAmount + innerInput)
        @>

    let newRegistry target =
        let registry =
            CD<
                TargetName,
                CD<
                    System.Version,
                    VersionedTarget<FeasibleFun<int, int, string>>
                >
            >()

        FodexFun<int, int, string>(registry, target)

    [<Tests>]
    let tests =
        testList "FeasibleFun and FodexFun" [
            testCase "invoke sends the same input through both stages" <| fun _ ->
                let feasibleFun =
                    FeasibleFun<int, int, string>(twiceInputQuotation)

                let actual =
                    FeasibleFun.invoke 7 feasibleFun
                    |> expectOk "twice input"

                Expect.equal actual 707
                    "outer and inner functions must receive the same input"

            testCase "quotation evaluation stays lazy" <| fun _ ->
                let feasibleFun =
                    FeasibleFun<int, int, string>(twiceInputQuotation)

                Expect.isFalse feasibleFun.f.IsValueCreated
                    "construction must not evaluate the quotation"

                FeasibleFun.invoke 1 feasibleFun |> ignore

                Expect.isTrue feasibleFun.f.IsValueCreated
                    "first invocation evaluates the quotation"

            testCase "exact version resolves the registered FeasibleFun" <| fun _ ->
                let registry = newRegistry "Exact"
                let version = System.Version(1, 0, 0)
                let feasibleFun =
                    FeasibleFun<int, int, string>(addQuotation 10)

                registry.Register(version, feasibleFun)

                let resolved =
                    FodexFun.pin (Some "1.0.0") registry
                    |> Option.defaultWith (fun () -> failtest "version missing")

                let actual =
                    FeasibleFun.invoke 2 resolved
                    |> expectOk "exact"

                Expect.equal actual 14
                    "outer prepares 12 and inner receives 2"

            testCase "unspecified version pins the greatest System.Version" <| fun _ ->
                let registry = newRegistry "Latest"

                registry.Register(
                    System.Version(1, 0, 0),
                    FeasibleFun<int, int, string>(addQuotation 10)
                )

                registry.Register(
                    System.Version(2, 0, 0),
                    FeasibleFun<int, int, string>(addQuotation 20)
                )

                let latest =
                    FodexFun.pin None registry
                    |> Option.defaultWith (fun () -> failtest "latest missing")

                let actual =
                    FeasibleFun.invoke 2 latest
                    |> expectOk "latest"

                Expect.equal actual 24
                    "version 2.0.0 must be selected"

            testCase "same version registration overwrites registry only" <| fun _ ->
                let registry = newRegistry "Overwrite"
                let version = System.Version(1, 0, 0)
                let original =
                    FeasibleFun<int, int, string>(addQuotation 10)

                registry.Register(version, original)
                let pinned = registry[version] |> Option.get

                registry.Register(
                    version,
                    FeasibleFun<int, int, string>(addQuotation 30)
                )

                let current = registry[version] |> Option.get

                let pinnedValue =
                    FeasibleFun.invoke 2 pinned
                    |> expectOk "pinned"

                let currentValue =
                    FeasibleFun.invoke 2 current
                    |> expectOk "current"

                Expect.equal pinnedValue 14
                    "an already pinned FeasibleFun keeps its original behavior"

                Expect.equal currentValue 34
                    "new resolution receives the overwritten version"

            testCase "missing exact version does not fall back to latest" <| fun _ ->
                let registry = newRegistry "Missing"

                registry.Register(
                    System.Version(2, 0, 0),
                    FeasibleFun<int, int, string>(addQuotation 20)
                )

                let missing = FodexFun.pin (Some "1.0.0") registry

                Expect.isNone missing
                    "an explicit version must never silently select another version"

            testCase "invalid version text does not escape the Result-based boundary" <| fun _ ->
                let registry = newRegistry "InvalidVersion"

                registry.Register(
                    System.Version(1, 0, 0),
                    FeasibleFun<int, int, string>(addQuotation 10)
                )

                let invalid = FodexFun.pin (Some "not-a-version") registry

                Expect.isNone invalid
                    "invalid version text must not throw during operation pinning"

            testCase "different targets remain isolated in a shared registry" <| fun _ ->
                let shared =
                    CD<
                        TargetName,
                        CD<
                            System.Version,
                            VersionedTarget<FeasibleFun<int, int, string>>
                        >
                    >()
                let version = System.Version(1, 0, 0)
                let targetA = FodexFun<int, int, string>(shared, "TargetA")
                let targetB = FodexFun<int, int, string>(shared, "TargetB")

                targetA.Register(
                    version,
                    FeasibleFun<int, int, string>(addQuotation 10)
                )

                Expect.isSome targetA.Instance
                    "the registered target must resolve its function"
                Expect.isNone targetB.Instance
                    "another target in the same registry must remain empty"

            testCase "Argu keeps NodeRole primitive and selectors explicit" <| fun _ ->
                let parsed =
                    FodexCommanddHelper.Parser.Parse(
                        [|
                            "--noderole"
                            "main-wrapper"
                            "--wrapperagent"
                            "1.0.0"
                        |],
                        ignoreMissing = true,
                        ignoreUnrecognized = true
                    )

                let nodeRole = parsed.TryGetResult CommonCommand.NodeRole
                let wrapperVersion =
                    (fun (results: Argu.ParseResults<CommonCommand>) ->
                        results.TryGetResult CommonCommand.WrapperAgent)
                        parsed

                Expect.equal nodeRole (Some "main-wrapper")
                    "NodeRole must be parsed as a primitive string"
                Expect.equal wrapperVersion (Some "1.0.0")
                    "runtime version selectors must use an explicit ParseResults lambda"

            testCase "ParseStr preserves a quoted Send payload" <| fun _ ->
                let parsed =
                    FodexCommanddHelper.ParseStr
                        testContext
                        "--send 1.0.0 \"fix parser\""

                let send = parsed.TryGetResult CommonCommand.Send

                Expect.equal send (Some ("1.0.0", "fix parser"))
                    "ParseLine and Argu must preserve spaces inside a quoted payload"
        ]
