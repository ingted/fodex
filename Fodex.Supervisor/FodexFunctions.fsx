#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

/// 唯一的內建宣告初始化，不是另一個 wrapper entrypoint，也不擁有 actor／session。
/// 例：在 host entrypoint 或第一次 createWrapper 前呼叫 FodexFunctions.initialize()。
/// AutoOpen 只控制名稱可見性；必須明確 evaluate 各 module 的 builtins，才會宣告 q。
/// Lazy 保證同一組件只接線一次；此處不讀 DB、不 eval q、不建立 ActorSystem。
[<AutoOpen>]
module FodexFunctions =
    let builtins =
        [|
            ReActJsonV301.Definitions.builtins
            ReActJsonV302.Definitions.builtins
            ReActTraceV302.Definitions.builtins
            ReActTopologyFodexFunV302.Definitions.builtins
            ReActCheckpointFodexFunV302.Definitions.builtins
            ReActTopologyFodexFunV301.Definitions.builtins
            ReActTopologyFodexFunV300.Definitions.builtins
            ReActCheckpointFodexFunV301.Definitions.builtins
            ReActCheckpointFodexFunV300.Definitions.builtins
            NativeOutputFodexFunV300.Definitions.builtins
            NativeOutputFodexFunV301.Definitions.builtins
            NativeProcessFodexFunV301.Definitions.builtins
            NativeProcessFodexFunV302.Definitions.builtins
            NativeCommandFodexFunV100.Definitions.builtins
            NativeOwnershipLeaseFodexFunV302.Definitions.builtins
            WrapperCommandFodexFunV300.Definitions.builtins
            ParticipantControlFodexFunV300.Definitions.builtins
            WrapperDraftFodexFunV300.Definitions.builtins
            IAgentFodexFunV200.Definitions.builtins
            IAgentAsyncFodexFunV301.Definitions.builtins
            ReActLoopFodexFunV300.Definitions.builtins
            ReActLoopAsyncFodexFunV301.Definitions.builtins
            ReActLoopTopologyFodexFunV300.Definitions.builtins
            WrapperContextFodexFunV300.Definitions.builtins
            SubagentFodexFunV204.Definitions.builtins
            SubagentFodexFunV206.Definitions.builtins
            ExplicitCotFodexFunV208.Definitions.builtins
            NativeOwnershipFodexFunV209.Definitions.builtins
            NativeSettingsFodexFunV300.Definitions.builtins
            NativeCompactFodexFunV300.Definitions.builtins
            NativeCompactFodexFunV301.Definitions.builtins
            NativeCompactFodexFunV302.Definitions.builtins
            ParticipantListingFodexFunV300.Definitions.builtins
            ParticipantListingFodexFunV301.Definitions.builtins
            NativeModelCatalogFodexFunV300.Definitions.builtins
            NativeModelCatalogFodexFunV301.Definitions.builtins
            NativeAgyModelCatalogFodexFunV300.Definitions.builtins
            NativeAgySettingsFodexFunV300.Definitions.builtins
            SubagentFodexFunV300.Definitions.builtins
            CliFodexFunV100.Definitions.builtins
            RemoteFodexFunV209.Definitions.builtins
            RemoteFodexFunV210.Definitions.builtins
            RemoteControlFodexFunV300.Definitions.builtins
            ConsoleInputFodexFunV300.Definitions.builtins
            ConsoleOutputFodexFunV300.Definitions.builtins
            ConsoleOutputFodexFunV405.Definitions.builtins
            OrderHubFodexFunV301.Definitions.builtins
            SubagentFodexFunV301.Definitions.builtins
            SubagentFodexFunV302.Definitions.builtins
            SubagentFodexFunV303.Definitions.builtins
            SubagentCoordinatorFodexFunV301.Definitions.builtins
            HubHostingFodexFunV301.Definitions.builtins
            FodexFunRegistrationV301.Definitions.builtins
            WrapperCompositionV302.Definitions.builtins
            ContextFodexFunV400.Definitions.builtins
            ReActDataStoreV400.Definitions.builtins
            ReActDataStoreV401.Definitions.builtins
            ReActDataStoreV402.Definitions.builtins
            ReActDataStoreV403.Definitions.builtins
            SharedCacheFodexFunV100.Definitions.builtins
            NativeAgyModelCatalogFodexFunV301.Definitions.builtins
            NativeModelCatalogFodexFunV302.Definitions.builtins
            NativeModelDirectoryFodexFunV100.Definitions.builtins
            NativeSettingsFodexFunV301.Definitions.builtins
            NativeAgySettingsFodexFunV302.Definitions.builtins
            SharedCacheMcpFodexFunV100.Definitions.builtins
            NativeMcpConfigurationFodexFunV100.Definitions.builtins
            NativePreflightFodexFunV100.Definitions.builtins
            NativeModelChangeFodexFunV100.Definitions.builtins
            SharedAssetFodexFunV100.Definitions.builtins
            ServiceProtocolFodexFunV101.Definitions.builtins
            ParticipantHandoverFodexFunV402.Definitions.builtins
            NativeAgyV304.Definitions.builtins
            NativeAppServerV304.Definitions.builtins
            NativeExecV304.Definitions.builtins
            FodexFunRegistrationV409.Definitions.builtins
            WrapperAgentV409.Definitions.builtins
            ParticipantManagerFodexFunV405.Definitions.builtins
            FodexAgentFodexFunV410.Definitions.builtins
            ConsolePolicyFodexFunV410.Definitions.builtins
            ConsoleHostFodexFunV410.Definitions.builtins
            ServiceWrapperFodexFunV105.Definitions.builtins
            NativeInfoFodexFunV100.Definitions.builtins
            ParticipantOperationsFodexFunV405.Definitions.builtins
            ParticipantHandoverFodexFunV400.Definitions.builtins
            ParticipantHandoverFodexFunV401.Definitions.builtins
            ParticipantOperationsFodexFunV403.Definitions.builtins
            ParticipantOperationsFodexFunV404.Definitions.builtins
            ReActLoopHandlerV404.Definitions.builtins
            ReActLoopHandlerV405.Definitions.builtins
            FodexFunRegistrationV408.Definitions.builtins
            ParticipantManagerFodexFunV404.Definitions.builtins
            WrapperAgentV408.Definitions.builtins
            NativeAgySettingsFodexFunV301.Definitions.builtins
            NativeAgyTerminalFodexFunV303.Definitions.builtins
            ParticipantConfigurationFodexFunV403.Definitions.builtins
            FodexFunRegistrationV400.Definitions.builtins
            FodexFunRegistrationV401.Definitions.builtins
            FodexFunRegistrationV406.Definitions.builtins
            FodexFunRegistrationV407.Definitions.builtins
            IAgentRequestFodexFunV401.Definitions.builtins
            HubClientFodexFunV101.Definitions.builtins
            HubClientFodexFunV102.Definitions.builtins
            ParticipantOperationsFodexFunV402.Definitions.builtins
            IAgentRequestFodexFunV402.Definitions.builtins
            ParticipantArgumentsFodexFunV402.Definitions.builtins
            ParticipantConfigurationFodexFunV402.Definitions.builtins
            WrapperAgentV402.Definitions.builtins
            WrapperAgentV405.Definitions.builtins
            WrapperAgentV406.Definitions.builtins
            WrapperAgentV407.Definitions.builtins
            ParticipantManagerFodexFunV402.Definitions.builtins
            ParticipantManagerFodexFunV403.Definitions.builtins
            ReActLoopHandlerV402.Definitions.builtins
            ReActLoopHandlerV403.Definitions.builtins
            HubHostingFodexFunV402.Definitions.builtins
            HubHostingFodexFunV405.Definitions.builtins
            FodexAgentFodexFunV402.Definitions.builtins
            FodexAgentFodexFunV405.Definitions.builtins
            FodexAgentFodexFunV406.Definitions.builtins
            FodexAgentFodexFunV407.Definitions.builtins
            FodexAgentFodexFunV408.Definitions.builtins
            FodexAgentFodexFunV409.Definitions.builtins
            ConsolePolicyFodexFunV403.Definitions.builtins
            ConsolePrinterFodexFunV403.Definitions.builtins
            ConsoleHostFodexFunV403.Definitions.builtins
            ConsolePolicyFodexFunV404.Definitions.builtins
            ConsolePolicyFodexFunV405.Definitions.builtins
            ConsolePolicyFodexFunV406.Definitions.builtins
            ConsolePolicyFodexFunV407.Definitions.builtins
            ConsoleHostFodexFunV404.Definitions.builtins
            ConsoleHostFodexFunV405.Definitions.builtins
            ConsoleHostFodexFunV407.Definitions.builtins
            ConsoleHostFodexFunV408.Definitions.builtins
            ConsoleHostFodexFunV409.Definitions.builtins
            ServiceProtocolFodexFunV100.Definitions.builtins
            ServiceSupervisorFodexFunV100.Definitions.builtins
            ServiceSupervisorFodexFunV101.Definitions.builtins
            ServiceSupervisorFodexFunV102.Definitions.builtins
            ServiceSupervisorFodexFunV103.Definitions.builtins
            ServiceSupervisorFodexFunV104.Definitions.builtins
            ServiceWrapperFodexFunV100.Definitions.builtins
            ServiceWrapperFodexFunV101.Definitions.builtins
            ServiceWrapperFodexFunV102.Definitions.builtins
            ServiceWrapperFodexFunV103.Definitions.builtins
            ServiceWrapperFodexFunV104.Definitions.builtins
            ServiceMcpFodexFunV100.Definitions.builtins
            ServiceMcpFodexFunV101.Definitions.builtins
            ServiceHostFodexFunV100.Definitions.builtins
            ServiceHostFodexFunV101.Definitions.builtins
            ServiceHostFodexFunV102.Definitions.builtins
            ServiceHostFodexFunV103.Definitions.builtins
            ServiceHostFodexFunV104.Definitions.builtins
            ServiceHostFodexFunV105.Definitions.builtins
            ServiceHostFodexFunV106.Definitions.builtins
        |]
        |> Array.concat

    let initialization = lazy (builtins |> Array.iter (fun configure -> configure()))

    /// 不帶 node：Versioned(node,target) 在後續 lookup 時才選擇 node 分區並讀其 SQL 設定。
    /// 重複呼叫不覆寫 registry、不重新註冊或換掉使用中的 pin。
    let initialize () = initialization.Value
