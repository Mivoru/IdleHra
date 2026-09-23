using System.Threading.Tasks;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

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

        internal static void Apply(ref TickStatePayload payload, in WorldBossAttemptUpdateNotification worldBossAttemptUpdate)
        {
            payload.WorldBossAttemptCount = worldBossAttemptUpdate.AttemptCount;
            payload.WorldBossSessionEndsEpoch = worldBossAttemptUpdate.SessionEndsEpoch;
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.AttackWorldBoss)
        internal static void HandleAttackWorldBoss(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateWorldBossAttackRequest(
                ref currentPayload,
                ref cmd,
                WorldBossEngine.ActiveBossInstanceId,
                ctx.WorldBossEngine.IsBossDead(),
                ctx.WorldBossEngine.IsEventActive))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            // Modul 06/15: Auto-Eat food depletion also closes a
            // player's World Boss battle session, alongside the
            // 300-second cap enforced inside WorldBossEngine itself.
            bool attackAutoEatDepleted = currentPayload.Food1_Count <= 0 && currentPayload.Food2_Count <= 0 && currentPayload.Food3_Count <= 0;

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

            uint bossDamage = (uint)Math.Min(
                uint.MaxValue,
                (double)serverAttack * (1.0 + (giantslayerPct / 100.0)));

            ctx.WorldBossEngine.QueueAttack(
                currentPayload.PlayerId,
                cmd.TargetedBossId,
                bossDamage,
                cmd.TargetedPlateIndex,
                attackAutoEatDepleted);
        }
    }
}
