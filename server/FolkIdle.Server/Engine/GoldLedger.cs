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

    /// <summary>Where a credit of gold came from (task 79, phase 2). Stored as a short - append only.</summary>
    public enum GoldIncomeSource : short
    {
        /// <summary>Kills on the live tick. Tallied on the payload.</summary>
        Combat = 1,
        /// <summary>Kills the offline catch-up simulated. Tallied on the payload.</summary>
        CombatAway = 2,
        /// <summary>The Town Hall's passive gold, live (tallied) and away (recorded at the grant).</summary>
        TownHall = 3,
        /// <summary>Auto-salvage proceeds. Tallied on the payload.</summary>
        AutoSalvage = 4,
        /// <summary>Selling from the Village Chest, by hand or by a sweep.</summary>
        ChestSale = 5,
        /// <summary>Market and cosmetic-market sales, net of fee and guild tax.</summary>
        Market = 6,
        LoginReward = 7,
        /// <summary>Gold attached to mail: the world boss, split-brain compensation, admin.</summary>
        Mail = 8,
        /// <summary>The weekly guild contribution payout.</summary>
        GuildPayout = 9,
        /// <summary>The Delve's consolation gold.</summary>
        Delve = 10,
        /// <summary>The 1,000 gold a new account starts with.</summary>
        Starter = 11,
        /// <summary>A delayed grant (PendingGrantOutbox) whose origin has no source of its own.</summary>
        Other = 12,
    }

    /// <summary>
    /// Task 79, phase 2: gold earned on the tick and not yet written to the
    /// income ledger. Lives on TickStatePayload.PendingGoldIncome.
    /// </summary>
    /// <remarks>
    /// Modul: EACH COIN IS COUNTED BY EXACTLY ONE CHECKPOINT. This rides the
    /// checkpoint exactly the way RedisPendingGoldDelta rides it (see
    /// StateCheckpointManager.RequestFlush), minus the Redis half: it is copied
    /// onto the job's snapshot and zeroed on the live payload at request time,
    /// FlushState writes it inside the checkpoint's transaction, and a failed
    /// flush's ack hands it back. A frame never touches it, so whether Redis or
    /// the checkpoint BANKS the gold does not change where it is COUNTED.
    ///
    /// Lost, never doubled: a logout whose flush fails every retry, or a crash,
    /// drops a tally, and the gold itself is unaffected.
    /// </remarks>
    public struct GoldIncomeTally
    {
        public long Combat;
        public long CombatAway;
        public long TownHall;
        public long AutoSalvage;

        public readonly bool IsEmpty => Combat == 0L && CombatAway == 0L && TownHall == 0L && AutoSalvage == 0L;

        public void Add(in GoldIncomeTally other)
        {
            Combat += other.Combat;
            CombatAway += other.CombatAway;
            TownHall += other.TownHall;
            AutoSalvage += other.AutoSalvage;
        }

        public void Subtract(in GoldIncomeTally other)
        {
            Combat -= other.Combat;
            CombatAway -= other.CombatAway;
            TownHall -= other.TownHall;
            AutoSalvage -= other.AutoSalvage;
        }
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

        /// <summary>
        /// Task 79, phase 2: records gold an engine has just credited to the
        /// player's gold row itself. Call it beside that credit, inside the same
        /// transaction, so a rollback takes both. Gold that rides
        /// RedisPendingGoldDelta instead goes through <see cref="TallyIncome"/>.
        /// </summary>
        public static async Task RecordIncomeAsync(FolkIdleDbContext db, long playerId, GoldIncomeSource source, long amount)
        {
            if (amount <= 0) return;

            var day = DateOnly.FromDateTime(DateTime.UtcNow);
            short src = (short)source;

            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO gold_income_daily (""PlayerId"", ""Day"", ""Source"", ""Amount"") VALUES ({playerId}, {day}, {src}, {amount})
ON CONFLICT (""PlayerId"", ""Day"", ""Source"")
DO UPDATE SET ""Amount"" = gold_income_daily.""Amount"" + EXCLUDED.""Amount""");
        }

        /// <summary>
        /// Task 79, phase 2: counts gold earned on the tick, beside the
        /// <c>RedisPendingGoldDelta +=</c> that owes it to the database. The
        /// checkpoint writes the tally (<see cref="RecordIncomeTallyAsync"/>).
        /// Tick thread only, like every other write to a payload.
        /// </summary>
        public static void TallyIncome(ref TickStatePayload payload, GoldIncomeSource source, long amount)
        {
            if (amount <= 0) return;
            switch (source)
            {
                case GoldIncomeSource.Combat: payload.PendingGoldIncome.Combat += amount; break;
                case GoldIncomeSource.CombatAway: payload.PendingGoldIncome.CombatAway += amount; break;
                case GoldIncomeSource.TownHall: payload.PendingGoldIncome.TownHall += amount; break;
                case GoldIncomeSource.AutoSalvage: payload.PendingGoldIncome.AutoSalvage += amount; break;
                default:
                    // A source whose gold an engine credits itself must be
                    // recorded at that credit; tallying it too would count it
                    // twice.
                    throw new ArgumentOutOfRangeException(nameof(source), source, "only payload-banked sources are tallied");
            }
        }

        /// <summary>Writes a checkpoint's tally. Called by FlushState and FlushBatch inside their transaction.</summary>
        internal static async Task RecordIncomeTallyAsync(FolkIdleDbContext db, long playerId, GoldIncomeTally tally)
        {
            if (tally.IsEmpty) return;
            await RecordIncomeAsync(db, playerId, GoldIncomeSource.Combat, tally.Combat);
            await RecordIncomeAsync(db, playerId, GoldIncomeSource.CombatAway, tally.CombatAway);
            await RecordIncomeAsync(db, playerId, GoldIncomeSource.TownHall, tally.TownHall);
            await RecordIncomeAsync(db, playerId, GoldIncomeSource.AutoSalvage, tally.AutoSalvage);
        }

        /// <summary>
        /// Where a delayed grant's gold came from. PendingGrantOutbox records
        /// it when the grant finally lands, because the original credit rolled
        /// back and was never counted.
        /// </summary>
        public static GoldIncomeSource SourceForPendingGrant(int pendingGrantSourceType) => pendingGrantSourceType switch
        {
            PendingGrantSourceType.CombatLoot => GoldIncomeSource.AutoSalvage,
            PendingGrantSourceType.OfflineVillageProduction => GoldIncomeSource.TownHall,
            _ => GoldIncomeSource.Other,
        };
    }
}
