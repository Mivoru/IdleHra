using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A slot fights with its own bloodline (2026-11-13): aptitudes, traits and
    /// the epic mark travel with the character when its slot takes the
    /// register, and come back out when it leaves.
    /// </summary>
    public class SlotOwnBloodTests
    {
        public SlotOwnBloodTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload TwoSlots()
        {
            var payload = new TickStatePayload
            {
                PlayerId = 990_201L,
                CurrentLevel = 60,
                STR = 80, DEX = 60, CON = 60, LCK = 30,
                Slot1_CharacterId = Guid.NewGuid(),
                Slot1_AgePhase = 1,
                Aptitude_Strength = 4, Aptitude_Skill = 4, Aptitude_Endurance = 4, Aptitude_Fortune = 4,
                TraitMask = 0L,
                Slot2_CharacterId = Guid.NewGuid(),
                Slot2_AgePhase = 1,
            };
            payload.Slot2Activity = StateCheckpointManager.WithBlood(payload.Slot2Activity, new Models.CharacterLineageRegistry
            {
                AptitudeStrength = 50, AptitudeSkill = 41, AptitudeEndurance = 30, AptitudeFortune = 22,
                TraitMask = 0b101L, IsEpicMutation = true,
            });
            return payload;
        }

        [Fact]
        public void SlotTwoTakesTheRegisterWithItsOwnAptitudes()
        {
            var payload = TwoSlots();
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);

            Assert.Equal(50, payload.Aptitude_Strength);
            Assert.Equal(41, payload.Aptitude_Skill);
            Assert.Equal(30, payload.Aptitude_Endurance);
            Assert.Equal(22, payload.Aptitude_Fortune);
            Assert.Equal(0b101L, payload.TraitMask);
            Assert.True(payload.IsEpicMutation);

            // And its milestones pay: Strength 50 is the top crit step.
            var stats = SimulationEngine.LiveCombatStats(in payload);
            var slotOne = TwoSlots();
            var slotOneStats = SimulationEngine.LiveCombatStats(in slotOne);
            Assert.True(SimulationEngine.LiveCritMultiplier(in payload, in stats) > SimulationEngine.LiveCritMultiplier(in slotOne, in slotOneStats));
        }

        [Fact]
        public void SwappingBackRestoresSlotOne()
        {
            var payload = TwoSlots();
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);

            Assert.Equal(4, payload.Aptitude_Strength);
            Assert.Equal(0L, payload.TraitMask);
            Assert.False(payload.IsEpicMutation);
            Assert.Equal(50, payload.Slot2Activity.AptitudeStrength);
        }

        [Fact]
        public void AnUnbredSlotReadsAsAFounder()
        {
            var state = StateCheckpointManager.WithBlood(default, null);
            Assert.Equal(BreedingAptitudes.StartingValue, state.AptitudeSkill);
            Assert.Equal(0L, state.TraitMask);
        }
    }
}
