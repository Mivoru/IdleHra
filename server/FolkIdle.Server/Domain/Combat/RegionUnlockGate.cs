using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: region progression. Beating a region's boss opens the next region,
    // and opening a region is the same act as being allowed to wear its gear.
    //
    // REPLACES THE LEVEL GATE for equipment. EquipmentLevelGate derived a
    // required level from RegionTier AND QualityTier, which meant a region-1
    // Epic shield asked for level 12 - the item dropped in the only region a
    // new character could reach, and then refused to be worn there. Rarity no
    // longer gates anything: once a region is open, everything that drops in it
    // is wearable at every rarity, and the only question the game asks is how
    // far you have actually got.
    //
    // ONE number answers both questions on purpose. "Which regions may I enter"
    // and "whose gear may I wear" were about to be two derivations of the same
    // progression, and two derivations drift - the equip side would keep a
    // quality term, or the entry side would count a boss the other did not, and
    // the symptom would be gear you can farm and cannot equip. That is the bug
    // being fixed here, so it is not being rebuilt one layer up.
    public static class RegionUnlockGate
    {
        // Region 1 is open to a brand-new account that has beaten nothing.
        public const int StartingRegion = RaceUnlockRegistry.FirstRegion;

        // How far a player has got: the highest region they may enter, and
        // equally the highest RegionTier of gear they may wear.
        //
        // Consecutive by construction - the loop stops at the first boss that
        // is not down. That is not a redundant safety net over "count the
        // bosses killed": the bosses are ordinary monsters that nothing stops
        // you from attacking out of order today, so a player who reached region
        // 3 by some other route and killed its boss must not thereby unlock
        // region 4 while region 2 is still closed behind them.
        public static int HighestUnlockedRegion(IReadOnlySet<int> defeatedBossMonsterIds)
        {
            int unlocked = StartingRegion;

            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
                if (bossId == 0 || !defeatedBossMonsterIds.Contains(bossId))
                {
                    break;
                }

                unlocked = region + 1;
            }

            // Clearing the last region's boss cannot open a sixth region.
            return unlocked > RaceUnlockRegistry.LastRegion ? RaceUnlockRegistry.LastRegion : unlocked;
        }

        public static bool CanEnterRegion(int regionTier, IReadOnlySet<int> defeatedBossMonsterIds)
            => regionTier <= HighestUnlockedRegion(defeatedBossMonsterIds);

        // Legacy gear - the 90 non-canonical monsters drop RegionTiers 6-10,
        // beyond the five regions that have bosses - needs ALL FIVE down.
        //
        // Letting it through unchecked was the first attempt here, on the
        // reasoning that no boss exists for region 9 so nothing could ever
        // unlock it and a permanent lock would brick drops players already
        // hold. That reasoning was backwards: the region-9 tier-5 greataxe is
        // among the strongest items in the game, so "no boss guards it" made it
        // the ONE thing a level-1 character could buy and wear, which is the
        // exact cheese the gate exists to stop.
        //
        // Requiring the full canonical clear keeps it reachable - it is what
        // you get for finishing the game - and keeps it out of reach of a fresh
        // account. Tiers below 1 are malformed content rather than progression
        // and stay permissive, so a bad row cannot strand a drop.
        public static bool CanWearRegionTier(int itemRegionTier, IReadOnlySet<int> defeatedBossMonsterIds)
        {
            if (itemRegionTier < RaceUnlockRegistry.FirstRegion)
            {
                return true;
            }

            if (itemRegionTier > RaceUnlockRegistry.LastRegion)
            {
                return HasClearedEveryRegion(defeatedBossMonsterIds);
            }

            return itemRegionTier <= HighestUnlockedRegion(defeatedBossMonsterIds);
        }

        // Every canonical boss down, including the last region's - which
        // HighestUnlockedRegion cannot express, since it clamps at 5 whether
        // Malakor is dead or not.
        public static bool HasClearedEveryRegion(IReadOnlySet<int> defeatedBossMonsterIds)
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                if (!defeatedBossMonsterIds.Contains(RaceUnlockRegistry.GetRegionBossMonsterId(region)))
                {
                    return false;
                }
            }

            return true;
        }

        // Convenience for callers holding a base id rather than a tier.
        // Unknown base ids are wearable, matching CanWearRegionTier's reasoning.
        public static bool CanWearItem(string baseItemId, IReadOnlySet<int> defeatedBossMonsterIds)
        {
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out var definition))
            {
                return true;
            }

            return CanWearRegionTier(definition.RegionTier, defeatedBossMonsterIds);
        }

        // Which region bosses this player has put down, as monster ids.
        //
        // Lives here rather than in each engine because the market and the
        // equip path both need it and a second copy is how the two ends of one
        // rule drift - the exact failure this class exists to prevent.
        //
        // Only the five boss ids are queried: the codex grows a row per monster
        // ever killed and the gate needs five booleans. KillCount >= 1 is the
        // whole test - a boss is down or it is not, and nothing here cares how
        // many times.
        //
        // AsNoTracking because callers run inside a Serializable transaction
        // that must save only its own rows; tracking these would enlist them in
        // that save.
        public static async Task<IReadOnlySet<int>> LoadDefeatedBossesAsync(FolkIdleDbContext db, long playerId)
        {
            var bossIds = new List<int>();
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                bossIds.Add(RaceUnlockRegistry.GetRegionBossMonsterId(region));
            }

            var defeated = await db.MonsterCodexEntries.AsNoTracking()
                .Where(e => e.PlayerId == playerId && bossIds.Contains(e.MonsterId) && e.KillCount >= 1)
                .Select(e => e.MonsterId)
                .ToListAsync();

            return new HashSet<int>(defeated);
        }
    }
}
