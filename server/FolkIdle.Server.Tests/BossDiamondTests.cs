using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Owner decision 2026-09-28: regional bosses pay no diamonds. They paid a
    /// guaranteed 10 on every kill, which a strong character farmed at about
    /// 10,900 an hour off region 1's boss. Killed here through the live tick, so
    /// the rule is checked where it runs rather than where it is written.
    /// </summary>
    public class BossDiamondTests
    {
        public BossDiamondTests()
        {
            ContentRegistry.Initialize();
        }

        [Fact]
        public void KillingABossManyTimesPaysNoDiamonds()
        {
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(1);
            Assert.True(ContentRegistry.IsRegionalBoss(bossId));

            var payload = new TickStatePayload
            {
                PlayerId = 1,
                CurrentLevel = 97,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = bossId,
                CurrentMonsterId = bossId,
                // Already beaten once: normal stats, which is the farmable case.
                DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(0, bossId),
                CurrentMonsterHp = BossFirstClearRules.MaxHpFor(BossFirstClearRules.MarkDefeated(0, bossId), bossId) * 1000L,
                InventorySpaceRemaining = int.MaxValue,
                PlayerHp = 100_000_000,
                TownHallLevel = 1,
            };
            RaceAttributeGrowth.ApplyLevelUpGrowth(ref payload, activeRaceId: 1, levelsGained: 96);
            payload.CachedAffixTotals.FlatAttack = 50_000;
            payload.CachedAffixTotals.FlatDefense = 50_000;
            payload.SetGold(0);
            payload.SetPremiumCurrency(0);

            var queue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();

            int kills = 0;
            for (int tick = 0; tick < 20_000 && kills < 50; tick++)
            {
                SimulationEngine.ProcessSubTick(ref payload, 100, 100, queue, contexts);

                // Counted from the codex's kill events: a strong enough
                // character kills and respawns the boss inside one tick, so its
                // health never visibly drops between two samples.
                while (CodexEngine.KillEventQueue.TryDequeue(out var kill))
                {
                    if (kill.PlayerId == payload.PlayerId && kill.MonsterId == bossId) kills++;
                }
                while (queue.TryDequeue(out _)) { }
                while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            }

            Assert.True(kills >= 50, $"only {kills} boss kills - the fixture is not reaching the rule it tests (monster {payload.CurrentMonsterId}, hp {payload.CurrentMonsterHp}, activity {payload.ActiveActivityId}, halt {payload.ActivityHaltReason}, player hp {payload.PlayerHp})");
            Assert.Equal(0, payload.PremiumCurrency);

            // The boss challenge notes this path now also writes are drained so
            // they cannot reach another test's worker.
            while (CosmeticGrantEngine.Queue.TryDequeue(out _)) { }
        }
    }
}
