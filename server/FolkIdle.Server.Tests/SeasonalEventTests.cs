using System;
using System.Linq;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The seasonal event currency: earned under one daily cap, stamped with
    /// its event so a new event starts from zero, and gone once the shop
    /// closes. Plan: docs/superpowers/plans/2026-10-09-samhain-event.md.
    /// </summary>
    public class SeasonalEventTests
    {
        private static readonly SeasonalEventDefinition Samhain = SeasonalEventRegistry.Find(SeasonalEventRegistry.SamhainId)!;
        private static long Live => Samhain.Start.AddDays(3).ToUnixTimeSeconds();
        private static long InGrace => Samhain.End.AddDays(1).ToUnixTimeSeconds();
        private static long AfterClose => Samhain.ShopClose.AddHours(1).ToUnixTimeSeconds();

        [Fact]
        public void The_calendar_has_a_live_phase_a_grace_and_an_end()
        {
            Assert.Equal(SeasonalEventPhase.Live, SeasonalEventRegistry.Current(Live).Phase);
            Assert.Equal(SeasonalEventPhase.Grace, SeasonalEventRegistry.Current(InGrace).Phase);
            Assert.Equal(SeasonalEventPhase.None, SeasonalEventRegistry.Current(AfterClose).Phase);
            Assert.Equal(3, Samhain.GraceDays);
        }

        [Fact]
        public void There_is_no_daily_cap_and_the_day_counter_resets_at_midnight()
        {
            // Owner, 2026-10-09: a player who plays more earns more.
            var payload = new TickStatePayload();
            var rng = new Random(7);
            int paid = SeasonalEventEarning.Roll(ref payload, SeasonalEventEarning.Source.Kill, 100_000, Live, rng);
            Assert.InRange(paid, (int)(100_000 * Samhain.KillChance * 0.9), (int)(100_000 * Samhain.KillChance * 1.1));
            Assert.Equal(paid, payload.EventCurrency);
            Assert.Equal(paid, payload.EventCurrencyEarnedToday);

            long tomorrow = Live + 86_400;
            int more = SeasonalEventEarning.Roll(ref payload, SeasonalEventEarning.Source.Kill, 10_000, tomorrow, rng);
            Assert.True(more > 0);
            Assert.Equal(paid + more, payload.EventCurrency);
            Assert.Equal(more, payload.EventCurrencyEarnedToday);
        }

        [Fact]
        public void Offline_pays_the_same_as_online_and_a_harvest_a_fifth_of_a_kill()
        {
            Assert.Equal(1.0, Samhain.OfflineFactor);
            Assert.Equal(Samhain.KillChance / 5, Samhain.GatherChance, 6);
        }

        [Fact]
        public void The_kill_rate_matches_the_chance()
        {
            var rng = new Random(11);
            int hits = 0;
            const int days = 4000; // ~4,000 hits at 0.1%: the 5% band is ~3 sigma
            for (int d = 0; d < days; d++)
            {
                var payload = new TickStatePayload();
                for (int k = 0; k < 1000; k++)
                {
                    hits += SeasonalEventEarning.Roll(ref payload, SeasonalEventEarning.Source.Kill, 1, Live, rng);
                }
            }
            double perKill = hits / (double)(days * 1000);
            Assert.InRange(perKill, Samhain.KillChance * 0.95, Samhain.KillChance * 1.05);
        }

        [Fact]
        public void A_large_offline_batch_keeps_its_mean()
        {
            var rng = new Random(3);
            long total = 0;
            const int runs = 2000;
            for (int i = 0; i < runs; i++)
            {
                total += SeasonalEventEarning.Draw(1000, 0.025, rng);
            }
            Assert.InRange(total / (double)runs, 1000 * 0.025 - 0.5, 1000 * 0.025 + 0.5);
        }

        [Fact]
        public void Grace_pays_nothing_but_keeps_the_balance_for_spending()
        {
            var payload = new TickStatePayload();
            SeasonalEventEarning.Grant(ref payload, 300, Live);
            Assert.Equal(300, payload.EventCurrency);

            Assert.Equal(0, SeasonalEventEarning.Roll(ref payload, SeasonalEventEarning.Source.Kill, 10_000, InGrace, new Random(1)));
            Assert.True(SeasonalEventEarning.TrySpend(ref payload, 250, InGrace));
            Assert.Equal(50, payload.EventCurrency);
            Assert.False(SeasonalEventEarning.TrySpend(ref payload, 100, InGrace));
            Assert.Equal(50, payload.EventCurrency);
        }

        [Fact]
        public void The_balance_is_gone_once_the_shop_closes_and_never_crosses_events()
        {
            var payload = new TickStatePayload();
            SeasonalEventEarning.Grant(ref payload, 200, Live);
            SeasonalEventEarning.Normalise(ref payload, AfterClose);
            Assert.Equal(0, payload.EventCurrency);

            // A balance stamped with another event is not this one's.
            payload.EventCurrency = 999;
            payload.EventCurrencyEventId = 42;
            SeasonalEventEarning.Normalise(ref payload, Live);
            Assert.Equal(0, payload.EventCurrency);
            Assert.Equal(Samhain.CurrencyStamp, payload.EventCurrencyEventId);

            // A raised currency generation is a reset: the old generation's
            // stamp is not this one's.
            payload.EventCurrency = 500;
            payload.EventCurrencyEventId = Samhain.Id * 100 + Samhain.CurrencyGeneration - 1;
            SeasonalEventEarning.Normalise(ref payload, Live);
            Assert.Equal(0, payload.EventCurrency);
        }

        [Fact]
        public void A_refund_for_another_event_is_dropped()
        {
            var payload = new TickStatePayload();
            SeasonalEventEarning.Grant(ref payload, 100, Live);
            SeasonalEventEarning.Refund(ref payload, currencyStamp: 42, amount: 50);
            Assert.Equal(100, payload.EventCurrency);
            SeasonalEventEarning.Refund(ref payload, Samhain.CurrencyStamp, 50);
            Assert.Equal(150, payload.EventCurrency);
        }

        [Fact]
        public void Every_shop_avatar_is_a_bound_cosmetic_with_art_on_disk()
        {
            string spriteRoot = System.IO.Path.Combine(RepoRoot(), "client", "Assets", "Images", "SpritesWeb");
            foreach (var item in SeasonalEventRegistry.All.SelectMany(e => e.Shop).Where(i => i.Kind == EventShopKind.Avatar))
            {
                var def = CosmeticRegistry.Find(item.Id);
                Assert.NotNull(def);
                Assert.Equal(CosmeticKind.Avatar, def!.Kind);
                Assert.True(def.Bound, $"{item.Id} must not be in a chest or on the market");
                Assert.DoesNotContain(def, CosmeticRegistry.ChestPool(def.Rarity));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(spriteRoot, item.Art)), $"no art at {item.Art}");
            }
        }

        [Fact]
        public void Shop_ids_are_unique()
        {
            var ids = SeasonalEventRegistry.All.SelectMany(e => e.Shop).Select(i => i.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "client_web"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }
}
