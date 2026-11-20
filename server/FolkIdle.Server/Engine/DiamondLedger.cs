using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Labels a diamond change for the diamond_daily ledger. The ledger itself
    /// is written by a trigger on PlayerRecords (see Models.DiamondDaily), so
    /// a writer only says WHY.
    /// </summary>
    /// <remarks>
    /// Modul: THE LABEL IS TRANSACTION-LOCAL. LabelAsync runs
    /// set_config(..., is_local => true), which lasts until the current
    /// transaction ends - so it must be called INSIDE the transaction that
    /// changes the balance, before the save. Outside a transaction it would
    /// expire with its own statement, so it refuses to run there rather than
    /// pretend; a writer with no transaction uses <see cref="LabelSql"/> in the
    /// same command as its UPDATE (Npgsql runs one command's statements in one
    /// implicit transaction).
    ///
    /// One label per transaction: the last one set wins for every change after
    /// it. A transaction that changes diamonds for two reasons labels between
    /// the two saves.
    /// </remarks>
    public static class DiamondLedger
    {
        public const string Checkpoint = "checkpoint";       // kill drops (live + away) and lifetime deed tiers paid by the checkpoint
        public const string Achievement = "achievement";
        public const string Lifetime = "lifetime_deed";
        public const string LoginReward = "login";
        public const string Chronicle = "chronicle";
        public const string ChroniclePass = "chronicle_pass";
        public const string QuestReward = "quest";
        public const string Mail = "mail";
        public const string Leaderboard = "leaderboard";
        public const string GuildWar = "guild_war";
        public const string Delve = "delve";
        public const string Deep = "deep";
        public const string Dungeon = "dungeon";
        public const string Nav = "nav";
        public const string LootChest = "loot_chest";
        public const string Purchase = "purchase";
        public const string Refund = "refund";
        public const string HallOfAncestors = "hall_of_ancestors";
        public const string Inheritance = "inheritance";
        public const string Admin = "admin";

        /// <summary>SQL fragment that sets the label for the rest of its command's implicit transaction.</summary>
        public static string LabelSql(string reason) => $"SELECT set_config('folkidle.diamond_reason', '{reason}', true);";

        public static async Task LabelAsync(FolkIdleDbContext db, string reason)
        {
            if (db.Database.CurrentTransaction == null) return;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('folkidle.diamond_reason', {reason}, true)");
        }
    }
}
