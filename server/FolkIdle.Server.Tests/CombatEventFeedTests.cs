using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The combat event feed says what the snapshot stream cannot.
    ///
    /// WHY THIS FEED EXISTS, and therefore what these tests are really
    /// guarding: measured on 2026-09-04, a geared character killed an early
    /// monster every ~1400 ms while StateUpdate snapshots arrived every
    /// ~1090 ms, so across 27 consecutive snapshots CurrentMonsterHp took
    /// exactly ONE value - its full health. Spawn and death both happened
    /// between two samples. The client infers every hit from the difference
    /// between two snapshots, so it had nothing to infer and the monster's
    /// health bar could not move however correct the bar was.
    ///
    /// THE HEADLINE ASSERTION IS THE MISS. A miss moves no health at all, so
    /// it is the one event that no amount of cleverness on the client can
    /// derive from a health difference - which makes it the proof that this
    /// feed is a real report from the simulation and not a re-derivation of
    /// something already on the wire. If the miss test is the only one left
    /// standing, keep that one.
    ///
    /// Deliberately fixture-free: the tick is a static method over a struct,
    /// so this needs no Postgres and no Redis.
    /// </summary>
    public class CombatEventFeedTests
    {
        private readonly ITestOutputHelper _output;

        public CombatEventFeedTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
            ActiveSkillEngine.Initialize();
        }

        private const int TicksPerSecond = 10;

        // Not 1. CombatEventFeed is a process-wide static queue and other test
        // classes drive ProcessSubTick concurrently under the same default
        // player id, so everything here filters on an id nothing else uses.
        private const long TestPlayerId = 987_654;

        private static int FirstFoodItemId()
        {
            foreach (int id in ContentRegistry.RawFishItemIds)
            {
                return id;
            }

            throw new InvalidOperationException("no food in the catalogue to stock the larder with");
        }

        private static TickStatePayload FreshFighter(int monsterId)
        {
            var payload = new TickStatePayload
            {
                PlayerId = TestPlayerId,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = monsterId,
                CurrentMonsterId = monsterId,
                CurrentMonsterHp = (long)ContentRegistry.GetScaledMonsterMaxHp(monsterId) * 1000L,
                InventorySpaceRemaining = int.MaxValue,
                PlayerHp = 100_000,
                TownHallLevel = 1,
                // Stocked, for the same reason ProgressionRateTests stocks it:
                // an unfed character dies in about thirty seconds and the run
                // measures a corpse rather than a fight.
                Food1_ItemId = FirstFoodItemId(),
                Food1_Count = 1_000_000,
                AutoEatThreshold = 50,
            };
            payload.SetGold(0);
            return payload;
        }

        /// <summary>
        /// Runs the real tick and collects this player's events.
        ///
        /// Drains EVERY tick rather than once at the end, for two reasons: the
        /// queue is bounded at 2048 and drops when full, and it is shared with
        /// whatever else the test run is ticking at the same time. Draining as
        /// we go keeps it empty enough that nothing of ours is dropped.
        /// </summary>
        private static List<ResponseCombatEventPacket> RunFight(ref TickStatePayload payload, int seconds)
        {
            var queue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();
            var mine = new List<ResponseCombatEventPacket>();

            CombatEventFeed.Clear();

            for (int tick = 0; tick < seconds * TicksPerSecond; tick++)
            {
                SimulationEngine.ProcessSubTick(ref payload, 100, 100, queue, contexts);

                while (CombatEventFeed.TryDequeue(out ResponseCombatEventPacket packet))
                {
                    if (packet.PlayerId == TestPlayerId)
                    {
                        mine.Add(packet);
                    }
                }
            }

            return mine;
        }

        [Fact]
        public void AResolvedMissIsReported_WhichNoHealthDifferenceCouldEverImply()
        {
            var payload = FreshFighter(91);
            List<ResponseCombatEventPacket> events = RunFight(ref payload, seconds: 900);

            int swings = events.Count(e => e.EventKind == ResponseCombatEventPacket.KindPlayerHit
                                        || e.EventKind == ResponseCombatEventPacket.KindPlayerMiss);
            int misses = events.Count(e => e.EventKind == ResponseCombatEventPacket.KindPlayerMiss);

            _output.WriteLine($"{swings} swings, {misses} of them missed");

            // Hit chance is clamped to a maximum of 0.95 (CombatDamageModel and
            // the tick agree), so at least one swing in twenty misses however
            // accurate the character is. Over hundreds of swings the chance of
            // seeing none is smaller than any other way this test could fail.
            Assert.True(swings > 200, $"the fight was too short to be conclusive: {swings} swings");
            Assert.True(misses > 0, "no miss was ever reported, so the feed cannot be reporting the hit roll");
        }

        [Fact]
        public void EveryEventCarriesTheMonstersHealthAtTheMomentItResolved()
        {
            var payload = FreshFighter(91);
            List<ResponseCombatEventPacket> events = RunFight(ref payload, seconds: 120);

            Assert.NotEmpty(events);

            int monsterMaxHp = ContentRegistry.GetScaledMonsterMaxHp(91);
            foreach (var e in events)
            {
                Assert.InRange(e.MonsterHpAfter, 0, monsterMaxHp);

                // In whole hit points, not milli. A hit that reported milli
                // would be a thousand times the monster's whole health and is
                // the single easiest mistake to make on this path.
                Assert.True(e.Amount <= monsterMaxHp * 10,
                    $"event kind {e.EventKind} reported {e.Amount}, which looks like milli-damage");
            }
        }

        [Fact]
        public void AKillIsAnnounced_BecauseOnAFastFightItIsTheOnlyMomentThereIs()
        {
            var payload = FreshFighter(91);
            List<ResponseCombatEventPacket> events = RunFight(ref payload, seconds: 600);

            var kills = events.Where(e => e.EventKind == ResponseCombatEventPacket.KindKill).ToList();
            _output.WriteLine($"{kills.Count} kills reported over 600 simulated seconds");

            Assert.NotEmpty(kills);
            Assert.All(kills, k => Assert.Equal(0, k.MonsterHpAfter));
            Assert.All(kills, k => Assert.True(k.Amount > 0, "a kill should report the xp it paid"));
        }

        [Fact]
        public void TheMonsterFightsBack_AndItsSwingsAreReportedToo()
        {
            var payload = FreshFighter(91);
            List<ResponseCombatEventPacket> events = RunFight(ref payload, seconds: 300);

            int monsterSwings = events.Count(e => e.EventKind == ResponseCombatEventPacket.KindMonsterHit
                                               || e.EventKind == ResponseCombatEventPacket.KindMonsterMiss);

            Assert.True(monsterSwings > 0,
                "the monster attacks on its own cadence and none of it reached the feed");
        }

        [Fact]
        public void SequenceNumbersOnlyEverIncrease()
        {
            var payload = FreshFighter(91);
            List<ResponseCombatEventPacket> events = RunFight(ref payload, seconds: 120);

            Assert.NotEmpty(events);
            for (int i = 1; i < events.Count; i++)
            {
                Assert.True(events[i].Sequence > events[i - 1].Sequence,
                    $"sequence went {events[i - 1].Sequence} -> {events[i].Sequence}; the client uses this to order and to drop replays");
            }
        }

        [Fact]
        public void TheQueueDropsRatherThanGrowingWithoutLimit()
        {
            CombatEventFeed.Clear();

            for (int i = 0; i < CombatEventFeed.MaxPending + 500; i++)
            {
                CombatEventFeed.Publish(TestPlayerId, 91, ResponseCombatEventPacket.KindPlayerHit, 1, 1);
            }

            // An idle game has a permanent producer on this queue, so an
            // unbounded one is a leak with extra steps. A combat line is
            // worthless a few seconds after the blow it describes.
            Assert.True(CombatEventFeed.PendingCount <= CombatEventFeed.MaxPending);
            Assert.True(CombatEventFeed.DroppedCount > 0);

            CombatEventFeed.Clear();
        }
    }
}
