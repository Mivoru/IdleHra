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

    /// <summary>One gate's standing, read from its DungeonProgress row (tickets already topped up for today).</summary>
    public readonly record struct DungeonGateStanding(int BestStage, int Tickets);

    /// <summary>
    /// "Your road" (2026-11-13): every long-term track an endgame account has,
    /// with where it stands, on one card.
    /// </summary>
    /// <remarks>
    /// Modul: THE TRACKS EXISTED; NOTHING PUT THEM SIDE BY SIDE. Measured on
    /// production the same night: one 20-rebirth account had never entered the
    /// Delve and both held 2-3 of the Book's six Seals. Every number here is
    /// read from the row that owns it (DeedProgressSource for the Book,
    /// DungeonProgress for the gates, PlayerRecords for the rest), never
    /// re-derived on the client.
    ///
    /// Modul: THE DUNGEON LIVES IN DungeonProgress, ONE ROW PER GATE.
    /// PlayerRecord.DungeonBestStage / DungeonTickets are the pre-rework
    /// columns, kept for a later drop and written by nothing. The first cut of
    /// this card read them, so it told two accounts at stages 5-9 that their
    /// best was 0 - and the "they never played the Dungeon" finding it was
    /// built on came from the same dead columns.
    /// </remarks>
    public static class JourneyBoard
    {
        /// <summary>The level from which the card is worth showing.</summary>
        public const int ShowFromLevel = 40;

        public static List<JourneyRow> Build(in DeedContext deeds, PlayerRecord player, int shardBalance, IReadOnlyList<DungeonGateStanding> gates)
        {
            var rows = new List<JourneyRow>();

            if (DungeonEngine.IsOpen((byte)deeds.DefeatedRegionBossMask) && gates.Count > 0)
            {
                // Value is the stages cleared across every gate, so pushing the
                // weakest gate moves the bar as much as the strongest.
                int cleared = gates.Sum(g => Math.Clamp(g.BestStage, 0, DungeonSimulator.StageCount));
                int tickets = gates.Sum(g => g.Tickets);
                string stages = string.Join(" / ", gates.Select(g => g.BestStage));
                rows.Add(new JourneyRow("dungeon", "Dungeon", $"best stages {stages} of {DungeonSimulator.StageCount}",
                    cleared, (long)DungeonSimulator.StageCount * gates.Count, "dungeon",
                    tickets > 0 ? $"{tickets} tickets waiting" : "new tickets at midnight UTC"));
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

            // Read-only: the rows are untracked and topped up in memory only, so
            // the ticket count matches what the Dungeon screen will show without
            // this GET writing anything. A gate with no row yet reads as a new
            // one does (stage 0, a full day's tickets).
            var stored = await db.DungeonProgress.AsNoTracking().Where(d => d.PlayerId == playerId).ToListAsync();
            var now = GameClock.UtcNow;
            var gates = new List<DungeonGateStanding>();
            foreach (var kind in DungeonBoss.All)
            {
                var row = stored.FirstOrDefault(r => r.Kind == (int)kind) ?? new DungeonProgress { PlayerId = playerId, Kind = (int)kind };
                DungeonEngine.TopUpTickets(row, now);
                gates.Add(new DungeonGateStanding(row.BestStage, row.Tickets));
            }
            return Build(in deeds, player, shards, gates);
        }
    }
}
