using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// ProcessSubTick dispatches on ActiveActivityId to exactly one of three
    /// branches - crafting, gathering, or combat - and they must be mutually
    /// exclusive. The gathering branch enforces this with its own `return`;
    /// the crafting branch did not, so a crafting character fell through into
    /// full combat resolution against ActivityIdBands.CombatFirst-adjacent
    /// fallbackId 1 every tick, silently, in addition to crafting correctly.
    ///
    /// Deliberately fixture-free: the tick is a static method over a struct,
    /// so this needs no Postgres and no Redis - same shape as
    /// CombatEventFeedTests, which documents the same technique.
    /// </summary>
    public class ProcessSubTickDispatchTests
    {
        private const long TestPlayerId = 987_655;

        public ProcessSubTickDispatchTests()
        {
            ContentRegistry.Initialize();
            ActiveSkillEngine.Initialize();
        }

        [Fact]
        public void ACraftingCharacterNeverSpawnsAMonster()
        {
            // ActivityIdBands.CraftingBand + 0 is recipe index 0 - always
            // valid as long as the catalogue has at least one recipe, which
            // TryGetRecipeByActivityId's own index bound already guarantees
            // for any populated content set.
            var payload = new TickStatePayload
            {
                PlayerId = TestPlayerId,
                CurrentLevel = 1,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = ActivityIdBands.CraftingBand,
                CurrentMonsterId = 0,
                PlayerHp = 100_000,
                InventorySpaceRemaining = int.MaxValue,
            };
            payload.SetGold(0);

            var queue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();

            SimulationEngine.ProcessSubTick(ref payload, 100, 100, queue, contexts);

            Assert.Equal(0, payload.CurrentMonsterId);
        }

        [Fact]
        public void ACraftingCharacterStillMakesCraftingProgress()
        {
            // The fix must not touch the crafting branch's own logic - only
            // add the missing exclusivity return. This pins that the craft
            // itself still progresses exactly as before.
            var payload = new TickStatePayload
            {
                PlayerId = TestPlayerId,
                CurrentLevel = 1,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = ActivityIdBands.CraftingBand,
                CurrentMonsterId = 0,
                PlayerHp = 100_000,
                InventorySpaceRemaining = int.MaxValue,
            };
            payload.SetGold(0);

            var queue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();

            SimulationEngine.ProcessSubTick(ref payload, 100, 100, queue, contexts);

            Assert.Equal(1, payload.GatheringProgressTicks);
            Assert.True(payload.RequiredProgressTicks > 0);
        }
    }
}
