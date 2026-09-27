using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>What the gate decided about one command.</summary>
    internal enum CommandGateVerdict
    {
        /// <summary>Hand the command to its handler.</summary>
        Proceed,

        /// <summary>Caller must TerminateSessionForSecurity and skip the command.</summary>
        Terminate,

        /// <summary>Caller must RequestShadowBan(playerId, 54, 2) and skip the command.</summary>
        ShadowBan,

        /// <summary>
        /// The client acted on a state that is several checkpoints old. Caller
        /// skips the command and answers StaleClientState - it does NOT end the
        /// session (see the epoch gate below).
        /// </summary>
        RefuseStale
    }

    /// <summary>
    /// The anti-cheat and epoch gate every non-internal client command passes
    /// through, extracted verbatim from SimulationEngine.EngineLoop.
    ///
    /// Modul: THE ORDER OF THESE FOUR CHECKS IS THE CONTRACT, and until
    /// CommandGateOrderingTests was written nothing in the repository pinned
    /// it - it was enforced only by the physical order of four `if` statements
    /// inside a very long loop body. Validators take the payload by ref and
    /// may mutate it (ValidateCommand stamps LastCommandTimestamp), so
    /// reordering them is not merely a different error message: it is a
    /// different resulting payload.
    ///
    /// The verdict is returned rather than acted on because terminating a
    /// session and requesting a shadow ban are both SimulationEngine's own
    /// instance work (they touch _activePlayers, _networkSystem and the
    /// anti-cheat engine). A gate that could end sessions itself would be a
    /// second place in the server with that power.
    ///
    /// Called synchronously on the tick thread only; owns no state.
    /// </summary>
    internal static class CommandGate
    {
        internal static CommandGateVerdict Evaluate(ref TickStatePayload currentPayload, ref ClientCommandPacket cmd)
        {
            // Server-generated packets carry no client epoch - see
            // IsServerInternalCommand for why Logout is one of them.
            bool isInternalCommand = SimulationEngine.IsServerInternalCommand(cmd.Command);

            // Modul: THE ANTI-CHEAT'S "WAS THIS CLIENT TALKING" STAMP,
            // AND IT WAS WRITTEN IN ONE PLACE THAT IS NOT THIS ONE.
            //
            // The challenge-miss branch in ProcessAccountTick only
            // counts a miss against a client that was otherwise sending
            // commands during the window - the whole point being that a
            // silent client is a BACKGROUNDED one, not a cheating one,
            // which this codebase learned by quarantining a real player
            // twice. That guard reads LastClientCommandAtMs.
            //
            // It was stamped only inside the activity-change drain. So
            // a player who equipped, spent, chatted, bought or fought
            // for ten minutes without changing activity read as SILENT,
            // and a player who changed activity once and then locked
            // their phone read as TALKING - the exact inversion of what
            // the guard is for. It failed open far more often than
            // closed, so nobody was banned by it; it simply was not the
            // check its own comment describes.
            //
            // Stamped here instead, where every genuine client command
            // arrives. Internal commands are excluded deliberately:
            // ReloadState and Logout are enqueued by the SERVER, and
            // counting them as the client talking would make a silent
            // client look busy at exactly the wrong moment.
            if (!isInternalCommand)
            {
                currentPayload.LastClientCommandAtMs = Environment.TickCount64;
            }

            // Epoch interception gate: reject commands from desynchronized clients.
            //
            // Modul: commands 47 and 48 used to be exempt here, because
            // for those two LogicEpochCounter carried UNIX seconds
            // rather than the save-generation counter. Both commands are
            // gone, so the exemption is too - and with it the only place
            // where that field meant two different things.
            //
            // Modul: A STALE EPOCH IS A STATE RACE, NOT A PROTOCOL VIOLATION
            // (2026-09-27). This used to Terminate. A phone suspends its
            // JavaScript in the background while the socket and the server's
            // checkpoints carry on; on resume it replays buffered frames and
            // acts before the fresh one lands, more than EpochDriftTolerance
            // checkpoints behind. The owner's account alone had 556 of these,
            // every other player 1 - each one a kick whose close reason says
            // "token", which the client reads as signed out, so it refreshed
            // and restarted the session: "the phone relogs a few seconds after
            // login and my offline drops are gone". Refusing still keeps a
            // desynchronized command from ever running; ending the session
            // added nothing but the lie. CLAUDE.md: terminate a protocol
            // violation, answer a state race.
            //
            // The challenge answer is exempt: JudgeAntiCheatChallengeResponse
            // binds it to ActiveChallengeIssuedEpoch itself, and the client
            // answers with the epoch of the frame that ASKED - which on resume
            // is exactly an old one.
            if (!isInternalCommand
                && cmd.Command != CommandType.AntiCheatChallengeResponse
                && !ClientCommandValidator.ValidateEpochSynchronization(ref currentPayload, ref cmd))
            {
                return CommandGateVerdict.RefuseStale;
            }

            if (!isInternalCommand && !ClientCommandValidator.ValidateCommand(ref currentPayload, (byte)cmd.Command))
            {
                return CommandGateVerdict.Terminate;
            }

            if (!isInternalCommand && !ClientCommandValidator.ValidateNoAntiCheatPayload(ref currentPayload, ref cmd))
            {
                return CommandGateVerdict.ShadowBan;
            }

            if (!isInternalCommand && !ClientCommandValidator.ValidateNoPushCompliancePayload(ref currentPayload, ref cmd))
            {
                return CommandGateVerdict.Terminate;
            }

            return CommandGateVerdict.Proceed;
        }
    }
}
