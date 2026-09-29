using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>What a debit of gold paid for. Stored as a short - append only.</summary>
    public enum GoldSpendCategory : short
    {
        Reroll = 1,
        Fusion = 2,
        Village = 3,
        Recruit = 4,
        Breeding = 5,
        Delve = 6,
        Deep = 7,
        Market = 8,
        Cosmetics = 9,
        Guild = 10,
        GuildRaid = 11,
    }

    /// <summary>
    /// Task 79: the one way to record that gold was SPENT. Call it beside the
    /// <c>Quantity -= cost</c> on the player's gold row, inside the same
    /// transaction.
    /// </summary>
    /// <remarks>
    /// Modul: TWO WRITES, ONE TRUTH EACH. The daily row (gold_spend_daily)
    /// answers "on what", and PlayerRecord.LifetimeGoldSpent answers "how
    /// much, ever", which the Treasury deed pays on. Both are raw SQL
    /// increments rather than tracked-entity edits for three reasons: two
    /// concurrent spends cannot lose one another; the checkpoint's own save of
    /// PlayerRecord cannot overwrite a column it never modified; and a
    /// rollback undoes both.
    ///
    /// GoldLedgerTests fails when a file debits a gold row without calling
    /// this. That is the mechanical guard - a sink added later without a
    /// category would otherwise vanish from the ledger in silence.
    /// </remarks>
    public static class GoldLedger
    {
        // Modul: THE LIVE COPY for the wire's achievement signal
        // (StateUpdatePacket.AchievementTierTotal), which is built on the tick
        // and cannot read the database. Seeded at login and refreshed by every
        // checkpoint, which reads the column anyway to pay the Treasury deed. A
        // spend therefore shows up within one checkpoint, the same moment the
        // tier is paid, and the toast and the payment land together. It is NOT
        // bumped by RecordSpendAsync itself, because that runs before its
        // transaction commits and a rollback would leave the copy ahead of the
        // truth.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _knownLifetimeSpent = new();

        internal static void NoteLifetimeSpent(long playerId, long lifetimeSpent)
            => _knownLifetimeSpent.AddOrUpdate(playerId, lifetimeSpent, (_, old) => Math.Max(old, lifetimeSpent));

        public static long KnownLifetimeSpent(long playerId)
            => _knownLifetimeSpent.TryGetValue(playerId, out long v) ? v : 0L;

        public static async Task RecordSpendAsync(FolkIdleDbContext db, long playerId, GoldSpendCategory category, long amount)
        {
            if (amount <= 0) return;

            var day = DateOnly.FromDateTime(DateTime.UtcNow);
            short cat = (short)category;

            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO gold_spend_daily (""PlayerId"", ""Day"", ""Category"", ""Amount"") VALUES ({playerId}, {day}, {cat}, {amount})
ON CONFLICT (""PlayerId"", ""Day"", ""Category"")
DO UPDATE SET ""Amount"" = gold_spend_daily.""Amount"" + EXCLUDED.""Amount""");

            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""PlayerRecords"" SET ""LifetimeGoldSpent"" = ""LifetimeGoldSpent"" + {amount} WHERE ""Id"" = {playerId}");
        }
    }
}
