using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// Everything a command handler needs that is not the payload or the
    /// packet.
    ///
    /// Modul: a readonly REF struct, on purpose. It never escapes the tick
    /// thread's stack, it cannot be captured into a lambda or stored on a
    /// field, and it therefore cannot become a back door through which a
    /// coordinator reaches SimulationEngine's state from somewhere other than
    /// the tick thread. The flip side is visible in every handler: a lambda
    /// handed to SafeDispatch cannot read ctx, so each handler copies the one
    /// engine its lambda needs into a local first. Every engine field on
    /// SimulationEngine is readonly, so reading it at command time rather than
    /// when the lambda runs is the same value.
    ///
    /// Built once per dequeued command that has a table entry (a stack-only
    /// construction, no allocation), from delegates SimulationEngine caches
    /// once in its constructor.
    ///
    /// The session-ending members are the three PRIMITIVES the old branches
    /// called, not one combined "reject" - because the branches combine them
    /// three different ways (RemoveActivePlayer + ForceDisconnect; the same
    /// plus PurgeTokensForPlayer; TerminateSessionForSecurity), and each
    /// branch's own choice is transcribed as it was.
    /// </summary>
    internal readonly ref struct CommandCoordinatorContext
    {
        internal long RoutingPlayerId { get; init; }
        internal Action<string, long, Func<Task>> SafeDispatch { get; init; }
        internal Action<long> TerminateSessionForSecurity { get; init; }
        internal Action<long> RemoveActivePlayer { get; init; }
        internal NetworkBroadcastSystem NetworkSystem { get; init; }
        internal PlayerSessionRegistry PlayerRegistry { get; init; }

        // Domain engine handles, added one at a time by the task whose
        // handlers need each.
        internal LegacyStoreEngine LegacyStoreEngine { get; init; }
        internal StateCheckpointManager CheckpointManager { get; init; }
        internal MarketEscrowEngine EscrowEngine { get; init; }
        internal MarketOrderBookEngine MarketEngine { get; init; }
    }

    /// <summary>
    /// One command's handler. Runs synchronously on the tick thread, after
    /// CommandGate has said Proceed. Returning is the old branch's
    /// `continue` - nothing followed the dispatch chain in the loop body.
    /// </summary>
    internal delegate void CommandHandler(
        ref TickStatePayload currentPayload,
        ref ClientCommandPacket cmd,
        in CommandCoordinatorContext ctx);
}
