using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// 2026-10-08: one character per kind of work, and a health bar that belongs
    /// to the character it describes.
    ///
    /// The kind-of-work rule replaced "never the same activity id": two
    /// characters could fight two different monsters or mine two different
    /// veins. The health half was reported as "my max HP was 2000 and crept up":
    /// the maximum was one account-wide cache overwritten by whichever slot
    /// fought last, and a slot that had been working or offline walked into a
    /// fight on the hydration default of 100 HP.
    /// </summary>
    public class KindOfWorkAndRestedHealthTests
    {
        public KindOfWorkAndRestedHealthTests()
        {
            ContentRegistry.Initialize();
        }

        [Theory]
        [InlineData(91L, 92L, true)]       // two monsters: both fighting
        [InlineData(1001L, 1005L, true)]   // two trees
        [InlineData(2001L, 2002L, true)]   // two veins
        [InlineData(3001L, 3004L, true)]   // two fishing spots
        [InlineData(5000L, 5010L, true)]   // two recipes
        [InlineData(91L, 1001L, false)]    // fighting and chopping
        [InlineData(1001L, 2001L, false)]  // chopping and mining
        [InlineData(2001L, 3001L, false)]  // mining and fishing
        [InlineData(3001L, 5000L, false)]  // fishing and crafting
        [InlineData(0L, 0L, false)]        // idle never collides
        [InlineData(0L, 91L, false)]
        public void TheKindOfWorkIsWhatCollides(long left, long right, bool same)
        {
            Assert.Equal(same, CharacterSlotEngine.IsSameKindOfWork(left, right));
        }

        [Fact]
        public void ASecondFighterIsRefused_EvenAgainstADifferentMonster()
        {
            long[] slots = { 91L, 1001L, 0L };
            Assert.True(CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(slots, 2, 95L));
            Assert.True(CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(slots, 2, 1003L));
            Assert.False(CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(slots, 2, 2001L));
            // The fighter itself may change monster.
            Assert.False(CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(slots, 0, 95L));
            // Going idle is always allowed.
            Assert.False(CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(slots, 2, 0L));
        }

        [Fact]
        public void AnAccountWithTwoFightersKeepsTheFirst()
        {
            long[] slots = { 109L, 108L, 1004L };
            CharacterSlotEngine.ResolveKindOfWorkConflicts(slots);
            Assert.Equal(new[] { 109L, 0L, 1004L }, slots);

            long[] two = { 0L, 2001L, 2003L };
            CharacterSlotEngine.ResolveKindOfWorkConflicts(two);
            Assert.Equal(new[] { 0L, 2001L, 0L }, two);
        }

        [Fact]
        public void TheLiveCheckSeesTheParkedSlotsWhicheverIsActive()
        {
            var payload = new TickStatePayload
            {
                ActiveActivityId = 1001L,
                Slot2_CharacterId = Guid.NewGuid(),
                Slot3_CharacterId = Guid.NewGuid(),
            };
            payload.Slot2Activity.ActiveActivityId = 91L;

            Assert.True(CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in payload, 93L));
            Assert.False(CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in payload, 2001L));

            // Slot 2 in the register: slot 1's woodcutting is now the parked one.
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.True(CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in payload, 1003L));
            Assert.False(CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in payload, 93L));
        }

        [Fact]
        public void TheFishingRuleDoesNotSendASecondFisher()
        {
            var rule = new AutomationRules.Rule { Type = AutomationRules.FishWhenLarderDry, Param = 1 };
            var payload = new TickStatePayload
            {
                PlayerId = 985_100L,
                CurrentLevel = 60,
                ActiveActivityId = 91L,
                CurrentMonsterId = 91,
                AutomationRules = AutomationRules.Pack(new[] { rule, default, default }),
                HighestLocationReached = 5,
                Slot2_CharacterId = Guid.NewGuid(),
            };
            // Another character fishes a DIFFERENT spot.
            payload.Slot2Activity.ActiveActivityId = 3003L;

            Assert.False(AutomationRules.TryGoFishing(ref payload));
            Assert.Equal(91L, payload.ActiveActivityId);
        }

        [Fact]
        public void ARestedCharacterOpensTheFightAtFullHealth()
        {
            var payload = new TickStatePayload
            {
                PlayerId = 985_101L,
                CurrentLevel = 40,
                ActiveActivityId = 91L,
                PlayerHp = 100_000, // the hydration default
                RestedHpPending = true,
                HighestLocationReached = 1,
            };

            SimulationEngine.RunCombatTick(ref payload, 100, 100,
                new ConcurrentQueue<GuildWarPointEvent>(), new ConcurrentDictionary<long, LiveSessionContext>());

            Assert.False(payload.RestedHpPending);
            Assert.True(payload.CachedEffectiveMaxHp > 100_000L);
            // Full, give or take the first monster's swing in the same tick.
            Assert.True(payload.PlayerHp > 100_000);
        }

        [Fact]
        public void SwitchingMonstersIsNotAFreeHeal_ButComingFromWorkIs()
        {
            var fighting = new TickStatePayload { ActiveActivityId = 91L, PlayerHp = 5_000 };
            SimulationEngine.ApplyActivityChangeToPayload(ref fighting, 92L);
            Assert.False(fighting.RestedHpPending);

            var gathering = new TickStatePayload { ActiveActivityId = 1001L, PlayerHp = 5_000 };
            SimulationEngine.ApplyActivityChangeToPayload(ref gathering, 92L);
            Assert.True(gathering.RestedHpPending);
        }

        [Fact]
        public void EachCharacterKeepsItsOwnHealthMaximum()
        {
            var payload = new TickStatePayload
            {
                CachedEffectiveMaxHp = 20_000_000L,
                Slot2_CharacterId = Guid.NewGuid(),
            };
            payload.Slot2Activity.CachedEffectiveMaxHp = 2_000_000L;

            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.Equal(2_000_000L, payload.CachedEffectiveMaxHp);
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.Equal(20_000_000L, payload.CachedEffectiveMaxHp);
        }
    }
}
