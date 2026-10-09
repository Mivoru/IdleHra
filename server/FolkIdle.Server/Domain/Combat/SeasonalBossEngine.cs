using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>A tier first-cleared on the tick, waiting to be saved and paid.</summary>
    public readonly record struct SeasonalBossClearNote(long PlayerId, int EventId, int Tier);

    public sealed record SeasonalBossTierView(
        int Tier, int Region, int BossMonsterId, string BossName,
        int Diamonds, long Gold, int Currency, string? Title, string? Pet,
        int HpPct, int AttackPct, bool Cleared);

    /// <summary>
    /// The off-tick half of the seasonal boss: saves a first clear and pays it.
    /// The tick has already credited the currency and set the cleared bit;
    /// this records the clear (the key makes it once) and mails the rest.
    ///
    /// Modul: THE MAIL IS WRITTEN IN THE SAME TRANSACTION AS THE CLEAR, so a
    /// saved clear always has its reward and a failed save pays nothing - the
    /// cleared bit on the payload is then only a cache, put right at the next
    /// login from the table.
    /// </summary>
    public static class SeasonalBossEngine
    {
        public const string SenderName = "The Cailleach";

        public static readonly ConcurrentQueue<SeasonalBossClearNote> Clears = new();

        public static async Task SaveClearAsync(IDbContextFactory<FolkIdleDbContext> factory, PlayerSessionRegistry registry, SeasonalBossClearNote note)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                bool paid = await SaveClearCoreAsync(db, note, DateTime.UtcNow);
                if (paid) registry.EnqueueCommandResult(note.PlayerId, (byte)CommandResultCode.SeasonalBossCleared);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Seasonal boss clear failed for player {note.PlayerId} tier {note.Tier}: {ex.Message}");
                registry.EnqueueCommandResult(note.PlayerId, (byte)CommandResultCode.CheckpointFailed);
            }
        }

        /// <summary>True when this call recorded the clear (and so paid it).</summary>
        internal static async Task<bool> SaveClearCoreAsync(FolkIdleDbContext db, SeasonalBossClearNote note, DateTime utcNow)
        {
            var tier = SeasonalBossRegistry.Find(note.Tier);
            if (tier == null) return false;

            await using var tx = await db.Database.BeginTransactionAsync();
            int inserted = await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO seasonal_boss_clears (\"PlayerId\", \"EventId\", \"Tier\", \"ClearedAtUtc\") " +
                "VALUES ({0}, {1}, {2}, {3}) ON CONFLICT DO NOTHING",
                note.PlayerId, note.EventId, note.Tier, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            if (inserted == 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            db.MailboxInstances.Add(new MailboxInstance
            {
                PlayerId = note.PlayerId,
                BaseItemId = string.Empty,
                Quantity = 0,
                GoldAttachment = tier.Gold,
                DiamondAttachment = tier.Diamonds,
                TitleAttachment = tier.TitleSlug,
                SenderName = SenderName,
                MessageText = MessageFor(tier.Tier),
                ReceivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();

            if (tier.PetId != null)
            {
                await PetEngine.GrantAsync(db, note.PlayerId, tier.PetId, utcNow);
            }

            await tx.CommitAsync();
            return true;
        }

        private static string MessageFor(int tier) => tier switch
        {
            6 => "You broke my winter. The little vampire is yours now - he never did like the cold. Until next Samhain.",
            3 => "Frost has touched you, and you did not break. Take this, and remember the cold.",
            _ => $"You stood against the {Ordinal(tier)} of my winters. It will not be the last.",
        };

        private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 4 => "fourth", 5 => "fifth", _ => n + "th" };

        /// <summary>The current event's first-cleared tiers, as the payload's mask.</summary>
        public static async Task<byte> LoadClearedMaskAsync(FolkIdleDbContext db, long playerId, int eventId)
        {
            var tiers = await db.SeasonalBossClears.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.EventId == eventId)
                .Select(c => c.Tier)
                .ToListAsync();
            byte mask = 0;
            foreach (int t in tiers) mask = SeasonalBossRegistry.WithCleared(mask, t);
            return mask;
        }

        public static async Task<List<SeasonalBossTierView>> ViewAsync(FolkIdleDbContext db, long playerId, int eventId)
        {
            byte mask = await LoadClearedMaskAsync(db, playerId, eventId);
            return SeasonalBossRegistry.Tiers.Select(t =>
            {
                int bossId = SeasonalBossRegistry.BossMonsterIdFor(t.Tier);
                string bossName = bossId > 0 ? ContentRegistry.GetMonsterName(bossId) : string.Empty;
                return new SeasonalBossTierView(
                    t.Tier, t.Region, bossId, bossName,
                    t.Diamonds, t.Gold, t.Currency,
                    TitleRegistry.DisplayNameFor(t.TitleSlug),
                    PetRegistry.Find(t.PetId)?.Name,
                    t.Modifiers.BossHpPct, t.Modifiers.AttackPct,
                    SeasonalBossRegistry.IsCleared(mask, t.Tier));
            }).ToList();
        }
    }
}
