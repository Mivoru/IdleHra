using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Which armour set a piece belongs to.
    ///
    /// THE CATALOGUE HAS NO SET FIELD. items.json carries an id, a region tier,
    /// a gold value, two stats and a BaseId - and nothing else - so the only
    /// place set membership is written down is the naming convention. Every
    /// tier authors exactly two families of five: linen/steel, sentry/hunter,
    /// magus/obsidian, brawler/monolith, doom/dread.
    ///
    /// DERIVED RATHER THAN AUTHORED, for the reason EquipmentDropTable is:
    /// a hand-written table beside a naming convention is two things that can
    /// disagree, and this codebase has lost items to exactly that before. The
    /// family is the token after `eq_`, with one wrinkle - tier 5 names its
    /// dread helmet `eq_dreadnought_helm_...` while the other four pieces are
    /// `eq_dread_...`, so families are merged when one name is a prefix of the
    /// other. `ArmourSetTests` asserts the outcome (two families of five per
    /// tier, ten in all) rather than trusting the rule.
    ///
    /// NOT THE SAME NUMBER AS EquipmentInstance.SetId, and that is worth
    /// stating plainly: that column exists, SetBonusEngine reads it, and
    /// NOTHING IN THIS SERVER HAS EVER WRITTEN IT - nine places construct an
    /// EquipmentInstance and not one assigns a set. So set bonuses do not fire
    /// on any item any player owns. This registry is what a fix would be built
    /// from; wiring it into the drops is a balance change and is deliberately
    /// not done here.
    /// </summary>
    public static class ArmourSetRegistry
    {
        /// <summary>Sets per region tier. The catalogue authors two, always.</summary>
        public const int SetsPerTier = 2;

        private static readonly Lazy<Dictionary<string, string>> _familyByBaseId =
            new(Build, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The set this piece belongs to - "linen", "dread" - or "" for
        /// anything that is not authored armour (weapons, amulets, rings,
        /// tools).
        /// </summary>
        public static string FamilyOf(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return string.Empty;
            return _familyByBaseId.Value.TryGetValue(baseItemId, out string? family) ? family : string.Empty;
        }

        /// <summary>
        /// The distinct families authored at a region tier, in a stable order.
        ///
        /// STABLE MATTERS: EquipmentDropTable deals armour by alternating
        /// between the two, so an order that changed between boots would put a
        /// different mix on every monster after a restart.
        /// </summary>
        public static List<string> FamiliesAt(int regionTier)
        {
            var families = new List<string>(SetsPerTier);
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].RegionTier != regionTier) continue;

                string family = FamilyOf(ContentRegistry.GetItemBaseId(items[i].Id));
                if (family.Length == 0 || families.Contains(family)) continue;

                families.Add(family);
            }

            families.Sort(StringComparer.Ordinal);
            return families;
        }

        private static Dictionary<string, string> Build()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;

            // Raw first token per armour piece, grouped by tier so a family
            // name reused across tiers cannot merge two different sets.
            var rawByTier = new Dictionary<int, List<(int ItemId, string BaseId, string Raw)>>();

            for (int i = 0; i < items.Length; i++)
            {
                string baseItemId = ContentRegistry.GetItemBaseId(items[i].Id);
                if (!IsArmourPiece(baseItemId)) continue;

                string raw = FirstToken(baseItemId);
                if (raw.Length == 0) continue;

                if (!rawByTier.TryGetValue(items[i].RegionTier, out var bucket))
                {
                    bucket = new List<(int, string, string)>();
                    rawByTier[items[i].RegionTier] = bucket;
                }
                bucket.Add((items[i].Id, baseItemId, raw));
            }

            foreach (var (_, bucket) in rawByTier)
            {
                // Shortest name wins the merge, so `dreadnought` folds into
                // `dread` rather than the other way round - which keeps the
                // family name the one four of the five pieces already use.
                var canonical = new List<string>();
                foreach (var entry in bucket)
                {
                    if (!canonical.Contains(entry.Raw)) canonical.Add(entry.Raw);
                }
                canonical.Sort((a, b) => a.Length != b.Length ? a.Length - b.Length : string.CompareOrdinal(a, b));

                foreach (var entry in bucket)
                {
                    string family = entry.Raw;
                    foreach (string candidate in canonical)
                    {
                        if (entry.Raw.StartsWith(candidate, StringComparison.Ordinal))
                        {
                            family = candidate;
                            break;
                        }
                    }
                    result[entry.BaseId] = family;
                }
            }

            return result;
        }

        private static bool IsArmourPiece(string baseItemId)
            => baseItemId.Contains("_armor_slot", StringComparison.Ordinal);

        private static string FirstToken(string baseItemId)
        {
            if (!baseItemId.StartsWith("eq_", StringComparison.Ordinal)) return string.Empty;

            int start = 3;
            int end = baseItemId.IndexOf('_', start);
            return end < 0 ? string.Empty : baseItemId[start..end];
        }
    }
}
