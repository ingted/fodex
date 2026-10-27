namespace Fodex.Supervisor

open FAkka.FSharp.Quotations.Evaluator
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open Fodex.Supervisor.Contracts.TypesMod

module RuntimeRegistration =
    let registerValue<'T>
        (node: FodexNode)
        (target: TargetName)
        (version: System.Version)
        (value: 'T) =
        let versioned = Versioned<'T>(node, target)
        versioned.Register(version, value)

    /// 將 quotation Evaluate 成函數後才寫入 registry。
    /// 同版本依 contract 規則覆寫；已建立的 IAgent/ReActLoop 仍持有原先 pin 的值。
    let registerFunctionsV100<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode)
        : Result<unit, exn> =
        try
            let version = RegistryFunctionQuotationsV100.version

            RegistryFunctionQuotationsV100.cliStart
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetCliStart version

            let promptConvert =
                SimpleFun<
                    'TypedCmdToWrapper,
                    FeasibleT<'TypedCmdToWrapper, JsonStr, exn>
                >(
                    RegistryFunctionQuotationsV100.promptConvert<
                        'TypedCmdToWrapper
                    >
                )

            registerValue node targetPromptConvert version promptConvert

            RegistryFunctionQuotationsV100.iAgentStart
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetIAgentStart version

            RegistryFunctionQuotationsV100.iAgentStop
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetIAgentStop version

            RegistryFunctionQuotationsV100.iAgentSend<'TAgentCommMsg>
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetIAgentSend version

            RegistryFunctionQuotationsV100.iAgentAcquireParticipant
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetIAgentAcquireParticipant version

            RegistryFunctionQuotationsV100.reActLoopUpsertParticipant<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopUpsertParticipant version

            RegistryFunctionQuotationsV100.reActLoopRemoveParticipant<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopRemoveParticipant version

            RegistryFunctionQuotationsV100.reActLoopResolveParticipant<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopResolveParticipant version

            RegistryFunctionQuotationsV100.reActLoopListParticipants<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopListParticipants version

            RegistryFunctionQuotationsV100.reActLoopPostHumanPrompt<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopPostHumanPrompt version

            RegistryFunctionQuotationsV100.reActLoopDispatchToParticipant<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopDispatchToParticipant version

            RegistryFunctionQuotationsV100.reActLoopRouteReActDataStream<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopRouteReActDataStream version

            RegistryFunctionQuotationsV100.reActLoopCollect<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopCollect version

            RegistryFunctionQuotationsV100.reActLoopGetStatus<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            >
            |> QuotationEvaluator.Evaluate
            |> registerValue node targetReActLoopGetStatus version

            Ok ()
        with ex ->
            Error ex

    let registerRuntimeObjectsV100
        (node: FodexNode)
        (ctx: UniContext)
        : Result<unit, exn> =
        match WrapperAgentRuntime.createAgent (node, ctx) with
        | Error err -> Error err
        | Ok wrapperAgent ->
            registerValue
                node
                targetWrapperAgent
                RegistryFunctionQuotationsV100.version
                wrapperAgent
            Ok ()

    let register<'TypedCmdToWrapper, 'TAgentCommMsg>:
        FeasibleT<FodexNode * SupervisorOptions * UniContext, unit, exn> =
        fun (node, _options, ctx) ->
            match
                registerFunctionsV100<'TypedCmdToWrapper, 'TAgentCommMsg> node
            with
            | Error err -> Error err
            | Ok () -> registerRuntimeObjectsV100 node ctx
