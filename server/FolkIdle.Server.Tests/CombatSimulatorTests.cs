using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Phase 2b.2 (roadmap 5). CombatSimulator jumps over the ticks where
    /// nothing happens and runs the real RunCombatTick on the rest. Because it
    /// never skips a tick that rolls dice, a seeded window must come out
    /// IDENTICAL to the same window ticked one tick at a time - every byte of
    /// the payload and everything the window would have sent. That equality is
    /// the whole guard on QuietTicksAhead: a tick wrongly called quiet shifts
    /// a swing, a bite or a ration, and the two runs part.
    /// </summary>
    [Collection("Static loot queues")]
    public class CombatSimulatorTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public CombatSimulatorTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private const long Hour = 36_000;

        public static IEnumerable<object[]> Rows() => OfflineCombatParityTests.Rows();

        private static byte[] Bytes(in TickStatePayload payload)
        {
            var copy = payload;
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
        }

        private static string Sent(CombatSimulator.Tally t) =>
            $"kills {t.Kills} paid {t.PaidKills} boss {t.BossKills} asc {t.AscensionClears} seasonal {t.SeasonalBossClears} "
            + $"drops {t.DropRequests.Count}/{t.DropRequests.Sum(d => (long)d.Kills)} codex {t.CodexKills.Count}/{t.CodexKills.Sum(k => k.GainedXp)} "
            + $"pets {string.Join(",", t.PetDrops)}";

        [Fact]
        public void ThePayloadCanBeComparedByteForByte()
        {
            Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<TickStatePayload>());
        }

        [Theory]
        [MemberData(nameof(Rows))]
        public void AJumpingWindow_IsTheTickedWindow_Exactly(string row, int region, bool geared, int monsterId, int food, int variant)
        {
            for (int seed = 1; seed <= 3; seed++)
            {
                var ticked = OfflineCombatParityTests.Build(region, geared, monsterId, food, variant);
                var jumped = ticked;
                var reference = CombatSimulator.Run(ref ticked, Hour, new Random(seed), tickEveryTick: true);
                var simulated = CombatSimulator.Run(ref jumped, Hour, new Random(seed));

                Assert.Equal(reference.TicksRun, simulated.TicksRun);
                Assert.Equal(reference.Died, simulated.Died);
                Assert.Equal(reference.LeftTheFight, simulated.LeftTheFight);
                Assert.Equal(Sent(reference.Tally), Sent(simulated.Tally));
                Assert.True(Bytes(in ticked).AsSpan().SequenceEqual(Bytes(in jumped)),
                    $"{row} seed {seed}: the payloads differ (kills {reference.Tally.Kills} / {simulated.Tally.Kills}, gold {ticked.CurrentGold} / {jumped.CurrentGold}, hp {ticked.PlayerHp} / {jumped.PlayerHp})");
            }
        }

        /// <summary>
        /// Rows the parity set never reaches: a bar that sits UNDER the eat
        /// threshold while the bite cooldown runs, so the bite falls due between
        /// two swings. A high threshold and a weak fish against a monster a region
        /// up put the character there all hour. Found by mutating
        /// QuietTicksAhead: with only the parity rows, eating one tick late passed.
        /// </summary>
        public static IEnumerable<object[]> HungryBiteRows()
        {
            foreach (int region in new[] { 1, 2, 3, 4 })
            foreach (bool geared in new[] { false, true })
                yield return new object[] { region, geared };
        }

        [Theory]
        [MemberData(nameof(HungryBiteRows))]
        public void ABiteDueBetweenSwings_IsEatenOnItsTick(int region, bool geared)
        {
            int monsterId = ContentRegistry.FirstCanonicalMonsterId + (region * 5) + 1;
            for (int seed = 1; seed <= 3; seed++)
            {
                var ticked = OfflineCombatParityTests.Build(region, geared, monsterId, 20_000, 0);
                ticked.AutoEatThreshold = 90;
                ticked.Food1_ItemId = FoodRegistry.FirstRawFishOfTier(1);
                var jumped = ticked;
                var reference = CombatSimulator.Run(ref ticked, Hour, new Random(seed), tickEveryTick: true);
                var simulated = CombatSimulator.Run(ref jumped, Hour, new Random(seed));
                Assert.Equal(reference.TicksRun, simulated.TicksRun);
                Assert.Equal(Sent(reference.Tally), Sent(simulated.Tally));
                Assert.True(Bytes(in ticked).AsSpan().SequenceEqual(Bytes(in jumped)),
                    $"r{region} {(geared ? "geared" : "bare")} seed {seed}: food {OfflineCombatParityTests.FoodCount(in ticked)} / {OfflineCombatParityTests.FoodCount(in jumped)}, hp {ticked.PlayerHp} / {jumped.PlayerHp}");
            }
        }

        private static void AssertSameWindow(TickStatePayload start, string what)
        {
            for (int seed = 1; seed <= 3; seed++)
            {
                var ticked = start;
                var jumped = start;
                var reference = CombatSimulator.Run(ref ticked, Hour, new Random(seed), tickEveryTick: true);
                var simulated = CombatSimulator.Run(ref jumped, Hour, new Random(seed));
                Assert.Equal(reference.TicksRun, simulated.TicksRun);
                Assert.Equal(reference.Died, simulated.Died);
                Assert.Equal(Sent(reference.Tally), Sent(simulated.Tally));
                Assert.True(Bytes(in ticked).AsSpan().SequenceEqual(Bytes(in jumped)), $"{what} seed {seed}: the payloads differ");
            }
        }

        /// <summary>
        /// Last Stand's hour counts down across the jumped ticks: a crown that
        /// starts 30 seconds from ready must be ready when a hungry character's
        /// killing blow comes minutes later.
        /// </summary>
        [Fact]
        public void LastStandsCooldown_RunsDownAcrossTheJump()
        {
            var start = OfflineCombatParityTests.Build(2, true, ContentRegistry.FirstCanonicalMonsterId + 7, 0, 0);
            start = TestTree.With(start, (WarTreeRegistry.LastStand, 1));
            start.LastStandCooldownRemaining = 300;
            AssertSameWindow(start, "last stand");
        }

        /// <summary>
        /// Skill tree v3 step B: the bleed pays on every tenth tick of the fight
        /// clock, so a jump must stop there; Sunder and the kill streak only
        /// change on swings and kills. Each is held to the ticked window exactly,
        /// on a fast region-3 farm and a slow region-5 one.
        /// </summary>
        [Theory]
        [InlineData(3, "lacerate")]
        [InlineData(5, "lacerate")]
        [InlineData(3, "sunder")]
        [InlineData(3, "momentum")]
        [InlineData(5, "all three")]
        public void TheFightShapedMechanics_JumpExactly(int region, string which)
        {
            int monster = ContentRegistry.FirstCanonicalMonsterId + ((region - 1) * 5) + 2;
            var start = OfflineCombatParityTests.Build(region, true, monster, 20_000, 0);
            start = which switch
            {
                "lacerate" => TestTree.With(start, (WarTreeRegistry.Lacerate, 3)),
                "sunder" => TestTree.With(start, (WarTreeRegistry.Sunder, 3)),
                "momentum" => TestTree.With(start, (WarTreeRegistry.Momentum, 3)),
                _ => TestTree.With(start, (WarTreeRegistry.Lacerate, 3), (WarTreeRegistry.Sunder, 3), (WarTreeRegistry.Momentum, 3)),
            };
            AssertSameWindow(start, $"{which} r{region}");
        }

        /// <summary>What the bleed costs the jump: ticks run per hour with and without it, printed.</summary>
        [Fact]
        public void TheBleed_CostsTheJumpLittle()
        {
            var bare = OfflineCombatParityTests.Build(5, true, ContentRegistry.FirstCanonicalMonsterId + 22, 20_000, 0);
            var bled = TestTree.With(bare, (WarTreeRegistry.Lacerate, 3));
            var a = CombatSimulator.Run(ref bare, 12 * Hour, new Random(7));
            var b = CombatSimulator.Run(ref bled, 12 * Hour, new Random(7));
            _output.WriteLine($"12 h ticked: {a.TicksTicked} without the bleed, {b.TicksTicked} with it ({100.0 * b.TicksTicked / Math.Max(1, a.TicksTicked):F0}%)");
            Assert.True(b.TicksTicked * 3 < b.TicksRun, $"ticked {b.TicksTicked} of {b.TicksRun}");
        }

        [Fact]
        public void TheJump_RunsFarFewerTicksThanTheWindow()
        {
            // A geared region-5 character, fed, on a region-5 regular: 12 hours.
            var payload = OfflineCombatParityTests.Build(5, true, ContentRegistry.FirstCanonicalMonsterId + 22, 20_000, 0);
            var outcome = CombatSimulator.Run(ref payload, 12 * Hour, new Random(7));
            Assert.Equal(12 * Hour, outcome.TicksRun);
            Assert.True(outcome.TicksTicked * 3 < outcome.TicksRun,
                $"ticked {outcome.TicksTicked} of {outcome.TicksRun}");
        }

        [Fact]
        public void ASimulatedWindow_SendsNothingLive()
        {
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            while (PetEngine.Drops.TryDequeue(out _)) { }
            int feedBefore = CombatEventFeed.PendingCount;

            var payload = OfflineCombatParityTests.Build(3, true, ContentRegistry.FirstCanonicalMonsterId + 12, 20_000, 3);
            var outcome = CombatSimulator.Run(ref payload, Hour, new Random(3));

            Assert.True(outcome.Tally.Kills > 100);
            Assert.True(outcome.Tally.DropRequests.Count > 0);
            Assert.Empty(CombatLootEngine.DropRequestQueue);
            Assert.Empty(CodexEngine.KillEventQueue);
            Assert.Empty(PetEngine.Drops);
            Assert.Equal(feedBefore, CombatEventFeed.PendingCount);
            Assert.Null(SimulationEngine.Simulating);
        }
    }
}
