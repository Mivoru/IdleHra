using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// Task 87: the tick thread's Boss Ascension command - start one step of a
    /// boss's ladder. Entirely on the payload: the highest cleared step is the
    /// packed cache (BossAscensionPacked), the boss's defeat is the mask the
    /// first-clear wall already keeps, so there is no scope, no query and no
    /// queue. The reward is paid later, off the tick, when the kill is judged.
    ///
    /// Modul: EVERY REFUSAL IS A RESULT CODE. The region and step come from
    /// buttons, so a locked step is a state race (a stale ladder on a phone) and
    /// not a protocol violation - it answers AscensionStepLocked and the honest
    /// client is never disconnected for it.
    /// </summary>
    internal static class BossAscensionTickCoordinator
    {
        internal static void HandleStartBossAscension(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            int region = (int)cmd.TargetId;
            int step = (int)cmd.SecondaryId;

            var code = Validate(in currentPayload, region, step);
            if (code == CommandResultCode.Success)
            {
                // The activity change FIRST: it disarms whatever was armed and
                // resets the fight, and the boss spawns at the stepped health.
                SimulationEngine.ApplyActivityChangeToPayload(ref currentPayload, RaceUnlockRegistry.GetRegionBossMonsterId(region));
                currentPayload.AscensionStep = (byte)step;
                currentPayload.AscensionRegion = (byte)region;
                currentPayload.AscensionCharacterId = currentPayload.Slot1_CharacterId;
            }

            ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId, (byte)code);
        }

        /// <summary>The whole rule, pure, so a test states it without a tick.</summary>
        internal static CommandResultCode Validate(in TickStatePayload payload, int region, int step)
        {
            if (!BossAscensionRegistry.IsValidRegion(region) || !BossAscensionRegistry.IsValidStep(step))
            {
                return CommandResultCode.GenericValidationFailure;
            }

            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            if (!BossFirstClearRules.IsDefeated(payload.DefeatedRegionBossMask, bossId))
            {
                return CommandResultCode.AscensionBossNotDefeated;
            }

            int highest = BossAscensionRegistry.HighestStepOf(payload.BossAscensionPacked, region);
            if (step > highest + 1)
            {
                return CommandResultCode.AscensionStepLocked;
            }

            return CommandResultCode.Success;
        }
    }
}
