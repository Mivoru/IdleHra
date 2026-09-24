using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The tick thread's guild war and guild raid commands: war supply, defence registration, shard attacks, guild combat turns and raid launch.
    ///
    /// Modul: a COORDINATOR, not an engine - a static class with no fields,
    /// called synchronously on the 10Hz tick thread by SimulationEngine's
    /// command dispatch table after CommandGate has said Proceed. It owns no
    /// thread, timer or state; anything asynchronous goes through the
    /// SafeDispatch delegate it is handed, never a task it starts itself.
    /// </summary>
    internal static class GuildWarTickCoordinator
    {
        /// <summary>
        /// True when the command must stop here because Guild Wars are still
        /// locked - having told the player why.
        ///
        /// Modul: the population lock (GuildWarUnlock), checked FIRST, before
        /// any validator. Two of these handlers used to answer a malformed or
        /// stale request by disconnecting, and a locked feature is neither: the
        /// player gets GuildWarsLocked on the result ring and stays connected.
        /// A null engine counts as locked - a war path with no war engine
        /// behind it has nothing to act with.
        /// </summary>
        internal static bool RefuseWhileLocked(ref TickStatePayload currentPayload, in CommandCoordinatorContext ctx)
        {
            if (ctx.GuildWarEngine != null && ctx.GuildWarEngine.Unlock.IsUnlocked)
            {
                return false;
            }

            ctx.PlayerRegistry?.EnqueueCommandResult(currentPayload.PlayerId, (byte)CommandResultCode.GuildWarsLocked);
            return true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ContributeToWarSupply)
        internal static void HandleContributeToWarSupply(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (RefuseWhileLocked(ref currentPayload, in ctx)) return;

            if (currentPayload.GuildId > 0 && currentPayload.ActiveGuildWarId > 0 && cmd.SecondaryId > 0 && cmd.TertiaryId > 0)
            {
                currentPayload.IsSuspended = true;
                ctx.CheckpointManager.FlushStateAndAdvance(ref currentPayload);
                ctx.GuildWarEngine.SupplyChainQueue.Enqueue(new GuildWarSupplyContribution
                {
                    PlayerId = currentPayload.PlayerId,
                    CommodityId = cmd.SecondaryId,
                    QuantityToBurn = cmd.TertiaryId
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RegisterGuildDefense)
        internal static void HandleRegisterGuildDefense(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (RefuseWhileLocked(ref currentPayload, in ctx)) return;

            if (!ClientCommandValidator.ValidateGuildWarAction(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            // Dispatched off-thread like every other database
            // command. This previously ran as
            // RegisterGuildDefenseAsync(...).GetAwaiter().GetResult(),
            // which blocked the 10 Hz tick - for EVERY player - on a
            // Serializable transaction taking two FOR UPDATE row
            // locks. UiGuildWarPanel sends this from a button, so any
            // player could stall the whole simulation for as long as
            // those locks took to acquire, and blocking the tick
            // thread while EF holds locks is a deadlock shape as well
            // as a latency one.
            //
            // Safe to fire and forget: it returns nothing and mutates
            // no payload state, so there is no result to thread back
            // through a notification queue.
            long guildDefenseGuildId = currentPayload.GuildId;
            var registerGuildDefense = ctx.RegisterGuildDefense;
            ctx.SafeDispatch("GuildWar.RegisterDefense", currentPayload.PlayerId, async () =>
            {
                await registerGuildDefense(guildDefenseGuildId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.SubmitShardAttack)
        internal static void HandleSubmitShardAttack(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (RefuseWhileLocked(ref currentPayload, in ctx)) return;

            if (!ClientCommandValidator.ValidateGuildWarAction(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            // Dispatched off-thread, result threaded back through
            // ShardAttackResultQueue and applied at the drain below.
            //
            // This was the last GetAwaiter().GetResult() in the tick
            // loop: a cross-shard network round trip executed
            // synchronously, which would have stalled every player's
            // simulation on one player's request. It could not follow
            // the plain fire-and-forget shape the other commands use,
            // because it writes three fields back into the payload -
            // and only the tick thread may touch a payload.
            //
            // The security-violation statuses (1, 2, 4) are carried
            // back rather than acted on in the lambda for the same
            // reason: TerminateSessionForSecurity mutates tick-owned
            // state, so the drain performs it.
            long shardPlayerId = currentPayload.PlayerId;
            long shardGuildId = currentPayload.GuildId;
            long shardNodeHp = currentPayload.GlobalNodeRemainingHp;
            System.Guid shardMatchUuid = cmd.TargetMatchUuid;
            uint shardPredictedDamage = cmd.ClientPredictedDamage;
            bool shardIsFinalBlow = cmd.IsBuy != 0;

            var playerRegistry = ctx.PlayerRegistry;
            var submitShardAttack = ctx.SubmitShardAttack;
            ctx.SafeDispatch("GuildWar.SubmitShardAttack", shardPlayerId, async () =>
            {
                var attackResult = await submitShardAttack(
                    shardGuildId,
                    shardNodeHp,
                    shardMatchUuid,
                    shardPredictedDamage,
                    shardIsFinalBlow);

                playerRegistry.ShardAttackResultQueue.Enqueue(new ShardAttackResultNotification
                {
                    PlayerId = shardPlayerId,
                    ProcessingStatus = attackResult.Response.ProcessingStatus,
                    MatchUuid = shardMatchUuid,
                    GlobalNodeRemainingHp = attackResult.Response.GlobalNodeRemainingHp,
                    ActiveMatchMmr = attackResult.ActiveMatchMmr
                });
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.LaunchGuildRaid)
        internal static void HandleLaunchGuildRaid(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long raidGuildId = currentPayload.GuildId;
            long raidRequestingPlayerId = currentPayload.PlayerId;
            if (raidGuildId > 0 && ctx.GuildRaidEngine != null)
            {
                // No single player to disconnect on failure here -
                // raidGuildId identifies a guild, not a player, and
                // passing it as playerIdToDisconnectOnFailure would
                // force-disconnect whichever unrelated player, if
                // any, happens to share that numeric id. Leader-only
                // enforcement happens inside TryStartRaidAsync
                // itself, against the locked GuildMembers row - a
                // non-leader's request simply rolls back with no
                // effect, matching every other rejected-command
                // path in this engine.
                var guildRaidEngine = ctx.GuildRaidEngine;
                ctx.SafeDispatch("Guild.LaunchRaid", 0L, async () => {
                    await guildRaidEngine.TryStartRaidAsync(raidGuildId, raidRequestingPlayerId);
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ExecuteCombatTurn)
        internal static void HandleExecuteCombatTurn(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (RefuseWhileLocked(ref currentPayload, in ctx)) return;

            if (!ClientCommandValidator.ValidateCombatTurnRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            long guildId = currentPayload.GuildId;
            ClientCommandPacket capturedCommand = cmd;

            var guildCombatSimulationEngine = ctx.GuildCombatSimulationEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("GuildCombat.ExecuteTurn", pId, async () => {
                var result = await guildCombatSimulationEngine.ExecuteCombatTurnAsync(pId, guildId, capturedCommand);
                if (result == GuildCombatTurnResult.InvalidRequest || result == GuildCombatTurnResult.NotFound)
                {
                    networkSystem.PurgeTokensForPlayer(pId);
                    networkSystem.ForceDisconnect(pId);
                }
            });
        }
    }
}
