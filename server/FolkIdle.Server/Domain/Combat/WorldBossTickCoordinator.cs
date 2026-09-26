using System.Threading.Tasks;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
using System.Collections.Generic;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The tick thread's World Boss attempt hand-off. See
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class WorldBossTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.WorldBossAttemptUpdateQueue.TryDequeue(out var worldBossAttemptUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, worldBossAttemptUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in worldBossAttemptUpdate);
                }
            }
        }

        /// <summary>
        /// Prices the shield wheel's strikes with A x G from each striker's
        /// payload and hands them to the engine (spec 5.7).
        /// </summary>
        /// <remarks>
        /// Modul: A BUDGET, NOT A DRAIN-TO-EMPTY (CLAUDE.md, unbounded drain).
        /// The depth is read once per tick and only that many orders are taken,
        /// so a burst of strikes cannot hold the tick thread; the rest wait one
        /// tick. Every order taken is answered, including the ones refused here.
        /// </remarks>
        internal static int DrainStrikeOrders(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            WorldBossEngine engine)
        {
            int budget = registry.WorldBossStrikeQueue.Count;
            int taken = 0;
            while (taken < budget && registry.WorldBossStrikeQueue.TryDequeue(out var order))
            {
                taken++;
                try
                {
                    ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, order.PlayerId);
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                    {
                        // Modul: NO SESSION IS PRICED AT THE FLOOR, NEVER REFUSED
                        // (security review, 2026-09-26). This answered Failed -
                        // "nothing spent" - and the challenge was already gone.
                        // So a script could close its socket, throw a spear, read
                        // WeakHit, and discard the run for free until the first
                        // guess was weak: the 6.0x ceiling every day, and every
                        // bad run re-rolled. With no payload there is no attack
                        // power, so the engine prices the base hit at its
                        // 1,000 floor and the strike is SPENT, like any other.
                        // An honest client always has a session here.
                        engine.QueueStrike(order, 0);
                        continue;
                    }
                    if (payload.WorldBossAttemptCount >= WorldBossEngine.MaxAttemptsPerDay)
                    {
                        order.Complete(WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.NoAttemptsLeft));
                        continue;
                    }
                    engine.QueueStrike(order, ServerStrikeDamage(ref payload));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"World boss strike order failed for player {order.PlayerId}: {ex.Message}");
                    order.Complete(WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.Failed));
                }
            }
            return taken;
        }

        /// <summary>
        /// A x G: how hard this player hits the boss before any plate or skill
        /// multiplier, from the payload alone. The one place both opcode 32 and
        /// the shield wheel take it from.
        /// </summary>
        internal static long ServerStrikeDamage(ref TickStatePayload currentPayload)
        {
            // Modul: skill tree, Giantslayer. The most generous
            // branch in the tree - 40% at cap - because the world
            // boss is its own activity on its own timer and cannot
            // reach a region's pacing however large it grows.
            //
            // Applied HERE rather than inside WorldBossEngine: the
            // engine takes a damage figure and has no player state
            // to read a tree level from, and passing the payload in
            // would hand it far more than it needs.
            float giantslayerPct = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BranchWorldBossDamage, currentPayload.Skill_WorldBossDamage);

            // Modul: THE SERVER ANSWERS "how hard does this player
            // hit" ITSELF NOW.
            //
            // This used to read cmd.ClientPredictedDamage - a
            // figure the client computed about its own character
            // and posted, bounded only by a 100,000,000 clamp
            // inside WorldBossEngine. The same number the live tick
            // swings with is already on the payload, cached once per
            // tick, so there was never a reason to ask the client.
            //
            // In whole hit points, because the boss's health pool is
            // whole rather than milli.
            long serverAttack = currentPayload.CachedEffectiveMilliAttack / 1000L;
            if (serverAttack < 1L) serverAttack = 1L;

            return (long)Math.Min(
                uint.MaxValue,
                (double)serverAttack * (1.0 + (giantslayerPct / 100.0)));
        }

        internal static void Apply(ref TickStatePayload payload, in WorldBossAttemptUpdateNotification worldBossAttemptUpdate)
        {
            payload.WorldBossAttemptCount = worldBossAttemptUpdate.AttemptCount;
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.AttackWorldBoss)
        internal static void HandleAttackWorldBoss(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Protocol first: a client this build does not speak to is
            // terminated, exactly as before.
            if (!ClientCommandValidator.ValidateWorldBossAttackRequest(
                ref currentPayload,
                ref cmd,
                WorldBossEngine.ActiveBossInstanceId))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            // Modul: UNDER THE WHEEL, OPCODE 32 IS AN OLD CLIENT, told to update
            // rather than obeyed (spec 4). Its plate press would be a strike
            // priced on the encounter-wide weak plate, which wheel mode no
            // longer keeps, and its answer has no room for the private result.
            if (ctx.WorldBossEngine.MinigameMode == BossMinigameMode.Wheel)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(
                    currentPayload.PlayerId,
                    (byte)FolkIdle.Server.Network.CommandResultCode.WorldBossUpdateRequired);
                return;
            }

            // Modul: then state, which is ANSWERED rather than punished (task
            // 25). A window that closed a moment ago or a boss that died a
            // moment ago is a race an honest client loses, and it used to cost
            // the player their session.
            var stateRefusal = ClientCommandValidator.WorldBossStateRefusal(
                ref currentPayload,
                ctx.WorldBossEngine.IsBossDead(),
                ctx.WorldBossEngine.IsEventActive);
            if (stateRefusal.HasValue)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId, (byte)stateRefusal.Value);
                return;
            }

            // Modul: A SPENT BUDGET IS REFUSED HERE, IN MEMORY. Opcode 32 left
            // the 100 ms double-tap rule in task 25, so without this every
            // press after the third was a Task.Run holding a pooled connection
            // in a Serializable transaction on the one row every player
            // contends for - enough spam fills the bounded pool and starves
            // the loot and checkpoint workers behind it. The payload count is
            // reset to 0 for every online player when a window opens AND at
            // every UTC midnight (one strike a day, LiveOps), and the
            // engine still enforces the cap inside its transaction.
            if (currentPayload.WorldBossAttemptCount >= WorldBossEngine.MaxAttemptsPerDay)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(
                    currentPayload.PlayerId,
                    (byte)FolkIdle.Server.Network.CommandResultCode.WorldBossNoAttemptsLeft);
                return;
            }

            uint bossDamage = (uint)ServerStrikeDamage(ref currentPayload);

            ctx.WorldBossEngine.QueueAttack(
                currentPayload.PlayerId,
                cmd.TargetedBossId,
                bossDamage,
                cmd.TargetedPlateIndex);
        }
    }
}
