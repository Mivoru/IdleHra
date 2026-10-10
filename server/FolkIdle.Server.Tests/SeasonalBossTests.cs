using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The seasonal boss (The Cailleach): six tiers graded like the five region
    /// bosses plus one region step, first clears paid once, and the ladder
    /// unlocking tier by tier. The fight is the Ascension machinery's, so these
    /// pin the tier table and its rewards rather than re-test the combat.
    /// </summary>
    [Collection("Postgres collection")]
    public class SeasonalBossTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _o;

        public SeasonalBossTests(PostgresTestFixture fixture, ITestOutputHelper o)
        {
            _fixture = fixture;
            _o = o;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void Six_tiers_on_the_region_ladder_paying_the_owners_diamonds()
        {
            Assert.Equal(6, SeasonalBossRegistry.Tiers.Count);
            for (int t = 1; t <= 5; t++)
            {
                Assert.Equal(t, SeasonalBossRegistry.Find(t)!.Region);
                Assert.True(SeasonalBossRegistry.Find(t)!.Modifiers.IsNone, $"tier {t} is region {t}'s boss at its wall, nothing added");
            }
            var six = SeasonalBossRegistry.Find(6)!;
            Assert.Equal(5, six.Region);
            Assert.False(six.Modifiers.IsNone, "tier 6 is more than region 5's wall");

            Assert.True(SeasonalBossRegistry.Tiers.Sum(t => t.Diamonds) >= 120, "owner: at least 120 diamonds in all");
            Assert.All(SeasonalBossRegistry.Tiers.Where(t => t.TitleSlug != null), t => Assert.NotNull(TitleRegistry.Find(t.TitleSlug)));
            Assert.Equal(PetRegistry.BossPetOf(SeasonalEventRegistry.SamhainId)!.Id, six.PetId);
        }

        [Fact]
        public void A_tier_opens_after_its_region_boss_and_the_tier_below()
        {
            var samhain = SeasonalEventRegistry.Find(SeasonalEventRegistry.SamhainId)!;
            long live = samhain.Start.AddDays(2).ToUnixTimeSeconds();
            var payload = new TickStatePayload();

            // Region 1's boss never beaten: tier 1 waits for it.
            Assert.Equal(CommandResultCode.AscensionBossNotDefeated, SeasonalBossRegistry.Validate(in payload, 1, samhain.Id, live));

            payload.DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(0, RaceUnlockRegistry.GetRegionBossMonsterId(1));
            payload.DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(payload.DefeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(2));
            Assert.Equal(CommandResultCode.Success, SeasonalBossRegistry.Validate(in payload, 1, samhain.Id, live));
            Assert.Equal(CommandResultCode.AscensionStepLocked, SeasonalBossRegistry.Validate(in payload, 2, samhain.Id, live));

            payload.SeasonalBossClearedMask = SeasonalBossRegistry.WithCleared(0, 1);
            Assert.Equal(CommandResultCode.Success, SeasonalBossRegistry.Validate(in payload, 2, samhain.Id, live));

            // Out of season, or a stale event id: closed.
            Assert.Equal(CommandResultCode.EventShopClosed, SeasonalBossRegistry.Validate(in payload, 1, samhain.Id, samhain.End.AddDays(1).ToUnixTimeSeconds()));
            Assert.Equal(CommandResultCode.EventShopClosed, SeasonalBossRegistry.Validate(in payload, 1, 99, live));
        }

        [Fact]
        public void The_fight_reads_the_boss_at_its_wall_even_when_beaten()
        {
            int boss3 = RaceUnlockRegistry.GetRegionBossMonsterId(3);
            byte allBeaten = 0;
            for (int r = 1; r <= 5; r++) allBeaten = BossFirstClearRules.MarkDefeated(allBeaten, RaceUnlockRegistry.GetRegionBossMonsterId(r));
            byte wall = SeasonalBossRegistry.WallMaskFor(allBeaten, 3);
            Assert.False(BossFirstClearRules.IsDefeated(wall, boss3));
            Assert.True(BossFirstClearRules.IsDefeated(wall, RaceUnlockRegistry.GetRegionBossMonsterId(2)));
            Assert.True(BossFirstClearRules.MaxHpFor(wall, boss3, 0) > BossFirstClearRules.MaxHpFor(allBeaten, boss3, 0));
        }

        /// <summary>
        /// MEASURED, not asserted against a wish: who beats each tier. A tier is
        /// the region's wall, so the gear that clears that wall clears the tier;
        /// tier 6 should need more than region 5's wall gear - it is a sixth
        /// region nobody has the gear for yet - and the best gear in the game
        /// should still have a chance at it.
        /// </summary>
        [Fact]
        public void Tier_six_is_beyond_the_region_five_wall_and_within_reach_of_the_best_gear()
        {
            int boss5 = RaceUnlockRegistry.GetRegionBossMonsterId(5);
            var wallGear = new ReferenceLoadout(
                BossGearBenchmark.ReferenceLevelForRegion(5), 5,
                BossFirstClearRules.RequiredQualityTierFor(5), BossFirstClearRules.RequiredAffixRarityFor(5));
            var bestGear = new ReferenceLoadout(100, 5, RarityTier.Transcendent, AffixRarity.Legendary);
            var six = SeasonalBossRegistry.Find(6)!.Modifiers;
            var none = default(AscensionModifiers);

            var wall = BossGearBenchmark.ProjectFirstClearWithModifiers(boss5, in wallGear, in none);
            var sixWall = BossGearBenchmark.ProjectFirstClearWithModifiers(boss5, in wallGear, in six);
            var sixBest = BossGearBenchmark.ProjectFirstClearWithModifiers(boss5, in bestGear, in six);

            _o.WriteLine($"region 5 wall, wall gear:  kill {wall.SecondsToKillBoss:F0}s vs death {wall.SecondsToPlayerDeath:F0}s");
            _o.WriteLine($"tier 6, wall gear:         kill {sixWall.SecondsToKillBoss:F0}s vs death {sixWall.SecondsToPlayerDeath:F0}s");
            _o.WriteLine($"tier 6, best gear (L100):  kill {sixBest.SecondsToKillBoss:F0}s vs death {sixBest.SecondsToPlayerDeath:F0}s");

            Assert.True(wall.PlayerWins, "region 5's wall gear clears region 5's wall (tier 5)");
            Assert.False(sixWall.PlayerWins, "tier 6 asks for more than region 5's wall gear");
            Assert.True(sixBest.PlayerWins, "the best gear in the game can break the long winter");
            Assert.True(sixBest.SecondsToKillBoss < 60 * 60, "and within an hour");
        }

        private async Task<long> SeedAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = $"sb{playerId}",
            });
            await db.SaveChangesAsync();
            return playerId;
        }

        [Fact]
        public async Task A_first_clear_is_paid_once_by_mail_and_tier_six_brings_the_wolf()
        {
            long playerId = await SeedAsync(986_000_001L);
            int eventId = SeasonalEventRegistry.SamhainId;
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            Assert.True(await SeasonalBossEngine.SaveClearCoreAsync(db, new SeasonalBossClearNote(playerId, eventId, 6), DateTime.UtcNow));
            Assert.False(await SeasonalBossEngine.SaveClearCoreAsync(db, new SeasonalBossClearNote(playerId, eventId, 6), DateTime.UtcNow));

            var mail = await db.MailboxInstances.AsNoTracking().Where(m => m.PlayerId == playerId).ToListAsync();
            var six = SeasonalBossRegistry.Find(6)!;
            Assert.Single(mail);
            Assert.Equal(six.Diamonds, mail[0].DiamondAttachment);
            Assert.Equal(six.Gold, mail[0].GoldAttachment);
            Assert.Equal(six.TitleSlug, mail[0].TitleAttachment);
            Assert.True(await PetEngine.OwnsAsync(db, playerId, six.PetId!));

            Assert.Equal(SeasonalBossRegistry.WithCleared(0, 6), await SeasonalBossEngine.LoadClearedMaskAsync(db, playerId, eventId));
        }

        // ---- one attempt, then back to work (2026-10-10) --------------------

        /// <summary>Slot 1, strong, armed on tier 1 with the Cailleach one hit from death, having left <paramref name="returnTo"/>.</summary>
        private static TickStatePayload ArmedOnTierOne(long returnTo)
        {
            int boss1 = RaceUnlockRegistry.GetRegionBossMonsterId(1);
            var p = new TickStatePayload
            {
                PlayerId = 1,
                CurrentLevel = 97,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = boss1,
                CurrentMonsterId = boss1,
                CurrentMonsterHp = 1,
                DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(0, boss1),
                // Already broken once, so the win is a repeat: no event clock in the test.
                SeasonalBossClearedMask = SeasonalBossRegistry.WithCleared(0, 1),
                InventorySpaceRemaining = int.MaxValue,
                PlayerHp = 100_000_000,
                TownHallLevel = 1,
            };
            RaceAttributeGrowth.ApplyLevelUpGrowth(ref p, activeRaceId: 1, levelsGained: 96);
            p.CachedAffixTotals.FlatAttack = 50_000;
            p.CachedAffixTotals.FlatDefense = 50_000;
            p.SeasonalBossTier = 1;
            p.SeasonalBossCharacterId = p.Slot1_CharacterId;
            p.SeasonalBossReturnActivityId = returnTo;
            return p;
        }

        private static void Tick(ref TickStatePayload p)
        {
            var queue = new System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext>();
            SimulationEngine.ProcessSubTick(ref p, 100, 100, queue, contexts);
            while (queue.TryDequeue(out _)) { }
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            while (CosmeticGrantEngine.Queue.TryDequeue(out _)) { }
            while (CosmeticGrantEngine.BossKills.TryDequeue(out _)) { }
            while (SeasonalBossEngine.Clears.TryDequeue(out _)) { }
        }

        [Fact]
        public void A_win_ends_the_fight_and_sends_the_character_back_to_its_old_work()
        {
            const long oldWork = 92; // region 1's second monster
            var p = ArmedOnTierOne(oldWork);
            for (int i = 0; i < 200 && p.SeasonalBossTier != 0; i++) Tick(ref p);

            Assert.Equal(0, p.SeasonalBossTier);
            Assert.Equal(0L, p.SeasonalBossReturnActivityId);
            Assert.Equal(oldWork, p.ActiveActivityId);
            Assert.NotEqual(RaceUnlockRegistry.GetRegionBossMonsterId(1), p.CurrentMonsterId);
        }

        [Fact]
        public void A_win_from_idle_leaves_the_character_idle_not_farming_the_yardstick_boss()
        {
            var p = ArmedOnTierOne(0);
            for (int i = 0; i < 200 && p.SeasonalBossTier != 0; i++) Tick(ref p);

            Assert.Equal(0, p.SeasonalBossTier);
            Assert.Equal(0L, p.ActiveActivityId);
            Assert.Equal(0, p.CurrentMonsterId);
        }

        [Fact]
        public void A_death_ends_the_fight_and_sends_the_character_back_too()
        {
            const long oldWork = 92;
            var p = ArmedOnTierOne(oldWork);
            int boss1 = RaceUnlockRegistry.GetRegionBossMonsterId(1);
            byte deathsBefore = p.LastDeathTick;

            SimulationEngine.ApplyCombatDeath(ref p, boss1, 1000);

            Assert.Equal((byte)(deathsBefore + 1), p.LastDeathTick);
            Assert.Equal(boss1, p.LastDeathMonsterId);
            Assert.Equal(0, p.SeasonalBossTier);
            Assert.Equal(oldWork, p.ActiveActivityId);
        }
    }
}
