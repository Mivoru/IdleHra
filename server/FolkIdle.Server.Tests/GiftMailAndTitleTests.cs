using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The 2026-10-09 betatester thank-you: titles carry a colour the client
    /// draws as a chip, the worn title rides the batched worn-cosmetics lookup
    /// (chat, boards, rosters), and an admin mail can carry diamonds and a
    /// title that are credited - and worn - on claim.
    /// </summary>
    [Collection("Postgres collection")]
    public class GiftMailAndTitleTests
    {
        private readonly PostgresTestFixture _fixture;

        public GiftMailAndTitleTests(PostgresTestFixture fixture) => _fixture = fixture;

        [Fact]
        public void EveryTitleHasAColour_AndTheHandGrantedOnesAreNotDeepTitles()
        {
            var hex = new Regex("^#[0-9a-f]{6}$");
            Assert.All(TitleRegistry.All, t => Assert.Matches(hex, t.Color));
            Assert.All(TitleRegistry.All, t => Assert.True(t.Slug.Length <= 32, t.Slug));

            Assert.Equal("Betatester", TitleRegistry.DisplayNameFor(TitleRegistry.BetatesterSlug));
            Assert.Equal("DEV", TitleRegistry.DisplayNameFor(TitleRegistry.DevSlug));

            // Floor 0: no depth in the Delve ever grants or advertises them.
            Assert.DoesNotContain(TitleRegistry.ForDeepFloor(int.MaxValue), t => t.Slug is TitleRegistry.BetatesterSlug or TitleRegistry.DevSlug);
            Assert.NotEqual(TitleRegistry.BetatesterSlug, TitleRegistry.NextDeepTitle(0)?.Slug);
        }

        private async Task<PlayerRecord> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"gift_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 1,
                PremiumDiamonds = 5,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1_000 });
            await db.SaveChangesAsync();
            return player;
        }

        [Fact]
        public async Task AGiftMail_CreditsDiamondsAndGold_AndWearsItsTitle()
        {
            var player = await CreatePlayerAsync();
            long mailId;
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var mail = new MailboxInstance
                {
                    PlayerId = player.Id, BaseItemId = string.Empty, SenderName = "Dev", MessageText = "Thank you",
                    GoldAttachment = 100_000_000, DiamondAttachment = 100, TitleAttachment = TitleRegistry.BetatesterSlug,
                };
                seed.MailboxInstances.Add(mail);
                await seed.SaveChangesAsync();
                mailId = mail.Id;
            }

            var registry = new PlayerSessionRegistry();
            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, registry);
            await engine.CommitMailClaimAsync(player.Id, mailId, true);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var after = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player.Id);
            Assert.Equal(105, after.PremiumDiamonds);
            Assert.Equal(TitleRegistry.BetatesterSlug, after.ActiveTitleSlug);
            Assert.True(await db.PlayerTitles.AnyAsync(t => t.PlayerId == player.Id && t.TitleSlug == TitleRegistry.BetatesterSlug));
            Assert.Equal(100_001_000, (await db.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == player.Id && c.ItemId == "gold")).Quantity);
            Assert.True((await db.MailboxInstances.AsNoTracking().SingleAsync(m => m.Id == mailId)).IsClaimed);

            // An online session's payload owns the diamond balance and the
            // checkpoint writes it back by assignment - so the committed balance
            // must reach the tick thread, or the next flush erases the gift.
            Assert.True(registry.BillingSyncQueue.TryDequeue(out var sync));
            Assert.Equal(player.Id, sync.PlayerId);
            Assert.Equal(105, sync.PremiumDiamondsBalance);

            // The worn lookup every name row uses now carries the chip.
            var worn = Assert.Single(await CosmeticEngine.WornAsync(db, new[] { player.Id }));
            Assert.Equal("Betatester", worn.Title);
            Assert.Equal(TitleRegistry.Find(TitleRegistry.BetatesterSlug)!.Color, worn.TitleColor);
        }

        [Fact]
        public async Task APlainGoldMail_TouchesNeitherDiamondsNorTitle()
        {
            var player = await CreatePlayerAsync();
            long mailId;
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var mail = new MailboxInstance { PlayerId = player.Id, BaseItemId = string.Empty, GoldAttachment = 10 };
                seed.MailboxInstances.Add(mail);
                await seed.SaveChangesAsync();
                mailId = mail.Id;
            }

            var registry = new PlayerSessionRegistry();
            await new MailboxAndBankEngine(_fixture.ServiceProvider, registry).CommitMailClaimAsync(player.Id, mailId, true);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var after = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player.Id);
            Assert.Equal(5, after.PremiumDiamonds);
            Assert.Null(after.ActiveTitleSlug);
            Assert.False(registry.BillingSyncQueue.TryDequeue(out _));
            Assert.Null(Assert.Single(await CosmeticEngine.WornAsync(db, new[] { player.Id })).Title);
        }
    }
}
