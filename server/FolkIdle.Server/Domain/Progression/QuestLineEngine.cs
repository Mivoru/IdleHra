using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    public enum QuestStepState
    {
        /// <summary>The act is not possible yet; the view says what unlocks it.</summary>
        Locked = 0,
        /// <summary>Possible and not done.</summary>
        Available = 1,
        /// <summary>Done, reward not yet taken.</summary>
        Done = 2,
        Claimed = 3,
    }

    public enum QuestClaimResult
    {
        Ok,
        UnknownStep,
        PlayerNotFound,
        /// <summary>Quarantined accounts are refused here as the tick refuses their commands.</summary>
        Restricted,
        /// <summary>Not done, and not yet possible.</summary>
        Locked,
        /// <summary>Possible, but the player has not done it.</summary>
        NotDone,
        AlreadyClaimed,
    }

    public sealed record QuestRewardView(int Region, long Gold, string MaterialLog, string MaterialOre, int MaterialQuantity);

    public sealed record QuestStepView(
        string Id, int Order, string Title, string Explanation, string Screen, string[] GuideTargets,
        string UnlockHint, string State, bool Claimable);

    public sealed record QuestLineView(
        string? Result, QuestRewardView Reward, int Done, int Claimed, int Total, QuestStepView[] Steps);

    /// <summary>
    /// The quest line's rules in motion: evaluates each step from durable facts,
    /// latches what it has seen, and pays the reward exactly once.
    /// </summary>
    /// <remarks>
    /// Modul: THE CLIENT SENDS A STEP ID AND NOTHING ELSE. Whether the step is
    /// done, what it pays and whether it was already paid are all decided here,
    /// from the database, inside the transaction that pays. There is no field on
    /// the claim request a tampered client could make profitable - the rule
    /// DelveEngine states for its own REST surface, for the same reason (opcode
    /// 39 once granted diamonds from an unsigned client number).
    ///
    /// Modul: THE CLAIM GUARD IS ONE SQL STATEMENT, NOT A READ-THEN-WRITE. The
    /// upsert in ClaimAsync only takes effect where ClaimedAtUtc IS NULL and
    /// reports how many rows it touched, so two claims racing past every other
    /// check still cannot both pay: the second waits on the row, re-evaluates
    /// the WHERE against the committed row, and touches nothing. The router's
    /// per-account stripe makes that race rare; this makes it impossible.
    ///
    /// Modul: OFF THE TICK, ON THE ESTABLISHED GOLD PATH - the same shape as
    /// DelveEngine. Gold and materials are credited to CommodityRecords through
    /// CommodityLedger inside the transaction, and the caller enqueues
    /// ReloadState afterwards so the live payload picks the credit up. Writing
    /// to the payload's gold directly would be the third gold path, and "two gold
    /// paths, and mixing them pays the player twice" is a lesson this codebase
    /// has already paid for.
    /// </remarks>
    public static class QuestLineEngine
    {
        // ---------------------------------------------------------------
        // Facts
        // ---------------------------------------------------------------

        /// <summary>Loads everything the predicates read; null when the player does not exist.</summary>
        public static async Task<QuestFacts?> LoadFactsAsync(FolkIdleDbContext db, long playerId)
        {
            var player = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => new
                {
                    p.CurrentLevel, p.PremiumDiamonds, p.ForgeFusionsCompleted, p.AffixRerollsPerformed,
                    p.DelveDeepestFloor, p.RebirthCount, p.GuildId,
                    p.AutoSalvageBelowTier, p.AutoSalvageRegionTiers,
                })
                .SingleOrDefaultAsync();
            if (player == null) return null;

            long gold = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => (long?)c.Quantity).SingleOrDefaultAsync() ?? 0L;

            var spend = await db.GoldSpendDaily.AsNoTracking()
                .Where(g => g.PlayerId == playerId)
                .GroupBy(g => g.Category)
                .Select(g => new { Category = g.Key, Total = g.Sum(x => x.Amount) })
                .ToListAsync();
            long Spent(GoldSpendCategory category)
                => spend.Where(s => s.Category == (short)category).Select(s => s.Total).FirstOrDefault();

            var buildings = await db.VillageInfrastructures.AsNoTracking()
                .Where(v => v.PlayerId == playerId)
                .Select(v => new { v.BuildingId, v.CurrentLevel, v.UpgradeTargetLevel })
                .ToListAsync();
            int LevelOf(int buildingId) => buildings.Where(b => b.BuildingId == buildingId).Select(b => b.CurrentLevel).FirstOrDefault();

            var defeated = await RegionUnlockGate.LoadDefeatedBossesAsync(db, playerId);

            int inheritanceLevels = await db.PlayerInheritanceStats.AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .SumAsync(r => (int?)r.Level) ?? 0;

            int ascension = await db.BossAscensionProgress.AsNoTracking()
                .Where(b => b.PlayerId == playerId)
                .MaxAsync(b => (int?)b.HighestStep) ?? 0;

            // Modul: the market's trade licence is guild membership
            // (MarketEscrowEngine refuses a listing with NoGuildLicense when
            // PlayerRecord.GuildId is not set), so the
            // step cannot be "available" to a player who has no guild - the
            // button it points at is disabled for them.
            long chestSales = await db.GoldIncomeDaily.AsNoTracking()
                .Where(g => g.PlayerId == playerId && g.Source == (short)GoldIncomeSource.ChestSale)
                .SumAsync(g => (long?)g.Amount) ?? 0L;
            bool inGuild = player.GuildId > 0;

            bool bossRow = await db.PlayerWorldBossAttempts.AsNoTracking()
                .AnyAsync(a => a.PlayerId == playerId && (a.AttemptCount > 0 || a.TotalInflictedDamage > 0));

            // Modul: an OPEN listing or a SOLD one. A cancelled listing leaves
            // nothing in either table, which is why the market step also has
            // the latch written at listing time (MarketEscrowEngine,
            // MarketOrderBookEngine) - this is the evidence for players who
            // listed before the latch existed.
            bool listing = await db.MarketOrderRecords.AsNoTracking()
                .AnyAsync(o => o.SellerId == playerId && o.OrderType == "SELL")
                || await db.HistoricalMarketArchives.AsNoTracking()
                    .AnyAsync(a => a.SellerId == playerId && a.OrderType == "SELL");

            return new QuestFacts
            {
                Level = player.CurrentLevel,
                Gold = gold,
                InGuild = inGuild,
                Diamonds = player.PremiumDiamonds,
                HighestUnlockedRegion = RegionUnlockGate.HighestUnlockedRegion(defeated),
                ForgeLevel = LevelOf(VillageManagementEngine.ForgeBuildingId),
                BreedingGroundsLevel = LevelOf(VillageManagementEngine.BreedingGroundsBuildingId),
                AnyVillageBuildingStarted = buildings.Any(b => b.CurrentLevel >= 1 || b.UpgradeTargetLevel > 0),
                FusionsCompleted = player.ForgeFusionsCompleted,
                GoldSpentOnFusion = Spent(GoldSpendCategory.Fusion),
                RerollsPerformed = player.AffixRerollsPerformed,
                GoldSpentOnReroll = Spent(GoldSpendCategory.Reroll),
                GoldSpentOnVillage = Spent(GoldSpendCategory.Village),
                GoldSpentOnBreeding = Spent(GoldSpendCategory.Breeding),
                GoldSpentOnDelve = Spent(GoldSpendCategory.Delve),
                InheritanceLevelsBought = inheritanceLevels,
                DelveDeepestFloor = player.DelveDeepestFloor,
                HasOpenOrSoldListing = listing,
                HasWorldBossAttemptRow = bossRow,
                HighestAscensionStep = ascension,
                RebirthCount = player.RebirthCount,
                GoldFromChestSales = chestSales,
                AutoSellRuleSet = player.AutoSalvageBelowTier > 0 || player.AutoSalvageRegionTiers != 0,
            };
        }

        /// <summary>
        /// The state of one step: claimed beats done beats unlocked. A step
        /// already DONE is never Locked - see QuestLineRegistry's remarks.
        /// </summary>
        public static QuestStepState StateOf(QuestStep step, QuestFacts facts, bool latched, bool claimed)
        {
            if (claimed) return QuestStepState.Claimed;
            if (latched || step.Done(facts)) return QuestStepState.Done;
            return step.Unlocked(facts) ? QuestStepState.Available : QuestStepState.Locked;
        }

        // ---------------------------------------------------------------
        // The latch
        // ---------------------------------------------------------------

        /// <summary>
        /// Records, once, that the player did <paramref name="stepId"/>. Called
        /// at the moment of the act for the two steps with no durable evidence
        /// of their own (market_list, world_boss), inside the act's own
        /// transaction so a rollback takes it too. Idempotent.
        /// </summary>
        public static async Task NoteFactAsync(FolkIdleDbContext db, long playerId, string stepId)
        {
            DateTime now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO quest_line_claims (""PlayerId"", ""StepId"", ""FactAtUtc"", ""GoldGranted"", ""MaterialQuantity"", ""Region"")
VALUES ({playerId}, {stepId}, {now}, 0, 0, 0)
ON CONFLICT (""PlayerId"", ""StepId"")
DO UPDATE SET ""FactAtUtc"" = COALESCE(quest_line_claims.""FactAtUtc"", EXCLUDED.""FactAtUtc"")");
        }

        // ---------------------------------------------------------------
        // The view
        // ---------------------------------------------------------------

        public static async Task<QuestLineView?> ViewAsync(FolkIdleDbContext db, long playerId, string? result = null)
        {
            var facts = await LoadFactsAsync(db, playerId);
            if (facts == null) return null;

            var rows = await db.QuestLineClaims.AsNoTracking()
                .Where(q => q.PlayerId == playerId)
                .ToListAsync();
            var byStep = rows.ToDictionary(r => r.StepId, StringComparer.Ordinal);

            var views = new List<QuestStepView>(QuestLineRegistry.Steps.Count);
            var toLatch = new List<string>();
            foreach (var step in QuestLineRegistry.Steps.OrderBy(s => s.Order))
            {
                byStep.TryGetValue(step.Id, out var row);
                bool latched = row?.FactAtUtc != null;
                bool claimed = row?.ClaimedAtUtc != null;
                var state = StateOf(step, facts, latched, claimed);

                // Modul: THE LATCH IS WRITTEN WHEN A GET SEES A STEP DONE. A
                // rebirth wipes the village and the codex, so a predicate
                // that was true yesterday can be false tomorrow; without the
                // latch a step ticked off before a rebirth would come back as
                // "to do". It is the one write a GET makes, it is idempotent,
                // and failing to make it only loses the latch, never the view.
                if ((state == QuestStepState.Done || state == QuestStepState.Claimed) && !latched) toLatch.Add(step.Id);

                views.Add(new QuestStepView(
                    step.Id, step.Order, step.Title, step.Explanation, step.Screen, step.GuideTargets,
                    state == QuestStepState.Locked ? step.UnlockHint : string.Empty,
                    state.ToString().ToLowerInvariant(),
                    state == QuestStepState.Done));
            }

            foreach (string id in toLatch)
            {
                try { await NoteFactAsync(db, playerId, id); }
                catch (Exception ex) { Console.WriteLine($"Quest line latch failed for player {playerId}, step {id}: {ex.Message}"); }
            }

            var reward = QuestLineRegistry.RewardFor(facts.HighestUnlockedRegion);
            return new QuestLineView(
                result,
                new QuestRewardView(reward.Region, reward.Gold, reward.MaterialLog, reward.MaterialOre, reward.MaterialQuantity),
                views.Count(v => v.State == "done" || v.State == "claimed"),
                views.Count(v => v.State == "claimed"),
                views.Count,
                views.ToArray());
        }

        // ---------------------------------------------------------------
        // The claim
        // ---------------------------------------------------------------

        public static async Task<QuestClaimResult> ClaimAsync(FolkIdleDbContext db, long playerId, string? stepId)
        {
            var step = QuestLineRegistry.Find(stepId);
            if (step == null) return QuestClaimResult.UnknownStep;

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var quarantine = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => new { p.IsQuarantined, p.Quarantine_Active })
                .SingleOrDefaultAsync();
            if (quarantine == null)
            {
                await tx.RollbackAsync();
                return QuestClaimResult.PlayerNotFound;
            }
            // Modul: a quarantined account has every command rejected on the
            // tick; a REST route has to ask for itself or it is the one door
            // the quarantine forgot (WorkshopCommissionEngine says the same).
            if (quarantine.IsQuarantined || quarantine.Quarantine_Active)
            {
                await tx.RollbackAsync();
                return QuestClaimResult.Restricted;
            }

            var facts = (await LoadFactsAsync(db, playerId))!;
            var row = await db.QuestLineClaims.AsNoTracking()
                .SingleOrDefaultAsync(q => q.PlayerId == playerId && q.StepId == step.Id);

            switch (StateOf(step, facts, row?.FactAtUtc != null, row?.ClaimedAtUtc != null))
            {
                case QuestStepState.Claimed:
                    await tx.RollbackAsync();
                    return QuestClaimResult.AlreadyClaimed;
                case QuestStepState.Locked:
                    await tx.RollbackAsync();
                    return QuestClaimResult.Locked;
                case QuestStepState.Available:
                    await tx.RollbackAsync();
                    return QuestClaimResult.NotDone;
            }

            var reward = QuestLineRegistry.RewardFor(facts.HighestUnlockedRegion);
            DateTime now = DateTime.UtcNow;

            // The guard. See the class remarks: zero rows touched means somebody
            // else claimed between the check above and here.
            int touched = await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO quest_line_claims (""PlayerId"", ""StepId"", ""FactAtUtc"", ""ClaimedAtUtc"", ""GoldGranted"", ""MaterialQuantity"", ""Region"")
VALUES ({playerId}, {step.Id}, {now}, {now}, {reward.Gold}, {reward.MaterialQuantity}, {reward.Region})
ON CONFLICT (""PlayerId"", ""StepId"")
DO UPDATE SET ""ClaimedAtUtc"" = EXCLUDED.""ClaimedAtUtc"",
              ""FactAtUtc"" = COALESCE(quest_line_claims.""FactAtUtc"", EXCLUDED.""FactAtUtc""),
              ""GoldGranted"" = EXCLUDED.""GoldGranted"",
              ""MaterialQuantity"" = EXCLUDED.""MaterialQuantity"",
              ""Region"" = EXCLUDED.""Region""
WHERE quest_line_claims.""ClaimedAtUtc"" IS NULL");
            if (touched == 0)
            {
                await tx.RollbackAsync();
                return QuestClaimResult.AlreadyClaimed;
            }

            await CommodityLedger.AddAsync(db, playerId, "gold", reward.Gold);
            await GoldLedger.RecordIncomeAsync(db, playerId, GoldIncomeSource.QuestLine, reward.Gold);

            var materials = new[]
            {
                new KeyValuePair<string, long>(reward.MaterialLog, reward.MaterialQuantity),
                new KeyValuePair<string, long>(reward.MaterialOre, reward.MaterialQuantity),
            };
            await CommodityLedger.AddManyAsync(db, playerId, materials);
            await MaterialLedger.RecordManyAsync(db, playerId, MaterialFlowDirection.Gathered, materials);

            await tx.CommitAsync();
            return QuestClaimResult.Ok;
        }

        // ---------------------------------------------------------------
        // Dev tool
        // ---------------------------------------------------------------

        /// <summary>
        /// DEV TOOLS ONLY (POST /api/v1/dev/quests/unclaim): takes back exactly
        /// what a claim paid and clears the claim, so exercise.mjs can claim on
        /// the dev fixture and leave it as it found it. The latch stays - the
        /// step is still DONE, it is only claimable again.
        /// </summary>
        public static async Task<bool> DevUnclaimAsync(FolkIdleDbContext db, long playerId, string stepId)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var row = await db.QuestLineClaims.AsNoTracking()
                .SingleOrDefaultAsync(q => q.PlayerId == playerId && q.StepId == stepId);
            if (row?.ClaimedAtUtc == null)
            {
                await tx.RollbackAsync();
                return false;
            }

            var reward = QuestLineRegistry.RewardFor(row.Region);
            // GoldLedger: a dev tool reversing a grant it recorded - neither income nor spend.
            await CommodityLedger.AddAsync(db, playerId, "gold", -row.GoldGranted);
            await CommodityLedger.AddAsync(db, playerId, reward.MaterialLog, -row.MaterialQuantity);
            await CommodityLedger.AddAsync(db, playerId, reward.MaterialOre, -row.MaterialQuantity);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE quest_line_claims SET ""ClaimedAtUtc"" = NULL, ""GoldGranted"" = 0, ""MaterialQuantity"" = 0
WHERE ""PlayerId"" = {playerId} AND ""StepId"" = {stepId}");
            await tx.CommitAsync();
            return true;
        }
    }
}
