using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: ONE AUTO-REROLL RUN IS ONE EVENT, 2026-09-12.
    ///
    /// Reported from a phone: "on mobile I get many notifications (green done)
    /// when I do rerolls, because every reroll it's popping next notification".
    /// Exactly that - ExecuteRerollAsync ended with an EnqueueCommandResult of
    /// Success INSIDE each attempt, so a fifty-attempt run was fifty toasts, one
    /// of which the player had asked for.
    ///
    /// The other half is what the single remaining message is allowed to say. It
    /// has to describe the run that happened: how many attempts were committed,
    /// what they cost, and what the affix ended up as. The loop used to read that
    /// from instance fields assigned before the commit, so the report could
    /// describe a roll that had been rolled back - see AutoRerollRunnerTests.
    /// The last test here is the end-to-end form of that: what the run REPORTS
    /// must be what is in the row.
    /// </summary>
    [Collection("Postgres collection")]
    public class AutoRerollRunReportTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        // Region 1 chest, so the reroll price is the region-1 entry of the flat
        // cost table. Asked of AffixRegistry below rather than written as 1000.
        private const string RegionOneChest = "eq_linen_shroud_chest_armor_slot_base";

        public AutoRerollRunReportTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
            ContentRegistry.Initialize();
        }

        private async Task<long> SeedRerollableItemAsync(long playerId, long gold)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid()
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = gold });

            var equipment = new EquipmentInstance
            {
                BaseItemId = RegionOneChest,
                PlayerId = playerId,
                QualityTier = 1,
                AffixPayload = "{\"flat_hp@1\":50}",
                IsAffixLocked = false
            };
            db.EquipmentInstances.Add(equipment);
            await db.SaveChangesAsync();
            return equipment.Id;
        }

        /// <summary>
        /// Deterministic whatever the dice do: the run either stops early on its
        /// condition or exhausts its attempts, and both are ONE event.
        /// </summary>
        [Fact]
        public async Task AnAutoRerollRunReportsExactlyOneCommandResult()
        {
            const long playerId = 990000811L;
            long equipmentId = await SeedRerollableItemAsync(playerId, 50_000_000L);

            var registry = new PlayerSessionRegistry();
            var engine = new AffixRerollEngine(_fixture.ServiceProvider, registry);

            var run = await engine.ExecuteAutoRerollAsync(
                playerId,
                equipmentId,
                affixIndex: 0,
                operation: RerollOperation.Full,
                stopCondition: new AutoRerollStopCondition(AffixRarity.Legendary, "flat_armor"),
                maxAttempts: 10);

            _output.WriteLine($"{run.Reason} after {run.AttemptsCommitted} attempts for {run.GoldSpent}g");

            Assert.Single(registry.CommandResultQueue);

            // And that one message says which of the two honest endings it was,
            // rather than the bare Success a single reroll reports. A run that
            // spent fifty attempts and found nothing is not a failure, but it is
            // also not "Done".
            Assert.True(registry.CommandResultQueue.TryPeek(out var result));
            Assert.Contains(
                (FolkIdle.Server.Network.CommandResultCode)result.ResultCode,
                new[]
                {
                    FolkIdle.Server.Network.CommandResultCode.AutoRerollConditionMet,
                    FolkIdle.Server.Network.CommandResultCode.AutoRerollAttemptsSpent
                });
        }

        /// <summary>
        /// "I would only be paying for 5 rerolls, because I only did 5/50."
        /// </summary>
        [Fact]
        public async Task TheRunChargesAndReportsOnlyTheAttemptsItPerformed()
        {
            const long playerId = 990000812L;
            const long startingGold = 50_000_000L;
            long equipmentId = await SeedRerollableItemAsync(playerId, startingGold);

            var engine = new AffixRerollEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            var run = await engine.ExecuteAutoRerollAsync(
                playerId,
                equipmentId,
                affixIndex: 0,
                operation: RerollOperation.Full,
                stopCondition: new AutoRerollStopCondition(AffixRarity.Legendary, "flat_armor"),
                maxAttempts: 10);

            long fee = AffixRegistry.CalculateRerollGoldCost(1, 0, rerollStatType: false);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var goldRow = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");

            Assert.InRange(run.AttemptsCommitted, 1, 10);
            Assert.Equal(run.AttemptsCommitted * fee, startingGold - goldRow.Quantity);
            Assert.Equal(run.AttemptsCommitted * fee, run.GoldSpent);
        }

        /// <summary>
        /// THE DEFECT, end to end. What the run says it landed on must be what
        /// the row holds - a report taken from pre-commit state can disagree,
        /// and a run that stopped on a rolled-back Legendary is the reported
        /// "it skipped the legendary".
        /// </summary>
        [Fact]
        public async Task TheReportedResultIsTheOneInTheDatabase()
        {
            const long playerId = 990000813L;
            long equipmentId = await SeedRerollableItemAsync(playerId, 50_000_000L);

            var engine = new AffixRerollEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());

            var run = await engine.ExecuteAutoRerollAsync(
                playerId,
                equipmentId,
                affixIndex: 0,
                operation: RerollOperation.Full,
                stopCondition: new AutoRerollStopCondition(AffixRarity.Legendary, "flat_armor"),
                maxAttempts: 10);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var item = await verifyDb.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == equipmentId);

            var payload = JsonNode.Parse(item.AffixPayload) as JsonObject;
            Assert.NotNull(payload);

            string storedKey = string.Empty;
            foreach (var entry in payload!)
            {
                if (entry.Key == "is_affix_locked" || entry.Value == null) continue;
                storedKey = entry.Key;
                break;
            }

            Assert.Equal(AffixRegistry.StripStackSuffix(storedKey), run.FinalAffixId);
            Assert.Equal(AffixRegistry.ParseRarity(storedKey), run.FinalRarity);

            // And if it reported stopping on the condition, the condition really
            // is met by what is stored.
            if (run.Reason == AutoRerollStopReason.ConditionMet)
            {
                Assert.Equal("flat_armor", run.FinalAffixId);
                Assert.Equal(AffixRarity.Legendary, run.FinalRarity);
            }
        }

        /// <summary>
        /// A chest can only ever roll flat_hp or flat_armor, so asking it for
        /// crit chance is a run that can never finish. It must cost nothing and
        /// say which kind of refusal it was, rather than spending a budget to
        /// find out - and the dropdown that offered the combination is what made
        /// the feature look dead.
        /// </summary>
        [Fact]
        public async Task AStatTheItemCanNeverRollIsRefusedBeforeAnythingIsSpent()
        {
            const long playerId = 990000814L;
            const long startingGold = 50_000_000L;
            long equipmentId = await SeedRerollableItemAsync(playerId, startingGold);

            var registry = new PlayerSessionRegistry();
            var engine = new AffixRerollEngine(_fixture.ServiceProvider, registry);

            var run = await engine.ExecuteAutoRerollAsync(
                playerId,
                equipmentId,
                affixIndex: 0,
                operation: RerollOperation.Full,
                stopCondition: new AutoRerollStopCondition(AffixRarity.Legendary, "crit_chance_pct"),
                maxAttempts: 50);

            Assert.Equal(AutoRerollStopReason.RejectedUnreachableCondition, run.Reason);
            Assert.Equal(0, run.AttemptsCommitted);
            Assert.Equal(0L, run.GoldSpent);
            Assert.Single(registry.CommandResultQueue);

            // Its own code, because "the server rejected that" is the message
            // that made this look like a dead feature. The player needs to be
            // told the target is impossible on this item.
            Assert.True(registry.CommandResultQueue.TryPeek(out var result));
            Assert.Equal(
                (byte)FolkIdle.Server.Network.CommandResultCode.AutoRerollConditionImpossible,
                result.ResultCode);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var goldRow = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            Assert.Equal(startingGold, goldRow.Quantity);
        }
    }
}
