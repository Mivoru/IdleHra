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
        // Adds playerId to _guildMembersIndex[guildId] - called only at
        // session-start (login, benchmark injection), never per tick, so the
        // occasional List<long> allocation on a guild's first active member
        // is outside the zero-allocation 10 Hz tick constraint (that
        // constraint applies to the four read/dequeue loops below, which
        // only ever iterate an already-allocated list).
        private void AddToGuildIndex(long guildId, long playerId)
        {
            if (guildId <= 0) return;

            if (!_guildMembersIndex.TryGetValue(guildId, out var members))
            {
                members = new System.Collections.Generic.List<long>();
                _guildMembersIndex[guildId] = members;
            }

            if (!members.Contains(playerId))
            {
                members.Add(playerId);
            }
        }

        // Removes playerId from _guildMembersIndex[guildId] - called only at
        // session-end (disconnect, security termination, validation-failure
        // eviction), never per tick.
        private void RemoveFromGuildIndex(long guildId, long playerId)
        {
            if (guildId <= 0) return;

            if (_guildMembersIndex.TryGetValue(guildId, out var members))
            {
                members.Remove(playerId);
                if (members.Count == 0)
                {
                    _guildMembersIndex.Remove(guildId);
                }
            }
        }

        internal bool IsPlayerInGuildIndex(long guildId, long playerId)
        {
            lock (_activePlayers)
            {
                return _guildMembersIndex.TryGetValue(guildId, out var members) && members.Contains(playerId);
            }
        }

        // Modul: Deploy activation fix. Test-only observability for the
        // ActivityChangeQueue drain - the live payload is tick-thread owned,
        // so a test cannot read it directly and must poll through here,
        // tolerating a tick's worth of latency.
        internal long GetActivePlayerActiveActivityId(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.ActiveActivityId : -1L;
            }
        }

        internal int GetActivePlayerCurrentMonsterId(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.CurrentMonsterId : -1;
            }
        }

        // Modul: Guild War scoreboard sync. Test-only observability for the
        // GuildWarScoreboardQueue drain, same tick-thread-ownership reason as
        // the two hooks above.
        internal int GetActivePlayerGuildCombatVanguardPoints(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.GuildCombatVanguardPoints : -1;
            }
        }

        internal long GetActivePlayerGuildId(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.GuildId : -1;
            }
        }

        // Test-only observability for tick-thread exception isolation:
        // GatheringProgressTicks is a simple, RNG-free, monotonically
        // increasing counter while a gathering activity is active, making
        // it a clean proxy for "the tick thread is still alive and still
        // processing this specific player" across repeated real ticks.
        internal int GetActivePlayerGatheringProgressTicks(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.GatheringProgressTicks : -1;
            }
        }

        // Test-only: the world boss attempt count as the player's packet will carry it.
        internal int GetActivePlayerWorldBossAttemptCount(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.WorldBossAttemptCount : -1;
            }
        }

        internal int GetActivePlayerTicksSinceLastFlush(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) ? payload.TicksSinceLastFlush : -1;
            }
        }

        internal bool IsActivePlayerPresent(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.ContainsKey(playerId);
            }
        }

        // Test-only observability (via InternalsVisibleTo) for the
        // Full-Stack Production Hardening Phase 3, Part 1 session-registry
        // leak fix - proves RemoveActivePlayer actually clears
        // _liveSessionContexts on every disconnect path, not just
        // TerminateSessionForSecurity's old explicit call.
        internal bool IsLiveSessionContextPresent(long playerId)
        {
            return _liveSessionContexts.ContainsKey(playerId);
        }

        // Test-only observability (via InternalsVisibleTo) for the Part 5
        // command-result ring buffer - returns all 4 slots (not just the
        // newest, unlike GetActivePlayerLastCommandResultCode) so a test
        // can assert every buffered rejection survived, in what order,
        // and which slot a wraparound append overwrote. Allocates a small
        // array - fine here since this is test-only diagnostic code, never
        // reachable from the 10Hz tick hot path itself.
        internal (byte code, uint tick)[] GetActivePlayerCommandResultSlots(long playerId)
        {
            lock (_activePlayers)
            {
                if (!_activePlayers.TryGetValue(playerId, out var payload))
                {
                    return Array.Empty<(byte, uint)>();
                }

                return new (byte, uint)[]
                {
                    (payload.CommandResultSlot0.ResultCode, payload.CommandResultSlot0.ResultTick),
                    (payload.CommandResultSlot1.ResultCode, payload.CommandResultSlot1.ResultTick),
                    (payload.CommandResultSlot2.ResultCode, payload.CommandResultSlot2.ResultTick),
                    (payload.CommandResultSlot3.ResultCode, payload.CommandResultSlot3.ResultTick)
                };
            }
        }

        internal bool IsActivePlayerSuspended(long playerId)
        {
            lock (_activePlayers)
            {
                return _activePlayers.TryGetValue(playerId, out var payload) && payload.IsSuspended;
            }
        }

        internal int GetActivePlayerLastCommandResultCode(long playerId)
        {
            lock (_activePlayers)
            {
                if (!_activePlayers.TryGetValue(playerId, out var payload))
                {
                    return -1;
                }

                // Modul: reports whichever of the 4 ring-buffer slots holds
                // the highest ResultTick (the most recently appended entry) -
                // preserves this helper's original "most recent rejection"
                // semantics now that a scalar no longer exists.
                CommandResultEntry newest = payload.CommandResultSlot0;
                if (payload.CommandResultSlot1.ResultTick > newest.ResultTick) newest = payload.CommandResultSlot1;
                if (payload.CommandResultSlot2.ResultTick > newest.ResultTick) newest = payload.CommandResultSlot2;
                if (payload.CommandResultSlot3.ResultTick > newest.ResultTick) newest = payload.CommandResultSlot3;
                return newest.ResultCode;
            }
        }

        // Modul: single entry point for adding a player to _activePlayers -
        // keeps _guildMembersIndex synchronized so no add site can forget
        // the index update. See RemoveActivePlayer for the matching removal
        // path.
        private void AddActivePlayer(TickStatePayload payload)
        {
            // Modul: STAGGERED CHECKPOINT BOUNDARIES (task 43, 2c). Every
            // session used to start its five-minute clock at 0, so a reconnect
            // wave - a deploy, a network blip - put every player on the same
            // boundary tick, and every checkpoint in the game was queued at
            // once, every five minutes, for ever. A fixed per-player phase
            // spreads them over the whole window. A payload arriving with a
            // counter already set (a forced boundary, a test) keeps it.
            if (payload.TicksSinceLastFlush == 0)
            {
                payload.TicksSinceLastFlush = StateCheckpointManager.StaggeredStartTicks(payload.PlayerId);
            }
            _activePlayers[payload.PlayerId] = payload;
            AddToGuildIndex(payload.GuildId, payload.PlayerId);

            // Modul: caches this player's GuildId directly on their
            // WebSocketSession so NetworkBroadcastSystem can route guild-
            // channel chat messages (BroadcastGuildChatMessage) without
            // needing a reference back into this class's own
            // _guildMembersIndex - see ChatEngine/NetworkBroadcastSystem's
            // own comments for why a per-session cached value was chosen
            // over that alternative.
            _networkSystem.UpdateSessionGuildId(payload.PlayerId, payload.GuildId);
        }

        // Modul: single entry point for removing a player from
        // _activePlayers - replaces every bare _activePlayers.Remove(id)
        // call in this file so _guildMembersIndex cannot drift out of sync
        // with _activePlayers (a player left in the guild index after
        // disconnect would keep receiving guild broadcast writes into a
        // TickStatePayload that no longer exists in _activePlayers, which
        // GetValueRefOrNullRef already guards against, but would still leak
        // the index entry itself indefinitely).
        //
        // Modul: Full-Stack Production Hardening Phase 3, Part 1. Also the
        // single authoritative choke point for _liveSessionContexts and
        // PlayerSessionRegistry cleanup - previously only
        // TerminateSessionForSecurity cleared _liveSessionContexts, so a
        // normal Logout leaked one entry per session forever (unbounded
        // growth with total historical logins, not concurrent players).
        // Worse, every one of the ~21 anti-cheat/validation-failure sites
        // called RemoveActivePlayer directly (never TerminateSessionForSecurity),
        // which removed the _activePlayers entry immediately - so the
        // deferred Logout command NetworkBroadcastSystem's socket-closure
        // finally block enqueues afterward was silently dropped by the
        // command loop's null-ref guard (_activePlayers no longer has the
        // entry), meaning _playerRegistry.UnregisterPlayer never ran for a
        // kicked player and PlayerSessionRegistry.IsPlayerOnline reported
        // them online forever. Folding both removals in here, on the same
        // O(1) ConcurrentDictionary operations, closes every disconnect
        // path (clean logout, anti-cheat kick, crash-detected-via-Logout)
        // through one place - no call site can bypass it.
        private void RemoveActivePlayer(long playerId)
        {
            if (_activePlayers.TryGetValue(playerId, out var payload))
            {
                RemoveFromGuildIndex(payload.GuildId, playerId);

                // Modul: the last minute of village production goes with the
                // session rather than with the payload - every disconnect path
                // comes through here (see above), and the offline window that
                // takes over from here starts at the logout stamp.
                EnqueuePendingVillageProduction(ref payload);
            }
            _activePlayers.Remove(playerId);
            _liveSessionContexts.TryRemove(playerId, out _);
            _playerRegistry.UnregisterPlayer(playerId);

            // Modul: broadcast dirty-checking. Folded in here for exactly the
            // reason this method exists: the last-sent-packet cache is one more
            // per-session structure that would otherwise grow with total
            // historical logins rather than concurrent players. Dropping it
            // also guarantees a reconnecting client gets a full snapshot
            // immediately instead of being compared against state it no longer
            // holds.
            RemoveBroadcastCacheEntry(playerId);

            // Modul: anti-cheat false positive. Command-timing profiles used to
            // outlive the session that produced them, so the ring buffer mixed
            // this session's cadence with the last one's and treated the offline
            // gap between them as a normal interval.
            _antiCheatTelemetryEngine?.ForgetPlayer(playerId);
        }

        public void InjectVirtualPlayer(TickStatePayload payload)
        {
            lock (_activePlayers)
            {
                AddActivePlayer(payload);
                _liveSessionContexts.TryAdd(payload.PlayerId, new LiveSessionContext(payload.PlayerId, payload.AccountId));
            }
        }

        public void InjectBenchmarkCommand(long playerId, ClientCommandPacket packet)
        {
            _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = playerId, Packet = packet });
        }
    }
}
