using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: moved out of SimulationEngine.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md).
    public partial class SimulationEngine
    {
        // True when this packet differs from the last one sent to this player,
        // or when the keepalive interval has elapsed. Records what it approves
        // so the next call compares against it.
        private bool ShouldSendStateUpdate(long playerId, ref Network.StateUpdatePacket packet)
        {
            long currentTick = _metrics.TotalTicksProcessed;

            if (_lastBroadcastByPlayer.TryGetValue(playerId, out LastBroadcast previous))
            {
                if (!ShouldDispatchStateUpdate(ref packet, ref previous.Packet, currentTick - previous.TickIndex))
                {
                    return false;
                }
            }

            _lastBroadcastByPlayer[playerId] = new LastBroadcast { Packet = packet, TickIndex = currentTick };
            return true;
        }

        // The decision itself, as a pure function of the two packets and how
        // long it has been since the last send.
        //
        // Split out from the dictionary bookkeeping above so it can be tested
        // directly: the keepalive in particular is the kind of thing that
        // silently does not fire and is invisible until a client's
        // interpolation buffer starves in production.
        public static bool ShouldDispatchStateUpdate(
            ref Network.StateUpdatePacket current,
            ref Network.StateUpdatePacket lastSent,
            long ticksSinceLastSend)
        {
            if (ticksSinceLastSend >= BroadcastKeepaliveTicks)
            {
                return true;
            }

            return !StateUpdatePacketsAreEquivalent(ref current, ref lastSent);
        }

        // Byte-compares two packets while ignoring TicksSinceLastFlush.
        //
        // That field increments EVERY tick by design (it is the client's
        // data-staleness indicator), so a naive comparison would find every
        // packet different and the dirty check would save nothing at all. It is
        // normalised to zero on both sides rather than excluded by offset,
        // because an offset-based skip would silently break the moment anyone
        // reorders the struct.
        internal static bool StateUpdatePacketsAreEquivalent(ref Network.StateUpdatePacket left, ref Network.StateUpdatePacket right)
        {
            Network.StateUpdatePacket normalizedLeft = left;
            Network.StateUpdatePacket normalizedRight = right;
            normalizedLeft.TicksSinceLastFlush = 0;
            normalizedRight.TicksSinceLastFlush = 0;

            ReadOnlySpan<byte> leftBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref normalizedLeft, 1));
            ReadOnlySpan<byte> rightBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref normalizedRight, 1));

            return leftBytes.SequenceEqual(rightBytes);
        }

        // Modul: broadcast dirty-checking. Called when a session ends so the
        // cache does not accumulate an entry per player who has ever connected.
        // Also forces the next packet after a reconnect to be a full send,
        // which is correct: a returning client has no snapshot at all.
        public void RemoveBroadcastCacheEntry(long playerId)
        {
            _lastBroadcastByPlayer.Remove(playerId);
        }
    }
}
