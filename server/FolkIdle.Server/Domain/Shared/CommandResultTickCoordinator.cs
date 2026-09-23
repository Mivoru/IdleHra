using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// The tick thread's generic client error-feedback channel drain - see
    /// CommandResultNotification's own comment. Zero-allocation: pure struct
    /// field writes against an already-resolved ref into _activePlayers,
    /// matching the guild-membership drain immediately above it in
    /// EngineLoop.
    ///
    /// Modul: Full-Stack Production Hardening Phase 3, Part 5.
    /// Appends into the 4-slot ring buffer instead of
    /// overwriting a single scalar - the previous single-slot
    /// design meant a client that missed one broadcast (e.g.
    /// across a reconnect gap) while two or more commands were
    /// rejected back to back would only ever see the last one,
    /// silently losing the earlier rejection's feedback. The
    /// ring-buffer append itself must happen here on the tick
    /// thread (not inside PlayerSessionRegistry.EnqueueCommandResult,
    /// which runs on arbitrary background SafeDispatchAsync
    /// threads and has no safe ref access to TickStatePayload) -
    /// CommandResultTickCounter is a per-player monotonically
    /// increasing counter, never reset, so the client can always
    /// tell which slots are newer than what it has already
    /// displayed and in what order to apply them.
    /// </summary>
    internal static class CommandResultTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.CommandResultQueue.TryDequeue(out var commandResult))
            {
                ref var resultPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, commandResult.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref resultPayload))
                {
                    Apply(ref resultPayload, in commandResult);
                }
            }
        }

        internal static void Apply(ref TickStatePayload resultPayload, in CommandResultNotification commandResult)
        {
            unchecked { resultPayload.CommandResultTickCounter++; }
            var newEntry = new CommandResultEntry { ResultCode = commandResult.ResultCode, ResultTick = resultPayload.CommandResultTickCounter };
            switch (resultPayload.CommandResultRingWriteIndex)
            {
                case 0: resultPayload.CommandResultSlot0 = newEntry; break;
                case 1: resultPayload.CommandResultSlot1 = newEntry; break;
                case 2: resultPayload.CommandResultSlot2 = newEntry; break;
                default: resultPayload.CommandResultSlot3 = newEntry; break;
            }
            resultPayload.CommandResultRingWriteIndex = (byte)((resultPayload.CommandResultRingWriteIndex + 1) & 3);
            resultPayload.IsDirty = true;
        }
    }
}
