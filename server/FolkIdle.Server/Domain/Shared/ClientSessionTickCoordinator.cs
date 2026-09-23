using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// The tick thread's client-session commands: telemetry, diagnostics, push registration, GDPR purge, language, UI context and simulation speed.
    ///
    /// Modul: a COORDINATOR, not an engine - a static class with no fields,
    /// called synchronously on the 10Hz tick thread by SimulationEngine's
    /// command dispatch table after CommandGate has said Proceed. It owns no
    /// thread, timer or state; anything asynchronous goes through the
    /// SafeDispatch delegate it is handed, never a task it starts itself.
    /// </summary>
    internal static class ClientSessionTickCoordinator
    {
        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ReportTelemetryBurst)
        internal static void HandleReportTelemetryBurst(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateTelemetryBurst(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            ctx.TelemetryStreamingEngine.EnqueueClientTelemetryBurst(currentPayload.AccountId, currentPayload.PlayerId, cmd);
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.PingNetworkDiagnostics)
        internal static void HandlePingNetworkDiagnostics(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidatePingNetworkDiagnostics(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }
            
            currentPayload.NetworkDiagnosticsToken = cmd.NetworkDiagnosticsToken;
            currentPayload.IsDirty = true;
            return;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RegisterPushToken)
        internal static void HandleRegisterPushToken(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateDeviceRegistrationRequest(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            byte[] deviceToken = SimulationEngine.CopyDeviceTokenBytes(ref cmd);
            ctx.PushNotificationTriggerEngine.QueueDeviceRegistration(currentPayload.PlayerId, deviceToken, cmd.TargetPlatformFamily);
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.TriggerGdprPurge)
        internal static void HandleTriggerGdprPurge(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateGdprPurgeRequest(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            ctx.CompliancePurgeEngine.QueueGdprPurge(currentPayload.PlayerId);
            ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.SwitchLanguage)
        internal static void HandleSwitchLanguage(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateLanguageSwitchRequest(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            currentPayload.ActiveLanguageState = cmd.TargetLanguageId;
            currentPayload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ReportUiContextSwitch)
        internal static void HandleReportUiContextSwitch(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            currentPayload.ActiveUiContextBitmask = cmd.ActiveUiContextBitmask;
            currentPayload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.SetSimulationSpeed)
        internal static void HandleSetSimulationSpeed(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            int requestedMultiplier = (int)cmd.TargetId;
            if (requestedMultiplier == 1 || requestedMultiplier == 2 || requestedMultiplier == 4)
            {
                if (currentPayload.AccumulatedTimeBankMs > 0)
                {
                    currentPayload.SpeedMultiplier = requestedMultiplier;
                    currentPayload.IsDirty = true;
                }
                else if (requestedMultiplier == 1)
                {
                    currentPayload.SpeedMultiplier = 1;
                    currentPayload.IsDirty = true;
                }
            }
        }
    }
}
