using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// Daily missions: the day's draw, progress, claims and the free reroll
    /// (docs/design/CHRONICLE_PASS.md, step 2). Missions pay Chronicle XP only,
    /// banked on PlayerMissionDays.XpEarned for the pass to sum.
    /// </summary>
    /// <remarks>
    /// Modul: THIS REPLACES QuestEngine's daily quests, which had three
    /// kill/craft slots and no client at all. QuestEngine still runs on the
    /// tick and still checkpoints its rows; removing it touches
    /// TickStatePayload and the checkpoint, so it is a separate cleanup.
    /// </remarks>
    public static class MissionEngine
    {
        public sealed class MissionView
        {
            public int Slot { get; init; }
            public string Action { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public string Hint { get; init; } = string.Empty;
            public int Progress { get; init; }
            public int Target { get; init; }
            public bool Done { get; init; }
            public bool Claimed { get; init; }
            public bool Fixed { get; init; }
            /// <summary>The server allows a swap of this slot now: a free swap, or a forced one for an impossible fixed mission.</summary>
            public bool Swappable { get; init; }
            public int Xp { get; init; }
        }

        public sealed class DayView
        {
            public int DayKey { get; init; }
            public List<MissionView> Missions { get; init; } = new();
            public bool AllDone { get; init; }
            public bool BonusClaimed { get; init; }
            public int BonusXp { get; init; }
            public int RerollsLeft { get; init; }
            public int XpEarnedToday { get; init; }
            /// <summary>Missions done and not yet claimed, plus the bonus when it is ready.</summary>
            public int Claimable { get; init; }
            public int SecondsUntilNextDay { get; init; }
        }

        public enum ClaimOutcome { Ok, NothingToClaim, NotFound }
        public enum RerollOutcome { Ok, NoRerollsLeft, NotRerollable, NothingElseToOffer, NotFound }

        public static int DayKeyOf(DateTime utcNow) => (int)DailyLoginRewardEngine.ToDateKey(utcNow);

        /// <summary>Reads what the player has open, for the draw and the reroll.</summary>
        public static async Task<MissionFacts?> LoadFactsAsync(FolkIdleDbContext db, long playerId, bool deepEnabled, bool worldBossActive)
        {
            var player = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => new { p.CurrentLevel, p.UnspentAttributePoints, p.AvailableSkillPoints, p.DelveDeepestFloor, p.VillagerRecruitmentsThisSeason })
                .SingleOrDefaultAsync();
            if (player == null) return null;

            long gold = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity).FirstOrDefaultAsync();

            var buildings = await db.VillageInfrastructures.AsNoTracking()
                .Where(v => v.PlayerId == playerId
                    && (v.BuildingId == VillageManagementEngine.InnBuildingId || v.BuildingId == VillageManagementEngine.BreedingGroundsBuildingId))
                .Select(v => new { v.BuildingId, v.CurrentLevel })
                .ToListAsync();
            int innLevel = buildings.Where(b => b.BuildingId == VillageManagementEngine.InnBuildingId).Select(b => b.CurrentLevel).FirstOrDefault();
            int groundsLevel = buildings.Where(b => b.BuildingId == VillageManagementEngine.BreedingGroundsBuildingId).Select(b => b.CurrentLevel).FirstOrDefault();

            int residents = await db.VillageNewcomers.AsNoTracking().CountAsync(v => v.PlayerId == playerId && !v.IsElder);
            bool feastPossible = innLevel >= 1
                && VillagerArrivalRules.RecruitBlockedReason(innLevel, residents, gold, player.VillagerRecruitmentsThisSeason) == null;

            // The same test ExpeditionEngine.StartAsync makes of a character.
            var onExpedition = db.Expeditions.Where(e => e.PlayerId == playerId).Select(e => e.CharacterId);
            bool expeditionCandidate = await db.CharacterRecords.AsNoTracking().AnyAsync(c =>
                c.PlayerId == playerId
                && c.SlotIndex >= CharacterSlotEngine.MaxCharacterSlots
                && c.AgePhase >= AgePhaseCurve.Adult
                && !c.IsLockedInEscrow
                && !onExpedition.Contains(c.Id));

            bool hasGuild = await db.GuildMembers.AsNoTracking().AnyAsync(m => m.PlayerId == playerId);
            int lootChests = await db.LootChests.AsNoTracking().CountAsync(c => c.PlayerId == playerId);
            int wardrobeChests = await db.CosmeticItems.AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && c.Kind == (byte)CosmeticKind.Chest && !c.IsListed);

            // The boss mask the way the login builds it: a regional boss with a
            // codex kill is beaten.
            var bossIds = Enumerable.Range(1, 5).Select(RaceUnlockRegistry.GetRegionBossMonsterId).ToArray();
            var beaten = await db.MonsterCodexEntries.AsNoTracking()
                .Where(e => e.PlayerId == playerId && e.KillCount > 0 && bossIds.Contains(e.MonsterId))
                .Select(e => e.MonsterId)
                .ToListAsync();
            byte bossMask = BossFirstClearRules.MaskFrom(new HashSet<int>(beaten));

            return new MissionFacts
            {
                Level = player.CurrentLevel,
                Gold = gold,
                HasGuild = hasGuild,
                BreedingGroundsLevel = groundsLevel,
                HasExpeditionCandidate = expeditionCandidate,
                FeastPossible = feastPossible,
                UnspentAttributePoints = player.UnspentAttributePoints,
                AvailableSkillPoints = player.AvailableSkillPoints,
                LootChests = lootChests,
                WardrobeChests = wardrobeChests,
                DefeatedRegionBossMask = bossMask,
                DungeonsOpen = DungeonEngine.IsOpen(bossMask),
                ReachedDeep = player.DelveDeepestFloor >= DelveRegistry.FirstDeepFloor,
                DeepEnabled = deepEnabled,
                WorldBossActive = worldBossActive,
                NavOpen = NavRules.Enabled() && NavRules.IsOpen(bossMask),
                // At most the fee DelveEngine charges (its region is the highest with a kill, never past beaten bosses + 1).
                DelveEntryFee = DelveRegistry.EntryFeeForRegion(1 + System.Numerics.BitOperations.PopCount((uint)bossMask)),
            };
        }

        /// <summary>
        /// Today's row, drawn and inserted on the first read of the day. A
        /// concurrent first read (two devices) loses the insert race and reads
        /// the winner's row, so both see the same day.
        /// </summary>
        public static async Task<PlayerMissionDay?> EnsureDayAsync(FolkIdleDbContext db, long playerId, int dayKey, Func<Task<MissionFacts?>> facts, DateTime utcNow)
        {
            var row = await db.PlayerMissionDays.AsNoTracking().SingleOrDefaultAsync(d => d.PlayerId == playerId && d.DayKey == dayKey);
            if (row != null) return row;

            var f = await facts();
            if (f == null) return null;

            var actions = MissionRegistry.Draw(playerId, dayKey, f);
            int inserted = await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""PlayerMissionDays"" (""PlayerId"", ""DayKey"", ""Actions"", ""ClaimedMask"", ""BonusClaimed"", ""RerollsUsed"", ""XpEarned"", ""CreatedAtUtc"")
                  VALUES ({0}, {1}, {2}, 0, FALSE, 0, 0, {3})
                  ON CONFLICT (""PlayerId"", ""DayKey"") DO NOTHING",
                playerId, dayKey, Encode(actions), DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            return await db.PlayerMissionDays.AsNoTracking().SingleOrDefaultAsync(d => d.PlayerId == playerId && d.DayKey == dayKey);
        }

        /// <summary>Progress per slot for one day, from the counters.</summary>
        public static async Task<int[]> ProgressAsync(FolkIdleDbContext db, long playerId, int dayKey, IReadOnlyList<MissionAction> actions)
        {
            var counters = await db.PlayerMissionCounters.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.DayKey == dayKey)
                .Select(c => new { c.Action, c.Detail, c.Count })
                .ToListAsync();

            var progress = new int[actions.Count];
            for (int i = 0; i < actions.Count; i++)
            {
                if (!MissionRegistry.TryGet(actions[i], out var def)) continue;
                var mine = counters.Where(c => c.Action == (short)actions[i] && c.Count > 0);
                int raw = def.CountsDistinctDetails ? mine.Select(c => c.Detail).Distinct().Count() : mine.Sum(c => c.Count);
                progress[i] = Math.Min(raw, def.Target);
            }
            return progress;
        }

        public static DayView BuildView(PlayerMissionDay day, int[] progress, DateTime utcNow, bool worldBossActive = true)
        {
            bool freeLeft = day.RerollsUsed < MissionRegistry.FreeRerollsPerDay;
            var actions = Decode(day.Actions);
            var missions = new List<MissionView>(actions.Length);
            int claimable = 0;
            for (int i = 0; i < actions.Length; i++)
            {
                MissionRegistry.TryGet(actions[i], out var def);
                bool done = def != null && progress[i] >= def.Target;
                bool claimed = (day.ClaimedMask & (1 << i)) != 0;
                if (done && !claimed) claimable++;
                missions.Add(new MissionView
                {
                    Slot = i,
                    Action = actions[i].ToString(),
                    Title = def?.Title ?? actions[i].ToString(),
                    Hint = def?.Hint ?? string.Empty,
                    Progress = progress[i],
                    Target = def?.Target ?? 1,
                    Done = done,
                    Claimed = claimed,
                    Fixed = def?.IsFixed ?? false,
                    Swappable = def != null && !done && !claimed
                        && (def.IsFixed ? MissionRegistry.FixedMissionImpossible(def.Action, day.DayKey, worldBossActive) : freeLeft),
                    Xp = MissionRegistry.XpPerMission,
                });
            }

            bool allDone = missions.Count > 0 && missions.All(m => m.Done);
            if (allDone && !day.BonusClaimed) claimable++;
            long nowEpoch = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return new DayView
            {
                DayKey = day.DayKey,
                Missions = missions,
                AllDone = allDone,
                BonusClaimed = day.BonusClaimed,
                BonusXp = MissionRegistry.XpAllDoneBonus,
                RerollsLeft = Math.Max(0, MissionRegistry.FreeRerollsPerDay - day.RerollsUsed),
                XpEarnedToday = day.XpEarned,
                Claimable = claimable,
                SecondsUntilNextDay = (int)((day.DayKey + 1L) * 86400L - nowEpoch),
            };
        }

        public static async Task<DayView?> ViewAsync(FolkIdleDbContext db, long playerId, Func<Task<MissionFacts?>> facts, DateTime utcNow, bool worldBossActive = true)
        {
            int dayKey = DayKeyOf(utcNow);
            var day = await EnsureDayAsync(db, playerId, dayKey, facts, utcNow);
            if (day == null) return null;
            var progress = await ProgressAsync(db, playerId, dayKey, Decode(day.Actions));
            return BuildView(day, progress, utcNow, worldBossActive);
        }

        /// <summary>
        /// Claims every finished, unclaimed mission of today - and the all-done
        /// bonus once all of them are finished. One button, so a player never
        /// has to tap six times; the XP lands on PlayerMissionDays.XpEarned.
        /// </summary>
        public static async Task<(ClaimOutcome Outcome, int XpGained, DayView? View)> ClaimAsync(FolkIdleDbContext db, long playerId, Func<Task<MissionFacts?>> facts, DateTime utcNow, bool worldBossActive = true)
        {
            int dayKey = DayKeyOf(utcNow);
            if (await EnsureDayAsync(db, playerId, dayKey, facts, utcNow) == null) return (ClaimOutcome.NotFound, 0, null);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                // The row lock is what makes two devices' claims take turns; the
                // router's account stripe already serialises one device's taps.
                var day = await db.PlayerMissionDays
                    .FromSqlRaw(@"SELECT * FROM ""PlayerMissionDays"" WHERE ""PlayerId"" = {0} AND ""DayKey"" = {1} FOR UPDATE", playerId, dayKey)
                    .SingleAsync();
                var actions = Decode(day.Actions);
                var progress = await ProgressAsync(db, playerId, dayKey, actions);

                int gained = 0;
                for (int i = 0; i < actions.Length; i++)
                {
                    if (!MissionRegistry.TryGet(actions[i], out var def)) continue;
                    if ((day.ClaimedMask & (1 << i)) != 0 || progress[i] < def.Target) continue;
                    day.ClaimedMask |= 1 << i;
                    gained += MissionRegistry.XpPerMission;
                }

                bool allDone = actions.Length > 0 && actions.Select((a, i) => MissionRegistry.TryGet(a, out var d) && progress[i] >= d.Target).All(x => x);
                if (allDone && !day.BonusClaimed)
                {
                    day.BonusClaimed = true;
                    gained += MissionRegistry.XpAllDoneBonus;
                }

                if (gained == 0)
                {
                    await tx.SafeRollbackAsync();
                    return (ClaimOutcome.NothingToClaim, 0, BuildView(day, progress, utcNow, worldBossActive));
                }

                day.XpEarned += gained;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (ClaimOutcome.Ok, gained, BuildView(day, progress, utcNow, worldBossActive));
            }
            catch
            {
                await tx.SafeRollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Swaps one unfinished mission for a rotating one the player can do:
        /// a rotating mission with one of the day's free swaps, a fixed one only
        /// when it cannot be finished (and then without using a free swap).
        /// </summary>
        /// <remarks>
        /// Modul: FIXED MISSIONS CAN BE SWAPPED TOO (owner, 2026-11-15: "what if
        /// I already fought every dungeon today and have no tickets, or cannot
        /// strike the world boss again?"). Acts done before the day was drawn
        /// still count - the counters are per day - so on an ordinary day a
        /// fixed mission is always finishable. Two cases are not: the missions'
        /// own deploy day (acts before it were never counted), and a world boss
        /// that dies after the day was drawn. Only those may be replaced: the
        /// owner did not want free swaps turning every day into the easiest six
        /// (MissionRegistry.FixedMissionImpossible).
        /// </remarks>
        public static async Task<(RerollOutcome Outcome, DayView? View)> RerollAsync(FolkIdleDbContext db, long playerId, int slot, Func<Task<MissionFacts?>> facts, DateTime utcNow)
        {
            int dayKey = DayKeyOf(utcNow);
            if (await EnsureDayAsync(db, playerId, dayKey, facts, utcNow) == null) return (RerollOutcome.NotFound, null);
            var f = await facts();
            if (f == null) return (RerollOutcome.NotFound, null);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var day = await db.PlayerMissionDays
                    .FromSqlRaw(@"SELECT * FROM ""PlayerMissionDays"" WHERE ""PlayerId"" = {0} AND ""DayKey"" = {1} FOR UPDATE", playerId, dayKey)
                    .SingleAsync();
                var actions = Decode(day.Actions);
                var progress = await ProgressAsync(db, playerId, dayKey, actions);

                RerollOutcome? refusal = null;
                bool forced = false;
                if (slot < 0 || slot >= actions.Length
                    || !MissionRegistry.TryGet(actions[slot], out var def)
                    || progress[slot] >= def.Target
                    || (day.ClaimedMask & (1 << slot)) != 0)
                {
                    refusal = RerollOutcome.NotRerollable;
                }
                else if (def.IsFixed)
                {
                    // Only an impossible fixed mission, and it costs no free swap.
                    if (MissionRegistry.FixedMissionImpossible(def.Action, dayKey, f.WorldBossActive)) forced = true;
                    else refusal = RerollOutcome.NotRerollable;
                }
                else if (day.RerollsUsed >= MissionRegistry.FreeRerollsPerDay)
                {
                    refusal = RerollOutcome.NoRerollsLeft;
                }

                MissionAction? pick = null;
                if (refusal == null)
                {
                    // A forced swap draws with its own salt, so it never moves
                    // the order the free swaps draw from.
                    pick = MissionRegistry.RerollPick(playerId, dayKey, forced ? 100 + slot : day.RerollsUsed + 1, actions, f);
                    if (pick == null) refusal = RerollOutcome.NothingElseToOffer;
                }

                if (refusal != null)
                {
                    await tx.SafeRollbackAsync();
                    return (refusal.Value, BuildView(day, progress, utcNow, f.WorldBossActive));
                }

                actions[slot] = pick!.Value;
                day.Actions = Encode(actions);
                if (!forced) day.RerollsUsed++;
                await db.SaveChangesAsync();
                await tx.CommitAsync();

                var fresh = await ProgressAsync(db, playerId, dayKey, actions);
                return (RerollOutcome.Ok, BuildView(day, fresh, utcNow, f.WorldBossActive));
            }
            catch
            {
                await tx.SafeRollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Chronicle XP banked by finished missions between two days, inclusive.
        /// The pass (step 3) reads its season through this.
        /// </summary>
        public static Task<int> XpBetweenAsync(FolkIdleDbContext db, long playerId, int fromDayKey, int toDayKey)
            => db.PlayerMissionDays.AsNoTracking()
                .Where(d => d.PlayerId == playerId && d.DayKey >= fromDayKey && d.DayKey <= toDayKey)
                .SumAsync(d => d.XpEarned);

        internal static string Encode(IEnumerable<MissionAction> actions) => string.Join(",", actions.Select(a => ((short)a).ToString()));

        internal static MissionAction[] Decode(string actions)
            => string.IsNullOrEmpty(actions)
                ? Array.Empty<MissionAction>()
                : actions.Split(',').Select(s => short.TryParse(s, out short v) ? (MissionAction)v : 0).Where(a => a != 0).ToArray();
    }
}
