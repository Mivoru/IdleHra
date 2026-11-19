using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    public enum NavDescendResult { Ran, NoCharges, CircleLocked, Offline, NoCharacter, Locked }
    public enum NavBuyResult { Bought, Unknown, Maxed, NotEnoughSouls }
    public enum NavResearchResult { Started, Finished, Unknown, Busy, NotEnoughGold, NothingToFinish }

    /// <summary>
    /// The Descent into Nav's writes (NavRules holds the numbers, NavSimulator
    /// plays a run). REST like the dungeon and the expeditions: nothing here is
    /// on StateUpdatePacket.
    /// </summary>
    public static class NavEngine
    {
        /// <summary>The account's row, created on first read, with charges and research settled.</summary>
        public static async Task<NavProgress> LoadAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            var row = await db.NavProgress.FirstOrDefaultAsync(n => n.PlayerId == playerId);
            if (row == null)
            {
                row = new NavProgress { PlayerId = playerId, Charges = NavRules.ChargeCap, ChargeAnchorEpoch = nowEpoch };
                db.NavProgress.Add(row);
            }
            Settle(row, nowEpoch);
            return row;
        }

        /// <summary>Charges that came back, and research whose timer ran out.</summary>
        public static void Settle(NavProgress row, long nowEpoch)
        {
            var (charges, anchor) = NavRules.SettleCharges(row.Charges, row.ChargeAnchorEpoch, nowEpoch);
            row.Charges = charges;
            row.ChargeAnchorEpoch = anchor;
            if (!string.IsNullOrEmpty(row.ResearchActive) && row.ResearchEndsEpoch > 0 && row.ResearchEndsEpoch <= nowEpoch)
            {
                var levels = NavRules.Unpack(row.Research);
                levels[row.ResearchActive] = levels.GetValueOrDefault(row.ResearchActive) + 1;
                row.Research = NavRules.Pack(levels);
                row.ResearchActive = string.Empty;
                row.ResearchEndsEpoch = 0;
            }
        }

        public static int BestDepth(NavProgress row, int circle) => NavRules.Unpack(row.BestDepths).GetValueOrDefault(circle.ToString());

        /// <summary>Everything the Workshop and the research add, summed.</summary>
        public static NavBonuses BonusesOf(NavProgress row)
        {
            var w = NavRules.Unpack(row.Workshop);
            var r = NavRules.Unpack(row.Research);
            double craft = NavRules.ResearchByKey("craft")!.PerLevel * r.GetValueOrDefault("craft");
            double Effect(string key) => NavRules.WorkshopEffect(NavRules.WorkshopByKey(key)!, w.GetValueOrDefault(key), craft);
            return new NavBonuses(
                DamagePct: Effect("strength"),
                HealthPct: Effect("bark"),
                AttackSpeedPct: Effect("quick"),
                CritDamagePct: Effect("grave"),
                SoulsPct: Effect("harvest") + NavRules.ResearchByKey("souls")!.PerLevel * r.GetValueOrDefault("souls"),
                StartDepth: w.GetValueOrDefault("fall"),
                Revives: Math.Min(3, w.GetValueOrDefault("breath") / 10));
        }

        public static double DiscountPct(NavProgress row)
            => Math.Min(60.0, NavRules.ResearchByKey("thrift")!.PerLevel * NavRules.Unpack(row.Research).GetValueOrDefault("thrift"));

        /// <summary>Spends a charge and plays a run with the given payload copy; writes the records it beat.</summary>
        public static async Task<(NavDescendResult Result, NavRunResult? Run, List<string> Rewards)> DescendAsync(
            FolkIdleDbContext db, long playerId, TickStatePayload payload, int slot, int circle, long nowEpoch, DateTime utcNow, int seed)
        {
            var rewards = new List<string>();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var row = await db.NavProgress.FromSqlRaw("SELECT * FROM nav_progress WHERE \"PlayerId\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
            if (row == null)
            {
                row = new NavProgress { PlayerId = playerId, Charges = NavRules.ChargeCap, ChargeAnchorEpoch = nowEpoch };
                db.NavProgress.Add(row);
            }
            Settle(row, nowEpoch);
            if (circle < 1 || circle > row.HighestCircle) { await tx.SafeRollbackAsync(); return (NavDescendResult.CircleLocked, null, rewards); }
            if (row.Charges <= 0) { await tx.SafeRollbackAsync(); return (NavDescendResult.NoCharges, null, rewards); }

            int record = BestDepth(row, circle);
            var run = NavSimulator.Run(payload, slot, circle, BonusesOf(row), seed, record);

            if (row.Charges >= NavRules.ChargeCap) row.ChargeAnchorEpoch = nowEpoch;
            row.Charges -= 1;
            row.RunsTotal += 1;
            row.Souls += run.Souls;
            row.SoulsLifetime += run.Souls;

            if (run.DeepestCleared > record)
            {
                var best = NavRules.Unpack(row.BestDepths);
                best[circle.ToString()] = run.DeepestCleared;
                row.BestDepths = NavRules.Pack(best);
                rewards.Add($"New record: depth {run.DeepestCleared} of Circle {circle}");
            }
            bool circleOpened = false;
            if (run.DeepestCleared >= NavRules.CircleOpensAt && circle == row.HighestCircle)
            {
                row.HighestCircle = circle + 1;
                circleOpened = true;
                rewards.Add($"Circle {circle + 1} opens");
            }

            // Titles: the first Veles, and the Circles that carry a name.
            if (run.DeepestCleared >= NavRules.WardenEvery && await TitleEngine.GrantAsync(db, playerId, NavRules.VelesTitleSlug, utcNow))
                rewards.Add("Title: Veles' Guest");
            if (circleOpened)
            {
                foreach (var t in NavRules.CircleTitles)
                {
                    if (t.Circle == row.HighestCircle && await TitleEngine.GrantAsync(db, playerId, t.Slug, utcNow))
                        rewards.Add($"Title: {t.Name}");
                }
            }

            // First-time milestones: a loot chest of the top region each.
            for (int i = 0; i < NavRules.MilestoneDepths.Length; i++)
            {
                int bit = NavRules.MilestoneBit(circle, i);
                if (bit >= 63 || run.DeepestCleared < NavRules.MilestoneDepths[i] || (row.MilestoneMask & (1L << bit)) != 0) continue;
                row.MilestoneMask |= 1L << bit;
                var tier = i switch { 0 => LootChestTier.Silver, 1 => LootChestTier.Gold, _ => LootChestTier.Ancient };
                LootChestEngine.Stage(db, playerId, tier, 5, LootChestSource.Nav, utcNow);
                rewards.Add($"{tier} chest for depth {NavRules.MilestoneDepths[i]} of Circle {circle}");
            }

            row.LastRunJson = JsonSerializer.Serialize(ViewOf(run, rewards));
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // News, after the commit so a rolled-back run never reaches chat:
            // a new Veles beaten (a record that crosses a 25th depth) or a
            // Circle opened. Rare by construction - a few times a season.
            string? news = NavNews(PlayerNameResolver.GetCachedOrFallback(playerId), circle, record, run.DeepestCleared, circleOpened);
            if (news != null) Domain.Social.ChatEngine.EnqueueSystemAnnouncement(news);
            return (NavDescendResult.Ran, run, rewards);
        }

        /// <summary>The News line for a run, or null when it is not news.</summary>
        public static string? NavNews(string name, int circle, int oldRecord, int newRecord, bool circleOpened)
        {
            if (circleOpened) return $"{name} has opened Circle {circle + 1} of Nav.";
            int before = oldRecord / NavRules.WardenEvery, after = newRecord / NavRules.WardenEvery;
            if (after > before) return $"{name} has beaten Veles at depth {after * NavRules.WardenEvery} of Nav (Circle {circle}).";
            return null;
        }

        /// <summary>The run as the screen shows it: the last 40 depths are enough to read why it stopped.</summary>
        public static object ViewOf(NavRunResult run, IReadOnlyList<string> rewards) => new
        {
            run.Circle,
            run.StartDepth,
            run.DeepestCleared,
            run.Souls,
            run.EndedBy,
            run.Seconds,
            run.RevivesUsed,
            run.StoppedBy,
            run.Hint,
            Rewards = rewards,
            Depths = run.Depths.Skip(Math.Max(0, run.Depths.Count - 40)).ToArray(),
        };

        public static async Task<(NavBuyResult Result, int Level)> BuyAsync(FolkIdleDbContext db, long playerId, string key, int levels, long nowEpoch)
        {
            var upgrade = NavRules.WorkshopByKey(key);
            if (upgrade == null) return (NavBuyResult.Unknown, 0);
            levels = Math.Clamp(levels, 1, 100);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var row = await db.NavProgress.FromSqlRaw("SELECT * FROM nav_progress WHERE \"PlayerId\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
            if (row == null) { await tx.SafeRollbackAsync(); return (NavBuyResult.NotEnoughSouls, 0); }
            Settle(row, nowEpoch);
            var w = NavRules.Unpack(row.Workshop);
            int level = w.GetValueOrDefault(key);
            double discount = DiscountPct(row);
            int bought = 0;
            for (int i = 0; i < levels; i++)
            {
                if (upgrade.MaxLevel > 0 && level >= upgrade.MaxLevel) break;
                long cost = NavRules.WorkshopCost(upgrade, level, discount);
                if (row.Souls < cost) break;
                row.Souls -= cost;
                level++;
                bought++;
            }
            if (bought == 0)
            {
                await tx.SafeRollbackAsync();
                return (upgrade.MaxLevel > 0 && level >= upgrade.MaxLevel ? NavBuyResult.Maxed : NavBuyResult.NotEnoughSouls, level);
            }
            w[key] = level;
            row.Workshop = NavRules.Pack(w);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (NavBuyResult.Bought, level);
        }

        /// <summary>Starts the next level of a research line, paying its gold from the database wallet.</summary>
        public static async Task<(NavResearchResult Result, long Gold)> ResearchAsync(FolkIdleDbContext db, long playerId, string key, long nowEpoch)
        {
            var line = NavRules.ResearchByKey(key);
            if (line == null) return (NavResearchResult.Unknown, 0);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var row = await db.NavProgress.FromSqlRaw("SELECT * FROM nav_progress WHERE \"PlayerId\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
            if (row == null) { await tx.SafeRollbackAsync(); return (NavResearchResult.NotEnoughGold, 0); }
            Settle(row, nowEpoch);
            if (!string.IsNullOrEmpty(row.ResearchActive)) { await tx.SafeRollbackAsync(); return (NavResearchResult.Busy, 0); }
            int level = NavRules.Unpack(row.Research).GetValueOrDefault(key);
            long price = NavRules.ResearchGold(level);
            var gold = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                .SingleOrDefaultAsync();
            if (gold == null || gold.Quantity < price) { await tx.SafeRollbackAsync(); return (NavResearchResult.NotEnoughGold, price); }
            gold.Quantity -= price;
            await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.NavResearch, price);
            row.ResearchActive = key;
            row.ResearchEndsEpoch = nowEpoch + NavRules.ResearchSeconds(level);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (NavResearchResult.Started, price);
        }

        /// <summary>
        /// DEV TOOLS ONLY (exercise.mjs): the running research finishes now, the
        /// charges refill, and the Souls are topped up to 1,000 - the fixture is
        /// too weak to clear depth 1, so it could otherwise never buy anything.
        /// </summary>
        public static async Task DevHurryAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            var row = await LoadAsync(db, playerId, nowEpoch);
            if (row.Souls < 1000) row.Souls = 1000;
            if (!string.IsNullOrEmpty(row.ResearchActive)) row.ResearchEndsEpoch = nowEpoch;
            row.Charges = NavRules.ChargeCap;
            Settle(row, nowEpoch);
            await db.SaveChangesAsync();
        }

        /// <summary>The screen's whole state.</summary>
        public static object View(NavProgress row, long nowEpoch, IReadOnlyList<object> roster, bool open = true, string lockedReason = "", object? board = null)
        {
            var w = NavRules.Unpack(row.Workshop);
            var r = NavRules.Unpack(row.Research);
            var bonuses = BonusesOf(row);
            double discount = DiscountPct(row);
            double craft = NavRules.ResearchByKey("craft")!.PerLevel * r.GetValueOrDefault("craft");
            object? lastRun = null;
            if (!string.IsNullOrEmpty(row.LastRunJson))
            {
                try { lastRun = JsonSerializer.Deserialize<JsonElement>(row.LastRunJson); } catch (JsonException) { lastRun = null; }
            }
            return new
            {
                Open = open,
                LockedReason = lockedReason,
                Circle = row.HighestCircle,
                Records = Enumerable.Range(1, row.HighestCircle).Select(c => new { Circle = c, Depth = BestDepth(row, c) }).ToArray(),
                row.Souls,
                row.SoulsLifetime,
                row.Charges,
                NavRules.ChargeCap,
                NextChargeInSeconds = row.Charges >= NavRules.ChargeCap ? 0 : Math.Max(0, row.ChargeAnchorEpoch + NavRules.ChargeSeconds - nowEpoch),
                row.RunsTotal,
                NavRules.SecondsPerGuardian,
                NavRules.CircleOpensAt,
                NavRules.WardenEvery,
                // Who stands below, in order, so the screen can show the next
                // guardians and what each one does before a charge is spent.
                Guardians = NavRules.Guardians.Append(NavRules.Warden)
                    .Select(g => new { g.Key, g.Name, g.Subject, g.AbilityName, g.AbilityText }).ToArray(),
                Bonuses = bonuses,
                Workshop = NavRules.Workshop.Select(u =>
                {
                    int level = w.GetValueOrDefault(u.Key);
                    bool maxed = u.MaxLevel > 0 && level >= u.MaxLevel;
                    return new
                    {
                        u.Key, u.Name, u.Effect, Level = level, u.MaxLevel, Maxed = maxed,
                        Cost = maxed ? 0 : NavRules.WorkshopCost(u, level, discount),
                        Total = NavRules.WorkshopEffect(u, level, craft),
                    };
                }).ToArray(),
                Research = NavRules.Research.Select(l =>
                {
                    int level = r.GetValueOrDefault(l.Key);
                    return new { l.Key, l.Name, l.Effect, Level = level, Gold = NavRules.ResearchGold(level), Seconds = NavRules.ResearchSeconds(level) };
                }).ToArray(),
                row.ResearchActive,
                ResearchLeftSeconds = string.IsNullOrEmpty(row.ResearchActive) ? 0 : Math.Max(0, row.ResearchEndsEpoch - nowEpoch),
                Roster = roster,
                Board = board,
                LastRun = lastRun,
            };
        }
    }
}
