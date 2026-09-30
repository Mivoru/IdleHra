using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 85: the rules themselves - storage, the level gate, the ladder, and
    /// each rule acting through the function every path calls.
    /// </summary>
    public class AutomationRulesTests
    {
        public AutomationRulesTests()
        {
            ContentRegistry.Initialize();
        }

        private static AutomationRules.Rule Fish(int node = 3001) => new() { Type = AutomationRules.FishWhenLarderDry, Param = node - (int)ActivityIdBands.FishingBand };
        private static AutomationRules.Rule StepDown => new() { Type = AutomationRules.StepDownOnDeath };
        private static AutomationRules.Rule Fuse(int tier) => new() { Type = AutomationRules.AutoFuseToTier, Param = tier };
        private static AutomationRules.Rule Empty => default;

        [Fact]
        public void ThreeRulesSurviveThePackedColumn()
        {
            var rules = new[] { Fish(3005), StepDown, Fuse(14) };
            long packed = AutomationRules.Pack(rules);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(rules[i].Type, AutomationRules.Unpack(packed, i).Type);
                Assert.Equal(rules[i].Param, AutomationRules.Unpack(packed, i).Param);
            }
            Assert.Equal(0L, AutomationRules.Pack(new[] { Empty, Empty, Empty }));
        }

        [Theory]
        [InlineData(19, 0, false)]
        [InlineData(20, 0, true)]
        [InlineData(39, 1, false)]
        [InlineData(40, 1, true)]
        [InlineData(59, 2, false)]
        [InlineData(60, 2, true)]
        public void EachSlotOpensAtItsLevel(int level, int slot, bool open)
        {
            Assert.Equal(open, AutomationRules.IsSlotUnlocked(slot, level));
        }

        [Fact]
        public void TheSetterRefusesWhatTheOwnerDidNotGrant()
        {
            Assert.Null(AutomationRules.Validate(new[] { Fish(), StepDown, Fuse(5) }, 60));
            Assert.Null(AutomationRules.Validate(new[] { StepDown, Empty, Empty }, 20));
            Assert.Equal("SlotLocked", AutomationRules.Validate(new[] { StepDown, Empty, Empty }, 19));
            Assert.Equal("SlotLocked", AutomationRules.Validate(new[] { Empty, StepDown, Empty }, 39));
            Assert.Equal("DuplicateRule", AutomationRules.Validate(new[] { StepDown, StepDown, Empty }, 60));
            Assert.Equal("NotAFishingSpot", AutomationRules.Validate(new[] { new AutomationRules.Rule { Type = AutomationRules.FishWhenLarderDry, Param = 0 }, Empty, Empty }, 60));
            Assert.Equal("NotAFishingSpot", AutomationRules.Validate(new[] { Fish(3999), Empty, Empty }, 60));
            Assert.Equal("TierOutOfRange", AutomationRules.Validate(new[] { Fuse(1), Empty, Empty }, 60));
            Assert.Equal("TierOutOfRange", AutomationRules.Validate(new[] { Fuse(15), Empty, Empty }, 60));
            Assert.Equal("UnknownRule", AutomationRules.Validate(new[] { new AutomationRules.Rule { Type = 9 }, Empty, Empty }, 60));
            Assert.Equal("ParamWithoutRule", AutomationRules.Validate(new[] { new AutomationRules.Rule { Param = 4 }, Empty, Empty }, 60));
            Assert.Equal("WrongSlotCount", AutomationRules.Validate(new[] { StepDown }, 60));
        }

        [Fact]
        public void ARuleAboveTheLevelIsKeptButInert()
        {
            // A rebirth drops the level and keeps the rules: the gate is read
            // when the rule acts.
            long packed = AutomationRules.Pack(new[] { Empty, Empty, Fuse(6) });
            Assert.False(AutomationRules.TryGetActive(packed, 1, AutomationRules.AutoFuseToTier, out _));
            Assert.True(AutomationRules.TryGetActive(packed, 60, AutomationRules.AutoFuseToTier, out int tier));
            Assert.Equal(6, tier);
        }

        [Theory]
        [InlineData(92, 91)] // a regular steps to the regular before it
        [InlineData(91, 0)]  // the bottom of the ladder
        [InlineData(95, 94)] // a boss steps to its own region's strongest regular
        [InlineData(96, 94)] // a region's first regular skips the boss behind it
        [InlineData(115, 114)]
        [InlineData(50, 0)]  // legacy monsters are not on the ladder
        public void OneMonsterEasierIsThePreviousRegular(int from, int expected)
        {
            Assert.Equal(expected, AutomationRules.EasierMonster(from));
        }

        private static TickStatePayload Fighter(long rules, int level = 60, int monster = 93)
        {
            return new TickStatePayload
            {
                PlayerId = 985_000L,
                CurrentLevel = level,
                ActiveActivityId = monster,
                CurrentMonsterId = monster,
                AutomationRules = rules,
                HighestLocationReached = 1,
                PlayerHp = 1000,
            };
        }

        [Fact]
        public void ADeathWithTheRule_RespawnsOneMonsterDown_AndIsStillADeath()
        {
            var payload = Fighter(AutomationRules.Pack(new[] { StepDown, Empty, Empty }));
            SimulationEngine.ApplyCombatDeath(ref payload, 93, 5000);

            Assert.Equal(92, payload.ActiveActivityId);
            Assert.Equal(Network.ActivityHaltReason.AutomationSteppedDown, payload.ActivityHaltReason);
            Assert.Equal(1, payload.LifetimeDeaths);
            Assert.Equal(93, payload.LastDeathMonsterId);
            Assert.Equal(5000, payload.PlayerHp);
            Assert.Equal(0, payload.CurrentMonsterId);
        }

        [Fact]
        public void ADeathWithoutTheRule_OrBelowItsLevel_StopsAsItAlwaysDid()
        {
            var none = Fighter(0);
            SimulationEngine.ApplyCombatDeath(ref none, 93, 5000);
            Assert.Equal(0, none.ActiveActivityId);
            Assert.Equal(Network.ActivityHaltReason.Died, none.ActivityHaltReason);

            var tooLow = Fighter(AutomationRules.Pack(new[] { StepDown, Empty, Empty }), level: 19);
            SimulationEngine.ApplyCombatDeath(ref tooLow, 93, 5000);
            Assert.Equal(0, tooLow.ActiveActivityId);
        }

        [Fact]
        public void TheRuleDoesNotStepOntoAMonsterAnotherCharacterIsFighting()
        {
            var payload = Fighter(AutomationRules.Pack(new[] { StepDown, Empty, Empty }));
            payload.Slot2Activity.ActiveActivityId = 92;
            SimulationEngine.ApplyCombatDeath(ref payload, 93, 5000);
            Assert.Equal(0, payload.ActiveActivityId);
            Assert.Equal(Network.ActivityHaltReason.Died, payload.ActivityHaltReason);
        }

        [Fact]
        public void ADryLarderWithTheRule_SendsTheLiveTickFishing()
        {
            var payload = Fighter(AutomationRules.Pack(new[] { Fish(3001), Empty, Empty }));
            payload.ActivityHaltReason = Network.ActivityHaltReason.OutOfFood;

            SimulationEngine.RunCombatTick(ref payload, 100, 100,
                new ConcurrentQueue<GuildWarPointEvent>(), new ConcurrentDictionary<long, LiveSessionContext>());

            Assert.Equal(3001, payload.ActiveActivityId);
            Assert.Equal(Network.ActivityHaltReason.AutomationFishing, payload.ActivityHaltReason);
            Assert.Equal(0, payload.CurrentMonsterId);
        }

        [Fact]
        public void TheFishingRuleWaitsForTheSpotToBeReached()
        {
            // 3002 is location 2; the character has only been to location 1.
            var payload = Fighter(AutomationRules.Pack(new[] { Fish(3002), Empty, Empty }));
            Assert.False(AutomationRules.TryGoFishing(ref payload));
            Assert.Equal(93, payload.ActiveActivityId);

            payload.HighestLocationReached = 2;
            Assert.True(AutomationRules.TryGoFishing(ref payload));
            Assert.Equal(3002, payload.ActiveActivityId);
        }

        [Fact]
        public void TheRuleNotesAreNotClearedAsIfTheyWereStops()
        {
            Assert.True(Network.ActivityHaltReason.IsStandingNote(Network.ActivityHaltReason.AutomationFishing));
            Assert.True(Network.ActivityHaltReason.IsStandingNote(Network.ActivityHaltReason.AutomationSteppedDown));
            Assert.True(Network.ActivityHaltReason.IsStandingNote(Network.ActivityHaltReason.OutOfFood));
            Assert.False(Network.ActivityHaltReason.IsStandingNote(Network.ActivityHaltReason.Died));
        }

        [Fact]
        public void TheRulesTravelWithTheCharacterThroughTheRegisterSwap()
        {
            var payload = Fighter(111L);
            payload.Slot2_CharacterId = Guid.NewGuid();
            payload.Slot2Activity.AutomationRules = 222L;

            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.Equal(222L, payload.AutomationRules);
            Assert.Equal(111L, payload.Slot2Activity.AutomationRules);

            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.Equal(111L, payload.AutomationRules);
        }

        [Fact]
        public void ARulesChangeReachesTheRegisterHoldingThatCharacter()
        {
            var payload = Fighter(0);
            payload.Slot1_CharacterId = Guid.NewGuid();
            payload.Slot3_CharacterId = Guid.NewGuid();

            AutomationRulesTickCoordinator.Apply(ref payload, new AutomationRulesNotification
            {
                PlayerId = payload.PlayerId, CharacterId = payload.Slot3_CharacterId, PackedRules = 77L,
            });
            Assert.Equal(77L, payload.Slot3Activity.AutomationRules);
            Assert.Equal(0L, payload.AutomationRules);

            AutomationRulesTickCoordinator.Apply(ref payload, new AutomationRulesNotification
            {
                PlayerId = payload.PlayerId, CharacterId = payload.Slot1_CharacterId, PackedRules = 88L,
            });
            Assert.Equal(88L, payload.AutomationRules);
        }

        [Fact]
        public void TheDropRequestCarriesTheFuseRule_LiveAndOfflineAlike()
        {
            // Build is the only builder, for the live kill and the offline window.
            var payload = Fighter(AutomationRules.Pack(new[] { Fuse(4), Empty, Empty }));
            var stats = SimulationEngine.LiveCombatStats(in payload);
            Assert.Equal(4, CombatLootDropRequest.Build(in payload, in stats, 93, 1, 0, false).AutoFuseToTier);
            Assert.Equal(4, CombatLootDropRequest.Build(in payload, in stats, 93, 50, 0, true, DropSource.Offline).AutoFuseToTier);

            payload.CurrentLevel = 5;
            Assert.Equal(0, CombatLootDropRequest.Build(in payload, in stats, 93, 1, 0, false).AutoFuseToTier);
        }
    }
}
