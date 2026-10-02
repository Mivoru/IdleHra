using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The player profile and the public guild view (2026-10-02). The profile
    /// used to answer with every character on the account, whole; it answers
    /// with the main character and the few others who wear something now, all
    /// eleven slots each, and the guild view shows a stranger's guild without
    /// its treasury.
    /// </summary>
    [Collection("Postgres collection")]
    public class PublicProfileTests
    {
        private readonly PostgresTestFixture _fixture;
        public PublicProfileTests(PostgresTestFixture fixture) => _fixture = fixture;

        private GuildManagementEngine Guilds() => new(_fixture.RetryingOptions, _fixture.PlayerRegistry);

        private async Task<Guid> SeedPlayerAsync(long id, string name, int level = 30)
        {
            var main = Guid.NewGuid();
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = id,
                PlayerGuid = main,
                AuthenticatorToken = Guid.NewGuid(),
                Username = name,
                CurrentLevel = level,
                TotalPlayTimeSeconds = 7200,
                RebirthCount = 2,
                SealsEarnedMask = 0b1011,
                BestHit = 1234,
            });
            db.CharacterRecords.Add(new CharacterRecord { Id = main, PlayerId = id, Name = "Main", SlotIndex = 0, AgePhase = 1, ActiveActivityId = 95 });
            await db.SaveChangesAsync();
            return main;
        }

        private async Task<long> WearAsync(long playerId, Guid characterId, string baseItemId, int slot, string affixes = "{}")
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var piece = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = 5, AffixPayload = affixes };
            db.EquipmentInstances.Add(piece);
            await db.SaveChangesAsync();
            var c = await db.CharacterRecords.SingleAsync(x => x.Id == characterId);
            switch (slot)
            {
                case EquipmentSlotEngine.SlotWeapon: c.EquippedWeaponId = piece.Id; break;
                case EquipmentSlotEngine.SlotRing: c.EquippedRingId = piece.Id; break;
                case EquipmentSlotEngine.SlotAxe: c.EquippedAxeId = piece.Id; break;
                case EquipmentSlotEngine.SlotRod: c.EquippedRodId = piece.Id; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
            await db.SaveChangesAsync();
            return piece.Id;
        }

        [Fact]
        public async Task TheProfileShowsTheMainCharacterAndOnlyTheDressedOthers()
        {
            const long id = 986100001L;
            var main = await SeedPlayerAsync(id, "profile_main");
            await WearAsync(id, main, "iron_sword", EquipmentSlotEngine.SlotWeapon, "{\"crit_chance@3\":25,\"is_affix_locked\":true}");
            await WearAsync(id, main, "copper_ring", EquipmentSlotEngine.SlotRing);
            await WearAsync(id, main, "birch_fishing_rod_1", EquipmentSlotEngine.SlotRod);

            // A household: one gatherer holding an axe, and many bare children.
            var gatherer = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.CharacterRecords.Add(new CharacterRecord { Id = gatherer, PlayerId = id, Name = "Woody", SlotIndex = 3, AgePhase = 1, ActiveActivityId = 1001 });
                for (int i = 0; i < 20; i++)
                {
                    db.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = id, Name = $"Child{i}", SlotIndex = 10 + i, AgePhase = 0 });
                }
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = id, MonsterId = 91, KillCount = 40 });
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = id, MonsterId = 95, KillCount = 3 });
                db.PlayerRegionCompletions.Add(new PlayerRegionCompletion { PlayerId = id, RegionId = 1, CompletedAtEpoch = 1 });
                await db.SaveChangesAsync();
            }
            await WearAsync(id, gatherer, "birch_axe_1", EquipmentSlotEngine.SlotAxe);

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await PublicProfiles.BuildProfileAsync(read, id, other => other == id);

            Assert.NotNull(view);
            Assert.Equal("profile_main", view!.Username);
            Assert.Equal(30, view.Level);
            Assert.True(view.IsOnline);
            Assert.Null(view.Guild);

            // The main character first, then the one other who wears something;
            // the twenty bare children are not on the profile at all.
            Assert.Equal(2, view.Characters.Count);
            Assert.True(view.Characters[0].IsMain);
            Assert.Equal("Fighting", view.Characters[0].Activity);
            Assert.Equal("Woody", view.Characters[1].Name);
            Assert.Equal("Woodcutting", view.Characters[1].Activity);
            Assert.Equal(0, view.MoreEquippedCharacters);

            // Combat slots AND tool slots, in slot order.
            Assert.Equal(new[] { EquipmentSlotEngine.SlotWeapon, EquipmentSlotEngine.SlotRing, EquipmentSlotEngine.SlotRod },
                view.Characters[0].Worn.Select(p => p.SlotIndex).ToArray());
            Assert.Equal(EquipmentSlotEngine.SlotAxe, view.Characters[1].Worn.Single().SlotIndex);

            // Affixes parse, and the lock flag is not mistaken for one.
            var sword = view.Characters[0].Worn[0];
            Assert.Equal(25, sword.Affixes["crit_chance@3"]);
            Assert.False(sword.Affixes.ContainsKey("is_affix_locked"));

            Assert.Equal(43, view.Stats.TotalKills);
            Assert.Equal(3, view.Stats.BossesSlain);
            Assert.Equal(1, view.Stats.RegionsCompleted);
            Assert.Equal(7200, view.Stats.TotalPlayTimeSeconds);
            Assert.Equal(2, view.Stats.RebirthCount);
            Assert.Equal(3, view.Stats.SealsEarned);
            Assert.Equal(1234, view.Stats.BestHit);
        }

        [Fact]
        public async Task ExtraDressedCharactersAreCappedAndCounted()
        {
            const long id = 986100002L;
            var main = await SeedPlayerAsync(id, "profile_many");
            for (int i = 0; i < PublicProfiles.MaxExtraCharacters + 3; i++)
            {
                var c = Guid.NewGuid();
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    db.CharacterRecords.Add(new CharacterRecord { Id = c, PlayerId = id, Name = $"G{i}", SlotIndex = 1 + i, AgePhase = 1 });
                    await db.SaveChangesAsync();
                }
                await WearAsync(id, c, "birch_axe_1", EquipmentSlotEngine.SlotAxe);
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await PublicProfiles.BuildProfileAsync(read, id, _ => false);

            // The main character is shown even when it wears nothing.
            Assert.True(view!.Characters[0].IsMain);
            Assert.Empty(view.Characters[0].Worn);
            Assert.Equal(1 + PublicProfiles.MaxExtraCharacters, view.Characters.Count);
            Assert.Equal(3, view.MoreEquippedCharacters);
        }

        [Fact]
        public async Task AnUnknownPlayerIsNull()
        {
            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null(await PublicProfiles.BuildProfileAsync(read, 986199999L, _ => false));
            Assert.Null(await PublicProfiles.BuildGuildAsync(read, 986199999L, 1, _ => false));
        }

        [Fact]
        public async Task TheProfileNamesTheGuildAndTheGuildViewListsItsMembers()
        {
            const long leader = 986100011L, member = 986100012L, outsider = 986100013L;
            await SeedPlayerAsync(leader, "pg_leader", 40);
            await SeedPlayerAsync(member, "pg_member", 25);
            await SeedPlayerAsync(outsider, "pg_outsider", 30);

            long guildId = (await Guilds().CreateGuildAsync(leader, "ProfileViewGuild")).GuildId;
            Assert.True(guildId > 0);
            Assert.True(await Guilds().JoinGuildAsync(member, guildId));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var g = await db.GuildRecords.SingleAsync(x => x.Id == guildId);
                g.GuildTreasuryGold = 987654;
                var row = await db.GuildMembers.SingleAsync(m => m.PlayerId == member);
                row.WeeklyContributionPoints = 70;
                db.GuildActiveBuffs.Add(new GuildActiveBuff { GuildId = guildId, BuffType = "Exp", Tier = 2, ExpiresAt = DateTime.UtcNow.AddHours(3) });
                db.GuildActiveBuffs.Add(new GuildActiveBuff { GuildId = guildId, BuffType = "Gold", Tier = 1, ExpiresAt = DateTime.UtcNow.AddHours(-1) });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var profile = await PublicProfiles.BuildProfileAsync(read, member, _ => false);
            Assert.NotNull(profile!.Guild);
            Assert.Equal(guildId, profile.Guild!.GuildId);
            Assert.Equal("ProfileViewGuild", profile.Guild.Name);
            Assert.Equal(0, profile.Guild.Role);

            // An outsider sees it - the view is public - and is told so.
            var view = await PublicProfiles.BuildGuildAsync(read, guildId, outsider, id => id == leader);
            Assert.NotNull(view);
            Assert.False(view!.ViewerIsMember);
            Assert.Equal(new[] { leader, member }, view.Members.Select(m => m.PlayerId).ToArray());
            Assert.Equal(2, view.Members[0].Role);
            Assert.True(view.Members[0].IsOnline);
            Assert.Equal("pg_member", view.Members[1].Username);
            Assert.Equal(25, view.Members[1].Level);
            Assert.Equal(70, view.WeeklyPoints);
            Assert.Equal("Exp", Assert.Single(view.ActiveBuffs).BuffType);
            Assert.True(view.Rank >= 1);

            Assert.True((await PublicProfiles.BuildGuildAsync(read, guildId, member, _ => false))!.ViewerIsMember);

            // Modul: THE TREASURY IS NOT ON THE PUBLIC VIEW. Pinned by the type,
            // so adding a gold field is a decision this test makes visible.
            var props = typeof(GuildPublicView).GetProperties().Select(p => p.Name).ToArray();
            Assert.DoesNotContain(props, p => p.Contains("Gold", StringComparison.OrdinalIgnoreCase) || p.Contains("Depot", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(typeof(GuildViewMember).GetProperties(), p => p.Name.Contains("Contribution", StringComparison.Ordinal));
            Assert.DoesNotContain(typeof(PlayerProfileView).GetProperties(), p => p.Name is "Email" or "Gold" or "PremiumDiamonds");
        }
    }
}
