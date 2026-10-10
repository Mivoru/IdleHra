using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The monster pets (owner, 2026-10-10): ten of them, +8% to one stat each,
    /// a 1-in-100,000 chance on any kill, and a duplicate paid as 50 diamonds
    /// by mail. The three stats no pet had before - health, armour, gathering
    /// yield - are held to the function each stat already has.
    /// </summary>
    [Collection("Postgres collection")]
    public class MonsterPetTests
    {
        private readonly PostgresTestFixture _fixture;

        public MonsterPetTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void Ten_pets_each_with_one_eight_percent_bonus_on_a_different_stat()
        {
            var pool = PetRegistry.MonsterPool;
            Assert.Equal(10, pool.Count);
            Assert.All(pool, p => Assert.Equal(new[] { 8 }, p.Bonuses.Select(b => b.Pct)));
            Assert.Equal(10, pool.Select(p => p.Bonuses[0].Stat).Distinct().Count());
            Assert.Equal(pool.Count, pool.Select(p => p.Id).Distinct().Count());

            // Better than a shop pet, short of the Witch's two bonuses.
            Assert.True(PetRegistry.MonsterPetPct > PetRegistry.ShopPetPct);
            var witch = PetRegistry.RareDropOf(SeasonalEventRegistry.SamhainId)!;
            Assert.True(witch.Bonuses.Sum(b => b.Pct) > PetRegistry.MonsterPetPct);
        }

        [Fact]
        public void A_kill_window_finds_pets_at_one_in_a_hundred_thousand()
        {
            while (PetEngine.Drops.TryDequeue(out _)) { }
            int found = PetRegistry.RollMonsterPets(986_000_001L, 10_000_000L, new Random(7));
            Assert.Equal(100, found);
            int queued = 0;
            while (PetEngine.Drops.TryDequeue(out var note))
            {
                Assert.Equal(PetSource.Monster, PetRegistry.Find(note.PetId)!.Source);
                queued++;
            }
            Assert.Equal(100, queued);

            // Nobody to give it to, nothing rolled.
            Assert.Equal(0, PetRegistry.RollMonsterPets(0, 10_000_000L, new Random(7)));
        }

        [Fact]
        public void The_three_new_stats_reach_the_functions_the_tick_uses()
        {
            var bare = new TickStatePayload { CurrentLevel = 40, CON = 50, STR = 50, DEX = 50, LCK = 25 };
            var withPets = bare;
            PetRegistry.AddTo(PetRegistry.Find("pet_llamhigyn_y_dwr")!, ref withPets.CachedAffixTotals);
            PetRegistry.AddTo(PetRegistry.Find("pet_ogham_monolith")!, ref withPets.CachedAffixTotals);
            PetRegistry.AddTo(PetRegistry.Find("pet_dagdas_cauldron")!, ref withPets.CachedAffixTotals);

            var bareStats = SimulationEngine.LiveCombatStats(in bare);
            var petStats = SimulationEngine.LiveCombatStats(in withPets);

            long bareHp = SimulationEngine.EffectiveMaxMilliHpFor(in bare, in bareStats);
            long petHp = SimulationEngine.EffectiveMaxMilliHpFor(in withPets, in petStats);
            Assert.Equal(bareHp + bareHp * 80 / 1000, petHp);

            Assert.Equal((int)(bareStats.FlatPhysicalArmor * 1.08f), petStats.FlatPhysicalArmor);

            var node = ContentRegistry.GatheringNodes.ToArray().First(n => n.ProfessionType == 0);
            var bareYield = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var petYield = SimulationEngine.GatheringYieldFor(ref withPets, in node, 100);
            Assert.Equal(1.0f, bare.CachedCodexYieldMultiplier);
            Assert.Equal(bareYield.MultiplierPct + 8, petYield.MultiplierPct);
        }

        [Fact]
        public async Task A_new_pet_is_owned_and_a_duplicate_mails_fifty_diamonds()
        {
            const long playerId = 986_000_002L;
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                seed.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = $"mpet{playerId}",
                });
                await seed.SaveChangesAsync();
            }

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var note = new PetDropNote(playerId, "pet_hugin");

            Assert.Equal(PetEngine.DropOutcome.New, await PetEngine.SaveDropCoreAsync(db, note, DateTime.UtcNow));
            Assert.Equal(0, await db.MailboxInstances.CountAsync(m => m.PlayerId == playerId));

            Assert.Equal(PetEngine.DropOutcome.Duplicate, await PetEngine.SaveDropCoreAsync(db, note, DateTime.UtcNow));
            var mail = await db.MailboxInstances.SingleAsync(m => m.PlayerId == playerId);
            Assert.Equal(PetRegistry.DuplicateDiamonds, mail.DiamondAttachment);
            Assert.Equal(50, PetRegistry.DuplicateDiamonds);
            Assert.Equal(1, await db.PlayerPets.CountAsync(p => p.PlayerId == playerId));

            // An EVENT pet found twice still pays nothing - the rule is the
            // monster pets'.
            Assert.Equal(PetEngine.DropOutcome.New, await PetEngine.SaveDropCoreAsync(db, new PetDropNote(playerId, "pet_witch"), DateTime.UtcNow));
            Assert.Equal(PetEngine.DropOutcome.Nothing, await PetEngine.SaveDropCoreAsync(db, new PetDropNote(playerId, "pet_witch"), DateTime.UtcNow));
            Assert.Equal(1, await db.MailboxInstances.CountAsync(m => m.PlayerId == playerId));
        }
    }
}
