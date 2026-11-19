using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The three research lines of Nav that count OUTSIDE it - Veles' bounty
    /// (combat gold), Memory of the dead (combat XP), Mokosh's thread (loot
    /// luck) - as the 10 Hz tick reads them.
    /// </summary>
    /// <remarks>
    /// Modul: A CACHE BESIDE THE PAYLOAD, LIKE THE GUILD BUFFS. The tick and the
    /// offline catch-up read GuildBonusesCache by guild id rather than carrying
    /// the buffs on TickStatePayload; this is the same shape by player id.
    /// Putting three levels on the payload would mean a hydration path, a
    /// checkpoint field and a reload merge for numbers that change a few times
    /// a week, and the payload is a fixed layout the whole tick copies.
    ///
    /// Modul: A RESEARCH THAT FINISHED WHILE NOBODY LOOKED STILL COUNTS. Nav
    /// credits a finished research only when the row is next read (NavEngine
    /// .Settle). The entry keeps the running line and its end, so the level is
    /// "finished levels, plus one if the running one is this line and its time
    /// is up" - the tick sees the level the moment it is earned.
    ///
    /// Modul: THE FLAG STILL CLOSES IT. With FOLKIDLE_NAV off every lookup is 0,
    /// so turning Nav off turns its outward bonuses off with it.
    /// </remarks>
    public static class NavBlessingsCache
    {
        private readonly record struct Entry(int Bounty, int Memory, int Luck, string Active, long EndsEpoch);

        private static readonly ConcurrentDictionary<long, Entry> _entries = new();

        /// <summary>Records the row's research after a write or a read.</summary>
        public static void Update(NavProgress row)
        {
            var r = NavRules.Unpack(row.Research);
            var entry = new Entry(r.GetValueOrDefault("bounty"), r.GetValueOrDefault("memory"), r.GetValueOrDefault("luck"),
                row.ResearchActive ?? string.Empty, row.ResearchEndsEpoch);
            if (entry.Bounty == 0 && entry.Memory == 0 && entry.Luck == 0 && !IsOutward(entry.Active))
            {
                _entries.TryRemove(row.PlayerId, out _);
                return;
            }
            _entries[row.PlayerId] = entry;
        }

        private static bool IsOutward(string key) => key is "bounty" or "memory" or "luck";

        private static int Level(long playerId, string key)
        {
            if (!NavRules.Enabled() || !_entries.TryGetValue(playerId, out var e)) return 0;
            int level = key switch { "bounty" => e.Bounty, "memory" => e.Memory, _ => e.Luck };
            if (e.Active == key && e.EndsEpoch > 0 && e.EndsEpoch <= GameClock.UtcNowOffset.ToUnixTimeSeconds()) level++;
            return level;
        }

        /// <summary>Combat gold, percent (a multiplier of its own in CombatGoldReward.FactorsFor).</summary>
        public static double GoldPct(long playerId) => Level(playerId, "bounty") * NavRules.ResearchByKey("bounty")!.PerLevel;

        /// <summary>Combat XP, whole percentage points added to the kill XP sum.</summary>
        public static int XpPct(long playerId) => (int)(Level(playerId, "memory") * NavRules.ResearchByKey("memory")!.PerLevel);

        /// <summary>Loot luck, percentage points added to the character's luck.</summary>
        public static float LootLuckPct(long playerId) => (float)(Level(playerId, "luck") * NavRules.ResearchByKey("luck")!.PerLevel);

        /// <summary>Loads every row with an outward line. Called once at start-up; the table is one row per account.</summary>
        public static async Task LoadAllAsync(FolkIdleDbContext db)
        {
            var rows = await db.NavProgress.AsNoTracking()
                .Where(n => n.Research != string.Empty || n.ResearchActive != string.Empty)
                .ToListAsync();
            foreach (var row in rows) Update(row);
        }

        /// <summary>Tests only.</summary>
        internal static void Clear() => _entries.Clear();
    }
}
