using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Reads the twenty-odd numbers the Book of Deeds asks about off the
    /// database, once.
    ///
    /// SEPARATE FROM DeedRegistry on purpose: the registry is the content and
    /// must stay testable without a database, this is the plumbing and must
    /// stay changeable without touching the content. The seam is DeedContext,
    /// which is a struct of primitives and nothing else.
    ///
    /// Deliberately NOT on the tick. This runs when a player opens the Progress
    /// screen, which is the only moment the answer matters - the alternative is
    /// eight queries at 10 Hz for a checklist.
    /// </summary>
    public static class DeedProgressSource
    {
        public static async Task<DeedContext> LoadAsync(FolkIdleDbContext db, long playerId)
        {
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            if (player == null) return default;

            var characters = await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .OrderBy(c => c.SlotIndex)
                .ToListAsync();

            var main = characters.FirstOrDefault();

            long woodStock = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == VillageManagementEngine.WoodCommodityId)
                .Select(c => c.Quantity)
                .FirstOrDefaultAsync();

            // The larder lives in three column pairs on PlayerRecords rather
            // than a table - see StateCheckpointManager, which writes them back
            // from the tick's Food1..3 slots.
            int larderStocked =
                (player.LarderSlot1Count > 0 ? 1 : 0) +
                (player.LarderSlot2Count > 0 ? 1 : 0) +
                (player.LarderSlot3Count > 0 ? 1 : 0);

            var codex = await db.MonsterCodexEntries.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.KillCount > 0)
                .Select(c => new { c.MonsterId, c.KillCount })
                .ToListAsync();

            long totalKills = codex.Sum(c => (long)c.KillCount);

            long bossesSlain = codex.Count(c => RaceUnlockRegistry.GetRegionForBossMonsterId(c.MonsterId) > 0);

            // The WEAKEST of region 1's five, so the deed finishes only when
            // none of them has been neglected - which is the point of asking
            // for "each of them" rather than for a total.
            int lowestRegionOne = int.MaxValue;
            for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
            {
                int monsterId = ContentRegistry.FirstCanonicalMonsterId + i;
                int kills = codex.FirstOrDefault(c => c.MonsterId == monsterId)?.KillCount ?? 0;
                if (kills < lowestRegionOne) lowestRegionOne = kills;
            }
            if (lowestRegionOne == int.MaxValue) lowestRegionOne = 0;

            // A region's codex is complete when every one of its five monsters
            // has been recorded at least once.
            int bestCodexRegion = 0;
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int first = ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion;
                bool whole = true;
                for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
                {
                    if (!codex.Any(c => c.MonsterId == first + i)) { whole = false; break; }
                }
                if (whole) { bestCodexRegion = 1; break; }
            }

            var buildings = await db.VillageInfrastructures.AsNoTracking()
                .Where(v => v.PlayerId == playerId)
                .Select(v => new { v.BuildingId, v.CurrentLevel })
                .ToListAsync();

            int highestRarity = await db.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId)
                .Select(e => (int?)e.QualityTier)
                .MaxAsync() ?? 0;

            int regionsCompleted = await db.PlayerRegionCompletions.AsNoTracking()
                .CountAsync(r => r.PlayerId == playerId);

            var lineages = await db.CharacterLineages.AsNoTracking()
                .Where(l => characters.Select(c => c.Id).Contains(l.CharacterId))
                .ToListAsync();

            // A CHILD IS A LINEAGE MEMBER WITH A PARENT. Generation index alone
            // would count a founder of generation 0 as bred, and counting
            // characters would count the ones the game handed over for clearing
            // a boss.
            int childrenBred = lineages.Count(l => l.ParentPaternalId.HasValue || l.ParentMaternalId.HasValue);
            int epicChildren = lineages.Count(l => l.IsEpicMutation && (l.ParentPaternalId.HasValue || l.ParentMaternalId.HasValue));
            int bestAptitude = lineages.Count == 0 ? 0 : lineages.Max(l => l.AptitudeVector().Sum());

            return new DeedContext(
                Level: player.CurrentLevel,
                HasWeaponEquipped: main?.EquippedWeaponId is > 0,
                LarderStocked: larderStocked,
                WoodStock: woodStock,
                ItemsCrafted: player.TotalItemsCrafted,
                TotalKills: totalKills,
                BossesSlain: bossesSlain,
                RegionsCompleted: regionsCompleted,
                HighestUnlockedRegion: RegionUnlockGate.HighestUnlockedRegion(
                    codex.Select(c => c.MonsterId).ToHashSet()),
                DefeatedRegionBossMask: DefeatedBossMask(codex.Select(c => c.MonsterId)),
                ForgeFusions: player.ForgeFusionsCompleted,
                AffixRerolls: player.AffixRerollsPerformed,
                HighestRarityOwned: highestRarity,
                LargestActiveSetBonus: await LargestSetBonusAsync(db, playerId, main),
                ForgeLevel: LevelOf(buildings.Select(b => (b.BuildingId, b.CurrentLevel)), VillageManagementEngine.ForgeBuildingId),
                InnLevel: LevelOf(buildings.Select(b => (b.BuildingId, b.CurrentLevel)), VillageManagementEngine.InnBuildingId),
                VillageBuildingLevelTotal: buildings.Sum(b => b.CurrentLevel),
                WarehouseLevel: LevelOf(buildings.Select(b => (b.BuildingId, b.CurrentLevel)), VillageManagementEngine.WarehouseBuildingId),
                GatheringMasteryTotal: player.WoodcuttingMasteryLevel + player.MiningMasteryLevel
                                     + player.FishingMasteryLevel + player.HerbalismMasteryLevel,
                LowestRegionOneKillCount: lowestRegionOne,
                BestCodexRegionCompletion: bestCodexRegion,
                BestSeasonRank: player.BestSeasonRank,
                ChildrenBred: childrenBred,
                EpicChildrenBred: epicChildren,
                BestAptitudeTotal: bestAptitude);
        }

        private static int LevelOf(System.Collections.Generic.IEnumerable<(int BuildingId, int CurrentLevel)> buildings, int buildingId)
        {
            foreach (var building in buildings)
            {
                if (building.BuildingId == buildingId) return building.CurrentLevel;
            }
            return 0;
        }

        private static int DefeatedBossMask(System.Collections.Generic.IEnumerable<int> killedMonsterIds)
        {
            int mask = 0;
            foreach (int monsterId in killedMonsterIds)
            {
                int region = RaceUnlockRegistry.GetRegionForBossMonsterId(monsterId);
                if (region > 0) mask |= 1 << (region - RaceUnlockRegistry.FirstRegion);
            }
            return mask;
        }

        /// <summary>
        /// The biggest number of pieces of ONE set the main character is
        /// wearing. Two, three and five are where the bonuses step, and two
        /// separate deeds ask about it.
        /// </summary>
        private static async Task<int> LargestSetBonusAsync(FolkIdleDbContext db, long playerId, CharacterRecord? main)
        {
            if (main == null) return 0;

            var equippedIds = new System.Collections.Generic.List<long>();
            void Add(long? id) { if (id is > 0) equippedIds.Add(id.Value); }

            Add(main.EquippedWeaponId);
            Add(main.EquippedHelmetId);
            Add(main.EquippedChestId);
            Add(main.EquippedGlovesId);
            Add(main.EquippedLeggingsId);
            Add(main.EquippedBootsId);
            Add(main.EquippedAmuletId);
            Add(main.EquippedRingId);

            if (equippedIds.Count == 0) return 0;

            // SetId is stored on the instance, so this is a count of equal
            // ids rather than a re-derivation from the base item - the same
            // number EquipmentSlotEngine feeds SetBonusEngine.
            var setIds = await db.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId && equippedIds.Contains(e.Id) && e.SetId > 0)
                .Select(e => e.SetId)
                .ToListAsync();

            int best = 0;
            foreach (var group in setIds.GroupBy(id => id))
            {
                if (group.Count() > best) best = group.Count();
            }

            return best;
        }
    }
}
