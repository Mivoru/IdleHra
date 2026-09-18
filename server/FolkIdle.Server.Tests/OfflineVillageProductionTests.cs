using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Offline village production used to fail with a bare
    /// `catch { await transaction.RollbackAsync(); }` - no log line, no
    /// counter, nothing. A transient database error (a dropped Supabase
    /// pooler connection, a serialization failure) made a player's earned
    /// wood/ore/gold vanish between the offline summary and the checkpoint
    /// with zero signal anywhere - not the log, not the summary, not a
    /// counter a dashboard could alert on (audit #17).
    ///
    /// Forces the failure the same way LootWorkerResilienceTests forces a
    /// real database failure for CombatLootEngine: a state that makes one of
    /// the method's own `SingleOrDefaultAsync` reads throw, rather than
    /// poking at the connection string. CommodityRecords has an index on
    /// (PlayerId, ItemId) but no UNIQUE constraint enforcing one row per
    /// player+item, so two seeded rows reproduce the same
    /// "Sequence contains more than one element" a live race would also
    /// produce.
    /// </summary>
    [Collection("Postgres collection")]
    public class OfflineVillageProductionTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _o;

        public OfflineVillageProductionTests(PostgresTestFixture fixture, ITestOutputHelper o)
        {
            _fixture = fixture;
            _o = o;
            ContentRegistry.Initialize();
        }

        private static readonly MethodInfo GrantVillagePassiveProductionMethod =
            typeof(OfflineSimulationEngine).GetMethod(
                "GrantVillagePassiveProductionAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;

        private static long ReadFailureCounter() =>
            (long)typeof(OfflineSimulationEngine)
                .GetProperty("VillageProductionFailures", BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;

        [Fact]
        public async Task AFailedGrantIsLoggedAndCounted()
        {
            const long playerId = 974_000_001L;

            // Two rows for the same (PlayerId, ItemId = "gold") - legitimate
            // seed data that reaches the same throw a live race would, without
            // touching the schema or the connection.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 0L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 0L });
                await db.SaveChangesAsync();
            }

            long before = ReadFailureCounter();

            var captured = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(captured);
            try
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

                // lumberjackLevel/mineLevel 0 so the wood/ore branches never
                // run (their rate is 0 at level 0); townHallLevel 0 still
                // pays 50 gold/hour (GetTownHallGoldRatePerHour floors at
                // level <= 1), so 3600 elapsed seconds earns exactly 50 gold
                // and reaches the duplicate-row read above.
                await (Task)GrantVillagePassiveProductionMethod.Invoke(
                    null, new object[] { db, playerId, 0, 0, 1, 0, 3600L })!;
            }
            finally
            {
                Console.SetOut(previous);
            }

            string output = captured.ToString();
            _o.WriteLine($"captured: [{output.Trim()}]");

            Assert.Equal(before + 1, ReadFailureCounter());
            Assert.Contains(playerId.ToString(), output);
            Assert.Contains("50", output); // the lost gold amount
        }
    }
}
