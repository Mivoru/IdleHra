using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The tick's half of the seasonal boss: StartSeasonalBoss arms a tier
    /// (entirely on the payload, as StartBossAscension does), and a first
    /// clear noted by the kill is handed to SeasonalBossEngine off the tick.
    /// Every refusal is a result code - the tier is a button.
    /// </summary>
    internal static class SeasonalBossTickCoordinator
    {
        internal static void HandleStart(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            int tier = (int)cmd.TargetId;
            int eventId = (int)cmd.SecondaryId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var code = SeasonalBossRegistry.Validate(in currentPayload, tier, eventId, now);
            int bossId = SeasonalBossRegistry.BossMonsterIdFor(tier);
            // One fighter per account (2026-10-08): the attempt is slot 1's.
            if (code == CommandResultCode.Success
                && CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in currentPayload, bossId))
            {
                code = CommandResultCode.NodeOccupied;
            }
            if (code == CommandResultCode.Success)
            {
                // Where slot 1 goes when the fight ends. A Fight pressed during
                // a fight keeps the first answer, or the character would "return"
                // to the Cailleach.
                bool alreadyFighting = currentPayload.SeasonalBossTier != 0
                    && currentPayload.SeasonalBossCharacterId == currentPayload.Slot1_CharacterId;
                long returnTo = alreadyFighting ? currentPayload.SeasonalBossReturnActivityId : currentPayload.ActiveActivityId;

                // The activity change FIRST: it disarms whatever was armed and
                // resets the fight, so the boss spawns at the tier's strength.
                SimulationEngine.ApplyActivityChangeToPayload(ref currentPayload, bossId);
                currentPayload.SeasonalBossTier = (byte)tier;
                currentPayload.SeasonalBossCharacterId = currentPayload.Slot1_CharacterId;
                currentPayload.SeasonalBossReturnActivityId = returnTo;
                code = CommandResultCode.SeasonalBossStarted;
            }

            ctx.PlayerRegistry.EnqueueCommandResult(currentPayload.PlayerId, (byte)code);
        }

        internal static void DrainClears(
            PlayerSessionRegistry registry,
            Action<string, long, Func<Task>> safeDispatch,
            IDbContextFactory<FolkIdleDbContext> contextFactory)
        {
            while (SeasonalBossEngine.Clears.TryDequeue(out var note))
            {
                var captured = note;
                safeDispatch("SeasonalBoss.Clear", captured.PlayerId, () => SeasonalBossEngine.SaveClearAsync(contextFactory, registry, captured));
            }
        }
    }
}
