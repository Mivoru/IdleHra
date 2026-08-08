using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Settles the village arrival clock: works out who turned up while the
    /// player was away, and rolls their blood against the Inn.
    ///
    /// SETTLED ON LOGIN rather than ticked. Arrivals are hours apart, so a 10 Hz
    /// loop checking them would run about three hundred thousand times per
    /// villager. What matters is that the roster is right the moment a player
    /// looks at it, and login is the only moment that can be true of anything
    /// they were not watching.
    ///
    /// The rules themselves are in VillagerArrivalRules and are tested on their
    /// own; this is the part that touches the database.
    /// </summary>
    public static class VillageArrivalEngine
    {
        /// <summary>
        /// Brings the village up to date. Returns how many people arrived, so
        /// a caller can mention it.
        ///
        /// Idempotent in the way that matters: the clock only ever advances by
        /// whole consumed intervals, so calling this twice in the same second
        /// does nothing the second time.
        /// </summary>
        public static async Task<int> SettleAsync(
            FolkIdleDbContext db, PlayerRecord player, int innLevel, long nowEpoch)
        {
            int population = await db.VillageNewcomers.CountAsync(v => v.PlayerId == player.Id);

            // Modul: A NEW ACCOUNT IS NOT OWED FIFTY YEARS OF VILLAGERS.
            //
            // LastVillagerArrivalEpoch defaults to 0, and treating that as a
            // real timestamp would compute the elapsed time since 1970 and fill
            // the village to its cap on the first login - handing away the
            // entire gene-pool hunt before the player has built anything.
            if (player.LastVillagerArrivalEpoch <= 0)
            {
                player.LastVillagerArrivalEpoch = nowEpoch;

                // The season opens with two, rolled at Inn level 1, so the
                // first two days are not dead and nobody is worth marrying yet.
                if (population == 0)
                {
                    for (int i = 0; i < VillagerArrivalRules.SeasonStartVillagers; i++)
                    {
                        db.VillageNewcomers.Add(Roll(player.Id, VillagerArrivalRules.SeasonStartInnLevel, nowEpoch));
                    }
                    await db.SaveChangesAsync();
                    return VillagerArrivalRules.SeasonStartVillagers;
                }

                await db.SaveChangesAsync();
                return 0;
            }

            long elapsed = nowEpoch - player.LastVillagerArrivalEpoch;
            var (arrivals, consumed) = VillagerArrivalRules.ArrivalsSince(elapsed, innLevel, population);

            if (consumed > 0) player.LastVillagerArrivalEpoch += consumed;
            if (arrivals <= 0)
            {
                await db.SaveChangesAsync();
                return 0;
            }

            for (int i = 0; i < arrivals; i++)
            {
                db.VillageNewcomers.Add(Roll(player.Id, innLevel, nowEpoch));
            }

            await db.SaveChangesAsync();
            return arrivals;
        }

        /// <summary>
        /// Turns somebody away, freeing a slot.
        ///
        /// Deleting rather than flagging: a dismissed newcomer has no history
        /// worth keeping - they never entered the line - and a table of
        /// tombstones would grow all season for nothing. An ELDER is different
        /// and is kept, because they did.
        /// </summary>
        public static async Task<bool> DismissAsync(FolkIdleDbContext db, long playerId, long newcomerId)
        {
            var row = await db.VillageNewcomers
                .FirstOrDefaultAsync(v => v.Id == newcomerId && v.PlayerId == playerId);

            if (row == null || row.IsElder) return false;

            db.VillageNewcomers.Remove(row);
            await db.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Rolls one newcomer against the Inn.
        ///
        /// The race is uniform across the unlocked span rather than weighted:
        /// the point of outside blood is that it is DIFFERENT from the line, so
        /// biasing it toward what the player already has would defeat it.
        /// </summary>
        private static VillageNewcomer Roll(long playerId, int innLevel, long nowEpoch)
        {
            var newcomer = new VillageNewcomer
            {
                PlayerId = playerId,
                // One race per region, the same five the unlock ladder uses.
                RaceId = Random.Shared.Next(
                    RaceUnlockRegistry.FirstRegion, RaceUnlockRegistry.LastRegion + 1),
                IsFemale = Random.Shared.Next(2) == 1,
                ArrivedAtEpoch = nowEpoch,
                IsElder = false,
            };

            newcomer.SetAptitudeVector(BreedingAptitudes.RollVillager(innLevel, Random.Shared));
            return newcomer;
        }
    }
}
