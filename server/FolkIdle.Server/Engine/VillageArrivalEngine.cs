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

            byte[] races = await UnlockedRacesAsync(db, player.Id);

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
                        db.VillageNewcomers.Add(Roll(player.Id, VillagerArrivalRules.SeasonStartInnLevel, nowEpoch, races));
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
                db.VillageNewcomers.Add(Roll(player.Id, innLevel, nowEpoch, races));
            }

            await db.SaveChangesAsync();
            return arrivals;
        }

        /// <summary>
        /// Throws a feast and attracts somebody NOW, rather than waiting out
        /// the day or two the Inn's clock takes.
        ///
        /// The price escalates 1.6x per recruitment within a season so this
        /// cannot be spammed into a slot machine for a twenty-roll, and it is
        /// priced in gold because the top of the economy has no sink that is
        /// not the Forge. See VillagerArrivalRules, which owns every number.
        ///
        /// Returns the refusal, or null on success - a reason rather than a
        /// bool because both refusals are things the player can act on ("the
        /// village is full" and "that costs more than you have"), and a command
        /// that fails in silence is how the last four features looked broken.
        ///
        /// DOES NOT TOUCH THE ARRIVAL CLOCK. Paying for somebody is not the
        /// same as waiting for them, and folding the recruit into the clock
        /// would mean gold could postpone the free arrival it was meant to
        /// pre-empt.
        /// </summary>
        public static async Task<string?> RecruitAsync(
            FolkIdleDbContext db, PlayerRecord player, int innLevel, long nowEpoch)
        {
            int population = await db.VillageNewcomers.CountAsync(v => v.PlayerId == player.Id);

            var gold = await db.CommodityRecords
                .FirstOrDefaultAsync(c => c.PlayerId == player.Id && c.ItemId == "gold");
            long held = gold?.Quantity ?? 0L;

            string? refusal = VillagerArrivalRules.RecruitBlockedReason(
                innLevel, population, held, player.VillagerRecruitmentsThisSeason);

            if (refusal != null) return refusal;

            // Read the price back from the same function that just approved it
            // rather than recomputing it from a different argument list.
            long cost = VillagerArrivalRules.RecruitCostGold(player.VillagerRecruitmentsThisSeason);
            gold!.Quantity -= cost;
            player.VillagerRecruitmentsThisSeason++;

            byte[] races = await UnlockedRacesAsync(db, player.Id);
            db.VillageNewcomers.Add(Roll(player.Id, innLevel, nowEpoch, races));

            await db.SaveChangesAsync();
            return null;
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
        /// The races that can turn up: Human, plus whatever the player has
        /// unlocked by clearing a region boss.
        ///
        /// UNLOCKED ONLY, and this is load-bearing rather than flavour.
        /// Breeding refuses a pair whose race differs, so a villager of a race
        /// the player owns no character of is a portrait and nothing else.
        /// Rolling uniformly over all six would have left five in six arrivals
        /// unmarriageable on a fresh account - the village would fill with
        /// people who exist to be dismissed. Restricting the roll instead means
        /// a new player's whole village is marriageable, and clearing a boss
        /// widens the pool as well as granting a pair.
        /// </summary>
        private static async Task<byte[]> UnlockedRacesAsync(FolkIdleDbContext db, long playerId)
        {
            var unlocked = await db.PlayerRaceUnlocks
                .AsNoTracking()
                .Where(u => u.PlayerId == playerId)
                .Select(u => u.RaceId)
                .ToListAsync();

            // Human needs no unlock row - every account starts with it.
            var races = new System.Collections.Generic.List<byte> { RaceIds.Human };
            for (int i = 0; i < unlocked.Count; i++)
            {
                byte raceId = (byte)unlocked[i];
                if (RaceUnlockRegistry.IsPlayableRace(raceId) && !races.Contains(raceId))
                {
                    races.Add(raceId);
                }
            }

            return races.ToArray();
        }

        /// <summary>
        /// Rolls one newcomer against the Inn.
        ///
        /// The race is uniform across the unlocked set rather than weighted
        /// toward the player's own line: the point of outside blood is that it
        /// is DIFFERENT, so biasing it toward what they already have would
        /// defeat it.
        /// </summary>
        private static VillageNewcomer Roll(long playerId, int innLevel, long nowEpoch, byte[] races)
        {
            var newcomer = new VillageNewcomer
            {
                PlayerId = playerId,
                RaceId = races.Length > 0 ? races[Random.Shared.Next(races.Length)] : RaceIds.Human,
                IsFemale = Random.Shared.Next(2) == 1,
                ArrivedAtEpoch = nowEpoch,
                IsElder = false,
            };

            newcomer.SetAptitudeVector(BreedingAptitudes.RollVillager(innLevel, Random.Shared));
            return newcomer;
        }
    }
}
