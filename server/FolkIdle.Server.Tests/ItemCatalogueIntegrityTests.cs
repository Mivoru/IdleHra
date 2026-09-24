using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    // Modul: ITEM IDS ARE ARRAY SLOTS, AND THIS FILE IS WHAT KEEPS THEM STILL.
    // Every items.json entry carries an explicit "Id", and ContentRegistry
    // stores it at index Id - 1 of arrays sized by the HIGHEST id. So an id is
    // not a label - it is the thing every loot row, every recipe and every
    // owned database row points at. d423b5b cut 111 items out of the catalogue
    // and task 33 (2026-09-24) cut the 40 legacy crafting materials, both
    // WITHOUT renumbering: a removed item leaves a hole, and a hole is legal.
    //
    // What nothing checked until now is the other half: that an id is never
    // SHIFTED (a rename, a re-sort that changes "Id"), and that a retired id is
    // never REUSED. The database holds numeric ids and slugs this repository
    // cannot see - a reused id would silently turn some player's old row into a
    // different object. ItemIdLedger.txt is the written-down history of every id
    // that has ever existed; these tests hold items.json to it.
    public class ItemCatalogueIntegrityTests
    {
        private readonly ITestOutputHelper _output;

        public ItemCatalogueIntegrityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static string LedgerPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server.Tests", "ItemIdLedger.txt")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir != null, "could not locate server/FolkIdle.Server.Tests/ItemIdLedger.txt from the test binary");
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server.Tests", "ItemIdLedger.txt");
        }

        // id -> BaseId, or "-" for a retired id.
        private static Dictionary<int, string> ReadLedger()
        {
            var ledger = new Dictionary<int, string>();
            foreach (string raw in File.ReadAllLines(LedgerPath()))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split('\t');
                Assert.True(parts.Length == 2, $"malformed ledger line: '{line}'");
                int id = int.Parse(parts[0]);
                Assert.False(ledger.ContainsKey(id), $"ledger lists id {id} twice");
                ledger[id] = parts[1];
            }
            return ledger;
        }

        [Fact]
        public void EveryLiveItemMatchesItsLedgerLine()
        {
            var ledger = ReadLedger();
            var failures = new List<string>();

            for (int id = 1; id <= ContentRegistry.ItemDefinitions.Length; id++)
            {
                string baseId = ContentRegistry.GetItemBaseId(id);
                if (baseId.Length == 0) continue;

                if (!ledger.TryGetValue(id, out string? recorded))
                {
                    failures.Add($"id {id} ('{baseId}') is new - append it to ItemIdLedger.txt");
                }
                else if (recorded == "-")
                {
                    failures.Add($"id {id} was retired and has been REUSED as '{baseId}' - pick a new id above the ledger's highest");
                }
                else if (recorded != baseId)
                {
                    failures.Add($"id {id} changed from '{recorded}' to '{baseId}' - ids are array slots, a rename/shift repoints every loot row, recipe and owned database row");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void EveryLedgerIdIsStillLiveUnlessRetired()
        {
            var failures = new List<string>();
            int retired = 0;

            foreach (var (id, recorded) in ReadLedger().OrderBy(pair => pair.Key))
            {
                if (recorded == "-") { retired++; continue; }
                string baseId = ContentRegistry.GetItemBaseId(id);
                if (baseId != recorded)
                {
                    failures.Add(baseId.Length == 0
                        ? $"id {id} ('{recorded}') disappeared from items.json - if deliberate, mark it - in the ledger"
                        : $"id {id} is '{baseId}' but the ledger says '{recorded}'");
                }
            }

            _output.WriteLine($"{retired} retired ids in the ledger");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        // Modul: the loot rows no table can reach. _lootSegments slices
        // _lootEntries by POSITION, so a row that lost its table cannot be
        // removed without re-slicing every table after it - the dead rows stay
        // as padding. Measured 2026-09-24: rows 0-21 (the pre-renumber fishing,
        // herbalism and mining-201 rows) and 47-76 (the old woodcutting and
        // mining nodes 101-105/201-205, renumbered to 1001-1005/2001-2005,
        // whose new tables start at row 77). Pinned here so a table that
        // silently loses its segment - or a new row that no table covers -
        // is a decision rather than an accident.
        private static readonly HashSet<int> UnreachableLootRows =
            new(Enumerable.Range(0, 22).Concat(Enumerable.Range(47, 30)));

        // Modul: Test_ContentRegistry_EveryRecipeIngredientIsObtainableFromSomeSource
        // only asks whether a recipe input appears in SOME loot table, so a
        // recipe and a loot row that both point at a hole pass it together.
        // This asks the question that matters: does the id name an item.
        //
        // Every row a loot table can reach must name a LIVE item. A row no
        // table reaches may also name a RETIRED id (the ten *_crafting_material
        // ores retired 2026-09-24 had no rows except dead padding), but
        // never an id that never existed - that would be a typo, not history.
        [Fact]
        public void NoLootRowOrRecipeNamesAMissingItem()
        {
            var failures = new List<string>();
            var ledger = ReadLedger();

            var loot = ContentRegistry.AllLootEntries;
            var reachable = new HashSet<int>();
            foreach (var (tableId, segment) in ContentRegistry.LootSegments)
            {
                if (segment.Start < 0 || segment.Start + segment.Count > loot.Length)
                {
                    failures.Add($"loot table {tableId} slices ({segment.Start}, {segment.Count}) past the {loot.Length}-row array");
                    continue;
                }
                for (int i = segment.Start; i < segment.Start + segment.Count; i++) reachable.Add(i);
            }

            for (int i = 0; i < loot.Length; i++)
            {
                int itemId = loot[i].ItemId;
                if (reachable.Contains(i))
                {
                    if (!ContentRegistry.ItemExists(itemId))
                        failures.Add($"loot row index {i} -> {itemId} (missing, and a table reaches it)");
                    if (UnreachableLootRows.Contains(i))
                        failures.Add($"loot row index {i} is listed as unreachable but a table now reaches it - update UnreachableLootRows");
                }
                else
                {
                    if (!UnreachableLootRows.Contains(i))
                        failures.Add($"loot row index {i} -> {itemId} is reached by no loot table - give it a segment, or add it to UnreachableLootRows");
                    bool retired = ledger.TryGetValue(itemId, out string? recorded) && recorded == "-";
                    if (!ContentRegistry.ItemExists(itemId) && !retired)
                        failures.Add($"dead loot row index {i} -> {itemId}, which never was an item");
                }
            }

            var recipes = ContentRegistry.Recipes;
            for (int i = 0; i < recipes.Length; i++)
            {
                var r = recipes[i];
                if (!ContentRegistry.ItemExists(r.ResultItemId))
                    failures.Add($"recipe {r.ResultItemId} result -> {r.ResultItemId} (missing)");
                if (r.Mat1Count > 0 && !ContentRegistry.ItemExists(r.Mat1Id))
                    failures.Add($"recipe {r.ResultItemId} Mat1 -> {r.Mat1Id} (missing)");
                if (r.Mat2Count > 0 && !ContentRegistry.ItemExists(r.Mat2Id))
                    failures.Add($"recipe {r.ResultItemId} Mat2 -> {r.Mat2Id} (missing)");
            }

            _output.WriteLine($"{loot.Length} loot rows ({reachable.Count} reachable), {recipes.Length} recipes checked");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        // Modul: _baseIdToItemDefinitionIndex is last-wins, so a duplicate slug
        // does not throw - it silently shadows the earlier item for every
        // lookup by BaseId.
        [Fact]
        public void NoTwoItemsShareABaseId()
        {
            var duplicates = new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int id = 1; id <= ContentRegistry.ItemDefinitions.Length; id++)
            {
                string baseId = ContentRegistry.GetItemBaseId(id);
                if (baseId.Length == 0) continue;
                if (seen.TryGetValue(baseId, out int first))
                    duplicates.Add($"'{baseId}' is both id {first} and id {id}");
                else
                    seen[baseId] = id;
            }

            Assert.True(duplicates.Count == 0, string.Join(Environment.NewLine, duplicates));
        }
    }
}
