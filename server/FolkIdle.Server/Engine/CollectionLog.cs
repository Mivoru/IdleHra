using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The collection log (task 57): the highest rarity ever owned of every
    /// catalogued piece, and the codex beside it, by region.
    ///
    /// WHAT COUNTS: the 75 `eq_` pieces in items.json, fifteen per region.
    /// Tools, materials and consumables are not collected - a tool has no
    /// rarity ladder to climb and a material is a stack, not a find.
    ///
    /// ONE RULE FOR BOTH WRITERS: a row only ever goes UP. The loot worker
    /// calls <see cref="RecordAsync"/> after its commit; the collection read
    /// calls it with whatever the Chest holds now (see
    /// PlayerCollectionEntry for why two writers and not one per path).
    /// </summary>
    public static class CollectionLog
    {
        /// <summary>Whether a BaseId is a collectable piece.</summary>
        public static bool IsCollectable(string baseItemId)
            => !string.IsNullOrEmpty(baseItemId)
               && baseItemId.StartsWith("eq_", StringComparison.Ordinal)
               && ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out _);

        /// <summary>
        /// Raises each (BaseId, tier) to at least that tier, in ONE statement.
        /// Non-collectable ids and tiers below 1 are skipped. Runs immediately,
        /// in whatever transaction <paramref name="db"/> holds.
        /// </summary>
        public static async Task<int> RecordAsync(FolkIdleDbContext db, long playerId, IReadOnlyDictionary<string, int> bestTierByBaseId, DateTime atUtc)
        {
            var rows = bestTierByBaseId
                .Where(kv => kv.Value > 0 && IsCollectable(kv.Key))
                // Sorted, so two writers for one player lock rows in the same
                // order and cannot deadlock against each other.
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            if (rows.Count == 0) return 0;

            var ids = rows.Select(r => r.Key).ToArray();
            var tiers = rows.Select(r => r.Value).ToArray();

            return await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO player_collection (""PlayerId"", ""BaseItemId"", ""BestTier"", ""FirstOwnedAtUtc"")
SELECT {playerId}, t.id, t.tier, {atUtc}
FROM unnest({ids}::text[], {tiers}::int[]) AS t(id, tier)
ON CONFLICT (""PlayerId"", ""BaseItemId"")
DO UPDATE SET ""BestTier"" = GREATEST(player_collection.""BestTier"", EXCLUDED.""BestTier"")
WHERE player_collection.""BestTier"" < EXCLUDED.""BestTier""");
        }

        public sealed class PieceView
        {
            public string BaseItemId { get; set; } = string.Empty;
            public int BestTier { get; set; }
        }

        public sealed class RegionView
        {
            public int Region { get; set; }
            public int PiecesOwned { get; set; }
            public int PiecesTotal { get; set; }
            /// <summary>Sum of best tiers against the ladder's top, 0-100.</summary>
            public int RarityPercent { get; set; }
            public int MonstersRecorded { get; set; }
            public int MonstersTotal { get; set; }
            public List<PieceView> Pieces { get; set; } = new();
        }

        public sealed class CollectionView
        {
            public int PiecesOwned { get; set; }
            public int PiecesTotal { get; set; }
            public int MonstersRecorded { get; set; }
            public int MonstersTotal { get; set; }
            /// <summary>Pieces and monsters together, 0-100 - the one headline number.</summary>
            public int Percent { get; set; }
            public List<RegionView> Regions { get; set; } = new();
        }

        /// <summary>
        /// Folds the Chest into the log, then answers the whole collection.
        /// </summary>
        public static async Task<CollectionView> BuildAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
        {
            var owned = await db.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId && e.BaseItemId.StartsWith("eq_"))
                .GroupBy(e => e.BaseItemId)
                .Select(g => new { BaseItemId = g.Key, Tier = g.Max(e => e.QualityTier) })
                .ToListAsync();
            await RecordAsync(db, playerId, owned.ToDictionary(o => o.BaseItemId, o => o.Tier), utcNow);

            var best = await db.PlayerCollectionEntries.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .ToDictionaryAsync(c => c.BaseItemId, c => c.BestTier);

            var recordedMonsters = (await db.MonsterCodexEntries.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.KillCount > 0)
                .Select(c => c.MonsterId)
                .ToListAsync()).ToHashSet();

            var view = new CollectionView();
            // An array, not the span: this method is async.
            ItemDefinition[] items = ContentRegistry.ItemDefinitions.ToArray();

            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                var rv = new RegionView { Region = region };
                int tierSum = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i].RegionTier != region) continue;
                    string baseId = ContentRegistry.GetItemBaseId(items[i].Id);
                    if (!baseId.StartsWith("eq_", StringComparison.Ordinal)) continue;

                    int tier = best.GetValueOrDefault(baseId);
                    rv.Pieces.Add(new PieceView { BaseItemId = baseId, BestTier = tier });
                    rv.PiecesTotal++;
                    if (tier > 0) rv.PiecesOwned++;
                    tierSum += tier;
                }
                rv.RarityPercent = rv.PiecesTotal == 0 ? 0 : tierSum * 100 / (rv.PiecesTotal * RarityTier.Transcendent);

                int firstMonster = ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion;
                rv.MonstersTotal = ContentRegistry.MonstersPerRegion;
                for (int m = 0; m < ContentRegistry.MonstersPerRegion; m++)
                {
                    if (recordedMonsters.Contains(firstMonster + m)) rv.MonstersRecorded++;
                }

                view.PiecesOwned += rv.PiecesOwned;
                view.PiecesTotal += rv.PiecesTotal;
                view.MonstersRecorded += rv.MonstersRecorded;
                view.MonstersTotal += rv.MonstersTotal;
                view.Regions.Add(rv);
            }

            int found = view.PiecesOwned + view.MonstersRecorded;
            int total = view.PiecesTotal + view.MonstersTotal;
            view.Percent = total == 0 ? 0 : found * 100 / total;
            return view;
        }
    }
}
