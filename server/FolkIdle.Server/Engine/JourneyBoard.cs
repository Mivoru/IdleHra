using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>One long-term track on the Home screen's "Your road" card.</summary>
    public sealed record JourneyRow(string Key, string Label, string Progress, long Value, long Max, string Screen, string Hint);

    /// <summary>
    /// "Your road" (2026-11-13): every long-term track an endgame account has,
    /// with where it stands, on one card.
    /// </summary>
    /// <remarks>
    /// Modul: THE CONTENT WAS THERE AND UNTOUCHED. Measured on production the
    /// same night: both 20-rebirth accounts (level 89-90) had a Dungeon best
    /// stage of 0, one had never entered the Delve, and they held 2-3 of the
    /// Book's Seals - while asking for something to do. The tracks existed;
    /// nothing put them side by side. Every number here is read from the row
    /// that owns it (DeedProgressSource for the Book, PlayerRecords for the
    /// rest), never re-derived on the client.
    /// </remarks>
    public static class JourneyBoard
    {
        /// <summary>The level from which the card is worth showing.</summary>
        public const int ShowFromLevel = 40;

        public static List<JourneyRow> Build(in DeedContext deeds, PlayerRecord player, int shardBalance)
        {
            var rows = new List<JourneyRow>();

            if (DungeonEngine.IsOpen((byte)deeds.DefeatedRegionBossMask))
            {
                rows.Add(new JourneyRow("dungeon", "Dungeon", $"best stage {player.DungeonBestStage} of {DungeonSimulator.StageCount}",
                    player.DungeonBestStage, DungeonSimulator.StageCount, "dungeon",
                    player.DungeonTickets > 0 ? $"{player.DungeonTickets} tickets waiting" : "three gates, fifteen stages each"));
            }

            rows.Add(new JourneyRow("delve", "The Delve", $"deepest floor {player.DelveDeepestFloor}",
                player.DelveDeepestFloor, Math.Max(player.DelveDeepestFloor, 25), "delve",
                player.DelveDeepestThisWeek > 0 ? $"floor {player.DelveDeepestThisWeek} this week" : "not entered this week"));

            int seals = DeedRegistry.SealCount(player.SealsEarnedMask);
            rows.Add(new JourneyRow("deeds", "Book of Deeds", $"{seals} of {DeedRegistry.ChapterCount} Seals",
                seals, DeedRegistry.ChapterCount, "progression", $"+{DeedRegistry.SkillPointsPerSeal} skill points a Seal, for good"));

            int ceiling = BreedingAptitudes.CeilingFor(player.RebirthCount);
            rows.Add(new JourneyRow("bloodline", "Bloodline", $"best lowest aptitude {deeds.BestLowestAptitude} of {BreedingAptitudes.MaxValue}",
                deeds.BestLowestAptitude, BreedingAptitudes.MaxValue, "breeding", $"ceiling {ceiling} now"));

            int trialsDone = RebirthTrials.All.Count(t => RebirthTrials.IsCompleted(player.CompletedRebirthTrials, t.Id));
            var active = RebirthTrials.Find(player.ActiveRebirthTrial);
            rows.Add(new JourneyRow("trials", "Rebirth Trials", $"{trialsDone} of {RebirthTrials.All.Count} finished",
                trialsDone, RebirthTrials.All.Count, "rebirth", active != null ? $"this life: {active.Name}" : "choose one at your next rebirth"));

            int ranks = LegacyPerkResolver.GetXpBonusPct(player.LegacyPerks)
                + LegacyPerkResolver.GetGoldBonusPct(player.LegacyPerks)
                + LegacyPerkResolver.GetCombatSpeedBonusPct(player.LegacyPerks);
            int maxRanks = LegacyPerkResolver.XpMaxRank + LegacyPerkResolver.GoldMaxRank + LegacyPerkResolver.CombatSpeedMaxRank;
            rows.Add(new JourneyRow("boons", "Ancestral Boons", $"{ranks} of {maxRanks} ranks",
                ranks, maxRanks, "rebirth", $"{shardBalance.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} shards to spend"));

            return rows;
        }

        public static async Task<List<JourneyRow>?> LoadAsync(FolkIdleDbContext db, long playerId)
        {
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            if (player == null) return null;
            if (player.CurrentLevel < ShowFromLevel && player.RebirthCount == 0) return new List<JourneyRow>();

            var deeds = await DeedProgressSource.LoadAsync(db, playerId);
            int shards = (int)Math.Min(int.MaxValue, await db.PlayerLegacyLedgers.AsNoTracking()
                .Where(l => l.PlayerId == playerId).SumAsync(l => (long)l.LegacyShardBalance));
            return Build(in deeds, player, shards);
        }
    }
}
