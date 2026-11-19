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
    public enum NavBuyResult { Bought, Unknown, Maxed, NotEnoughSouls, Locked }
    public enum NavResearchResult { Started, Finished, Unknown, Busy, NotEnoughGold, NothingToFinish, Maxed, NotOpen }
    public enum NavFrameResult { Bought, Unknown, Owned, NotEnoughSouls, NotReached }

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
            NavBlessingsCache.Update(row);
            return row;
        }

        /// <summary>The cap and seconds per charge after Deep well and Swift return.</summary>
        public static (int Cap, int Seconds) ChargeRulesOf(NavProgress row)
        {
            var r = NavRules.Unpack(row.Research);
            return NavRules.ChargeRules(r.GetValueOrDefault("well"), r.GetValueOrDefault("swift"));
        }

        /// <summary>Charges that came back, research whose timer ran out, and a week that turned.</summary>
        public static void Settle(NavProgress row, long nowEpoch)
        {
            // Research first: a finished Deep well raises the cap the charges settle against.
            if (!string.IsNullOrEmpty(row.ResearchActive) && row.ResearchEndsEpoch > 0 && row.ResearchEndsEpoch <= nowEpoch)
            {
                var levels = NavRules.Unpack(row.Research);
                levels[row.ResearchActive] = levels.GetValueOrDefault(row.ResearchActive) + 1;
                row.Research = NavRules.Pack(levels);
                row.ResearchActive = string.Empty;
                row.ResearchEndsEpoch = 0;
            }
            var (cap, seconds) = ChargeRulesOf(row);
            var (charges, anchor) = NavRules.SettleCharges(row.Charges, row.ChargeAnchorEpoch, nowEpoch, cap, seconds);
            row.Charges = charges;
            row.ChargeAnchorEpoch = anchor;
            RollWeek(row, NavRules.WeekOf(nowEpoch));
        }

        /// <summary>A new week: this week's best moves to Prev*, and the week starts from nothing.</summary>
        public static void RollWeek(NavProgress row, int week)
        {
            if (row.WeekId == week) return;
            if (row.WeekId > 0 && row.WeekDepth > 0)
            {
                row.PrevWeekId = row.WeekId;
                row.PrevWeekCircle = row.WeekCircle;
                row.PrevWeekDepth = row.WeekDepth;
            }
            row.WeekId = week;
            row.WeekCircle = 0;
            row.WeekDepth = 0;
        }

        /// <summary>This account's result in <paramref name="week"/>: (circle, depth), or (0, 0).</summary>
        public static (int Circle, int Depth) WeekResult(NavProgress row, int week)
        {
            if (row.WeekId == week) return (row.WeekCircle, row.WeekDepth);
            if (row.PrevWeekId == week) return (row.PrevWeekCircle, row.PrevWeekDepth);
            return (0, 0);
        }

        /// <summary>How many spirits walk: three, plus Ancestors' call.</summary>
        public static int SpiritCount(NavProgress row)
            => NavRules.SpiritsBase + Math.Clamp(NavRules.Unpack(row.Research).GetValueOrDefault("call"), 0, 3);

        /// <summary>
        /// Every adult this account has outside the three slots, with their
        /// aptitudes - the pool the spirits are chosen from. Children are not
        /// spirits; a character held in a market escrow is not on the bench.
        /// </summary>
        public static async Task<List<NavRules.Spirit>> LoadBenchAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId
                    && (c.SlotIndex < 0 || c.SlotIndex >= CharacterSlotEngine.MaxCharacterSlots)
                    && c.AgePhase >= 1 && !c.IsLockedInEscrow && c.Lineage != null)
                .Select(c => new { c.Name, c.Lineage!.AptitudeStrength, c.Lineage.AptitudeSkill, c.Lineage.AptitudeEndurance, c.Lineage.AptitudeFortune })
                .ToListAsync();
            return rows.Select(r => new NavRules.Spirit(r.Name, r.AptitudeStrength, r.AptitudeSkill, r.AptitudeEndurance, r.AptitudeFortune)).ToList();
        }

        public static int BestDepth(NavProgress row, int circle) => NavRules.Unpack(row.BestDepths).GetValueOrDefault(circle.ToString());

        /// <summary>Everything the Workshop and the research add, summed.</summary>
        public static NavBonuses BonusesOf(NavProgress row, IReadOnlyList<NavRules.Spirit>? bench = null, long nowEpoch = 0)
        {
            var w = NavRules.Unpack(row.Workshop);
            var r = NavRules.Unpack(row.Research);
            double craft = NavRules.ResearchByKey("craft")!.PerLevel * r.GetValueOrDefault("craft");
            double Effect(string key) => NavRules.WorkshopEffect(NavRules.WorkshopByKey(key)!, w.GetValueOrDefault(key), craft);
            var spirits = SpiritBonusOf(row, bench);
            var banes = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var u in NavRules.Workshop)
            {
                if (u.Guardian.Length == 0) continue;
                double cut = Effect(u.Key);
                if (cut > 0) banes[u.Guardian] = cut;
            }
            return new NavBonuses(
                DamagePct: Effect("strength") + spirits.Damage,
                HealthPct: Effect("bark") + spirits.Health,
                AttackSpeedPct: Effect("quick") + spirits.Speed,
                CritDamagePct: Effect("grave"),
                SoulsPct: Effect("harvest") + (NavRules.ResearchByKey("souls")!.PerLevel * r.GetValueOrDefault("souls")) + spirits.Souls,
                StartDepth: w.GetValueOrDefault("fall"),
                Revives: Math.Min(3, w.GetValueOrDefault("breath") / 10))
            {
                BaneReductionPct = banes,
                Restless = nowEpoch > 0 ? NavRules.RestlessGuardian(NavRules.WeekOf(nowEpoch)).Key : string.Empty,
            };
        }

        /// <summary>What this account's spirits add (after Spirit bond), from its bench.</summary>
        public static (double Damage, double Health, double Speed, double Souls) SpiritBonusOf(NavProgress row, IReadOnlyList<NavRules.Spirit>? bench)
        {
            if (bench == null || bench.Count == 0) return (0, 0, 0, 0);
            var w = NavRules.Unpack(row.Workshop);
            double craft = NavRules.ResearchByKey("craft")!.PerLevel * NavRules.Unpack(row.Research).GetValueOrDefault("craft");
            double bond = NavRules.WorkshopEffect(NavRules.WorkshopByKey("bond")!, w.GetValueOrDefault("bond"), craft);
            return NavRules.SpiritBonus(NavRules.ChooseSpirits(bench, SpiritCount(row)), bond);
        }

        public static double DiscountPct(NavProgress row)
            => Math.Min(60.0, NavRules.ResearchByKey("thrift")!.PerLevel * NavRules.Unpack(row.Research).GetValueOrDefault("thrift"));

        /// <summary>Spends a charge and plays a run with the given payload copy; writes the records it beat.</summary>
        public static async Task<(NavDescendResult Result, NavRunResult? Run, List<string> Rewards)> DescendAsync(
            FolkIdleDbContext db, long playerId, TickStatePayload payload, int slot, int circle, long nowEpoch, DateTime utcNow, int seed,
            IReadOnlyList<NavRules.Spirit>? bench = null)
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
            var run = NavSimulator.Run(payload, slot, circle, BonusesOf(row, bench, nowEpoch), seed, record);

            if (row.Charges >= ChargeRulesOf(row).Cap) row.ChargeAnchorEpoch = nowEpoch;
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
            // The guardian that stopped the run is met: its bane opens in the Workshop.
            int metBit = NavRules.GuardianBit(run.StoppedBy);
            if (metBit >= 0 && (row.GuardiansMet & (1 << metBit)) == 0)
            {
                row.GuardiansMet |= 1 << metBit;
                var bane = NavRules.BaneFor(run.StoppedBy);
                if (bane != null) rewards.Add($"New bane in the Workshop: {bane.Name}");
            }

            // The week's board: the deepest Circle, then depth, reached this week.
            if (run.DeepestCleared > 0 && (circle > row.WeekCircle || (circle == row.WeekCircle && run.DeepestCleared > row.WeekDepth)))
            {
                row.WeekCircle = circle;
                row.WeekDepth = run.DeepestCleared;
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
            if (upgrade.Guardian.Length > 0 && !NavRules.HasMet(row.GuardiansMet, upgrade.Guardian))
            {
                await tx.SafeRollbackAsync();
                return (NavBuyResult.Locked, 0);
            }
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
            int cap = NavRules.ResearchCap(line, row.HighestCircle);
            if (cap < 0) { await tx.SafeRollbackAsync(); return (NavResearchResult.NotOpen, 0); }
            if (cap > 0 && level >= cap) { await tx.SafeRollbackAsync(); return (NavResearchResult.Maxed, 0); }
            long price = NavRules.ResearchGold(line, level);
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
            NavBlessingsCache.Update(row);
            return (NavResearchResult.Started, price);
        }

        /// <summary>Buys a Nav frame with Souls: bound, once, behind the place in Nav it names.</summary>
        public static async Task<NavFrameResult> BuyFrameAsync(FolkIdleDbContext db, long playerId, string id, long nowEpoch, DateTime utcNow)
        {
            var frame = NavRules.FrameById(id);
            var def = CosmeticRegistry.Find(id);
            if (frame == null || def == null) return NavFrameResult.Unknown;
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var row = await db.NavProgress.FromSqlRaw("SELECT * FROM nav_progress WHERE \"PlayerId\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
            if (row == null) { await tx.SafeRollbackAsync(); return NavFrameResult.NotEnoughSouls; }
            Settle(row, nowEpoch);
            if (!FrameReached(row, frame)) { await tx.SafeRollbackAsync(); return NavFrameResult.NotReached; }
            if (await db.CosmeticItems.AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == id)) { await tx.SafeRollbackAsync(); return NavFrameResult.Owned; }
            if (row.Souls < frame.Price) { await tx.SafeRollbackAsync(); return NavFrameResult.NotEnoughSouls; }
            row.Souls -= frame.Price;
            db.CosmeticItems.Add(new CosmeticItem
            {
                PlayerId = playerId,
                Kind = (byte)def.Kind,
                DefinitionId = def.Id,
                Rarity = (byte)def.Rarity,
                Source = (byte)CosmeticSource.Nav,
                AcquiredAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return NavFrameResult.Bought;
        }

        /// <summary>The frame's Circle is open and its depth reached in some Circle.</summary>
        public static bool FrameReached(NavProgress row, NavRules.NavFrame frame)
        {
            if (row.HighestCircle < frame.Circle) return false;
            var best = NavRules.Unpack(row.BestDepths);
            return best.Any(kv => int.TryParse(kv.Key, out int c) && c >= frame.Circle && kv.Value >= frame.Depth)
                || row.HighestCircle > frame.Circle;
        }

        /// <summary>
        /// Pays the weekly board for the weeks this account still holds a
        /// result for and has not been settled for. Ranked against every row
        /// that still holds that week (an account that has since played two
        /// more weeks no longer does - with a handful of players that is rare,
        /// and the error is in the player's favour).
        /// </summary>
        public static async Task<List<string>> SettleWeekAsync(FolkIdleDbContext db, long playerId, long nowEpoch, DateTime utcNow)
        {
            var lines = new List<string>();
            int current = NavRules.WeekOf(nowEpoch);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var row = await db.NavProgress.FromSqlRaw("SELECT * FROM nav_progress WHERE \"PlayerId\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
            if (row == null) { await tx.SafeRollbackAsync(); return lines; }
            Settle(row, nowEpoch);
            var weeks = new[] { row.PrevWeekId, row.WeekId }
                .Where(w => w > 0 && w < current && w > row.WeekRewardSettledId).Distinct().OrderBy(w => w).ToList();
            if (weeks.Count == 0) { await db.SaveChangesAsync(); await tx.CommitAsync(); return lines; }

            foreach (int week in weeks)
            {
                var others = await db.NavProgress.AsNoTracking()
                    .Where(n => n.WeekId == week || n.PrevWeekId == week)
                    .ToListAsync();
                var standings = others.Where(o => o.PlayerId != playerId).Append(row)
                    .Select(o => (o.PlayerId, Result: WeekResult(o, week)))
                    .Where(o => o.Result.Depth > 0)
                    .OrderByDescending(o => o.Result.Circle).ThenByDescending(o => o.Result.Depth).ThenBy(o => o.PlayerId)
                    .ToList();
                int rank = standings.FindIndex(o => o.PlayerId == playerId) + 1;
                if (rank >= 1 && rank <= 3 && standings.Count >= NavRules.WeeklyMinRunners)
                {
                    var tier = rank switch { 1 => LootChestTier.Ancient, 2 => LootChestTier.Gold, _ => LootChestTier.Silver };
                    LootChestEngine.Stage(db, playerId, tier, 5, LootChestSource.Nav, utcNow);
                    lines.Add($"#{rank} deepest in Nav last week: {tier} chest");
                    if (rank == 1 && await TitleEngine.GrantAsync(db, playerId, NavRules.WeeklyTitleSlug, utcNow))
                        lines.Add("Title: Deepest of the Week");
                }
                row.WeekRewardSettledId = Math.Max(row.WeekRewardSettledId, week);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return lines;
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
            row.Charges = ChargeRulesOf(row).Cap;
            Settle(row, nowEpoch);
            await db.SaveChangesAsync();
        }

        /// <summary>The screen's whole state.</summary>
        public static object View(NavProgress row, long nowEpoch, IReadOnlyList<object> roster, bool open = true, string lockedReason = "", object? board = null,
            IReadOnlyList<NavRules.Spirit>? bench = null, object? weekBoard = null, IReadOnlyCollection<string>? ownedFrames = null, IReadOnlyList<string>? notices = null)
        {
            var w = NavRules.Unpack(row.Workshop);
            var r = NavRules.Unpack(row.Research);
            var bonuses = BonusesOf(row, bench, nowEpoch);
            var (cap, chargeSeconds) = ChargeRulesOf(row);
            int week = NavRules.WeekOf(nowEpoch);
            var restless = NavRules.RestlessGuardian(week);
            var walking = bench == null ? new List<NavRules.Spirit>() : NavRules.ChooseSpirits(bench, SpiritCount(row));
            var spiritBonus = SpiritBonusOf(row, bench);
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
                ChargeCap = cap,
                ChargeSeconds = chargeSeconds,
                NextChargeInSeconds = row.Charges >= cap ? 0 : Math.Max(0, row.ChargeAnchorEpoch + chargeSeconds - nowEpoch),
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
                    bool locked = u.Guardian.Length > 0 && !NavRules.HasMet(row.GuardiansMet, u.Guardian);
                    return new
                    {
                        u.Key, u.Name, u.Effect, Level = level, u.MaxLevel, Maxed = maxed,
                        Cost = maxed ? 0 : NavRules.WorkshopCost(u, level, discount),
                        Total = Math.Min(u.Guardian.Length > 0 ? NavRules.BaneMaxReductionPct : double.MaxValue, NavRules.WorkshopEffect(u, level, craft)),
                        u.Guardian,
                        Locked = locked,
                    };
                }).ToArray(),
                Research = NavRules.Research.Select(l =>
                {
                    int level = r.GetValueOrDefault(l.Key);
                    int lineCap = NavRules.ResearchCap(l, row.HighestCircle);
                    return new
                    {
                        l.Key, l.Name, l.Effect, Level = level,
                        Gold = NavRules.ResearchGold(l, level), Seconds = NavRules.ResearchSeconds(level),
                        Cap = lineCap, Open = lineCap >= 0, l.OpensAtCircle, l.Outward,
                        Maxed = lineCap > 0 && level >= lineCap,
                    };
                }).ToArray(),
                Blessings = new
                {
                    GoldPct = NavRules.ResearchByKey("bounty")!.PerLevel * r.GetValueOrDefault("bounty"),
                    XpPct = NavRules.ResearchByKey("memory")!.PerLevel * r.GetValueOrDefault("memory"),
                    LuckPct = NavRules.ResearchByKey("luck")!.PerLevel * r.GetValueOrDefault("luck"),
                },
                Spirits = new
                {
                    Count = SpiritCount(row),
                    NavRules.SpiritFloor,
                    Walking = walking.Select(s => new { s.Name, s.Strength, s.Skill, s.Endurance, s.Fortune, s.Total }).ToArray(),
                    BenchAdults = bench?.Count ?? 0,
                    DamagePct = Math.Round(spiritBonus.Damage, 1),
                    HealthPct = Math.Round(spiritBonus.Health, 1),
                    SpeedPct = Math.Round(spiritBonus.Speed, 1),
                    SoulsPct = Math.Round(spiritBonus.Souls, 1),
                },
                Omen = new
                {
                    restless.Key,
                    restless.Name,
                    AbilityPct = (int)Math.Round(NavRules.RestlessAbility * 100),
                    SoulsFactor = NavRules.RestlessSouls,
                    EndsInSeconds = Math.Max(0, NavRules.WeekEndsEpoch(week) - nowEpoch),
                },
                Week = new { Circle = row.WeekCircle, Depth = row.WeekDepth },
                WeekBoard = weekBoard,
                Frames = NavRules.Frames.Select(f => new
                {
                    f.Id, f.Name, f.Price, f.Circle, f.Depth,
                    Reached = FrameReached(row, f),
                    Owned = ownedFrames?.Contains(f.Id) ?? false,
                }).ToArray(),
                Notices = notices ?? Array.Empty<string>(),
                row.ResearchActive,
                ResearchLeftSeconds = string.IsNullOrEmpty(row.ResearchActive) ? 0 : Math.Max(0, row.ResearchEndsEpoch - nowEpoch),
                Roster = roster,
                Board = board,
                LastRun = lastRun,
            };
        }
    }
}
