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
    /// THE ONLY SOURCE OF A PIECE'S SET ID (task 63, owner 2026-09-28).
    /// EquipmentInstance.SetId exists as a column, but nothing in this server
    /// ever wrote it - 0 of 945 live items carried one - so no set bonus had
    /// ever paid anyone while the Character screen advertised them.
    /// EquipmentSlotEngine.ComputeEquippedTotalsAsync now asks SetIdOf here
    /// instead of reading the column, which leaves one definition of "which
    /// set is this piece" for the bonus, the deed and the screen. The column is
    /// dead; do not start writing it, or there are two again.
    ///
    /// SET IDS: (tier - 1) * 2 + 1 for the tier's light family (offensive) and
    /// + 2 for its heavy one (defensive), so 1..10 with odd = offensive -
    /// SetBonusEngine.IsOffensiveSet relies on exactly that.
    /// </summary>
    public static class ArmourSetRegistry
    {
        /// <summary>Sets per region tier. The catalogue authors two, always.</summary>
        public const int SetsPerTier = 2;

        /// <summary>
        /// Each tier authors a light family and a heavy one. The light one is
        /// the OFFENSIVE set (a damage bonus), the heavy one the DEFENSIVE set
        /// (armour). Written out rather than derived: nothing in items.json
        /// says which is which, and ArmourSetTests asserts every tier has
        /// exactly one of each.
        /// </summary>
        private static readonly HashSet<string> OffensiveFamilies =
            new(StringComparer.Ordinal) { "linen", "hunter", "magus", "brawler", "doom" };

        public static bool IsOffensiveFamily(string family) => OffensiveFamilies.Contains(family);

        private static readonly Lazy<Dictionary<string, int>> _setIdByBaseId =
            new(BuildSetIds, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The set id a worn piece counts toward (1..10), or 0 for anything that
        /// is not authored armour. See the class comment for the numbering.
        /// </summary>
        public static int SetIdOf(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return 0;
            return _setIdByBaseId.Value.TryGetValue(baseItemId, out int id) ? id : 0;
        }

        /// <summary>The family name ("linen", "dread") behind a set id, or "".</summary>
        public static string FamilyOfSetId(int setId)
        {
            foreach (var (baseId, id) in _setIdByBaseId.Value)
            {
                if (id == setId) return FamilyOf(baseId);
            }
            return string.Empty;
        }

        private static Dictionary<string, int> BuildSetIds()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;

            for (int i = 0; i < items.Length; i++)
            {
                string baseItemId = ContentRegistry.GetItemBaseId(items[i].Id);
                string family = FamilyOf(baseItemId);
                if (family.Length == 0 || items[i].RegionTier < 1) continue;

                result[baseItemId] = (items[i].RegionTier - 1) * SetsPerTier + (IsOffensiveFamily(family) ? 1 : 2);
            }

            return result;
        }

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
