# Task 33: delete the 40 legacy crafting materials

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the 40 unreachable `*_crafting_material` definitions (ten bars, ids 184-193, and thirty superseded monster-drop materials) from `server/GameData/items.json` without changing the meaning of any surviving item id, and leave behind two guard tests so that no later content edit can shift, reuse or dangle an item id without a failing test.

**Architecture:** Item ids are **explicit** (`"Id"` on every `items.json` entry), and the registry arrays are sized by the **highest** id and indexed by `Id - 1`. Removing an entry leaves an inert hole; it does not renumber anything. Nothing that holds a numeric id (loot tables, recipes, the database) points at any of the 40, and production holds no row naming any of them. The one real dependency is the client sprite generator, whose `MATERIAL_ALIASES` names two of the 40 as alias targets and hard-fails when a target disappears. So this is a content removal plus a two-line generator edit, a regenerated sprite table, a lowered art budget, one Wiki sentence, and the guard tests.

**Tech stack:** JSON content, C# / .NET 8 xUnit (`server/FolkIdle.Server.Tests`), Node (`client_web/scripts/generate-sprites.mjs`), Svelte (one line of `Wiki.svelte`), Python (`ops/validate_content.py`).

**Spec:** `docs/TASK_BOARD.md` "## 33. Delete the 40 legacy crafting materials" (line ~3518) and `docs/crafting_material_audit.md` (the list of 40 and the precondition "query production first").

## Global Constraints

- **Do NOT renumber anything.** Ids are explicit and positional in the sense that the registry uses `Id - 1` as an array index. Delete the 40 entries; do not touch the `Id` of any other entry, and do not "close the gaps". The catalogue already has 111 holes from `d423b5b` ("cut the catalogue to the 75 canonical pieces"), and that commit is the precedent.
- **Do not edit `_lootEntries` in `ContentRegistry.cs`.** The loot tables are a *second* positional structure: `_lootSegments` maps a table id to a `(Start, Count)` slice of `_lootEntries`, so removing or inserting a row there silently re-slices every table after it. None of the 40 ids appear in it (see Findings), so there is no reason to touch it.
- **Do not touch `client/`** (the retired Unity project). Its stale `client/Assets/StreamingAssets/GameData/items.json` and `AssetRegistry.asset` still name these items; task 34 deletes them wholesale. Editing them here is churn in files nothing reads.
- **Stop the running server before any `dotnet build`/`dotnet test`** (CLAUDE.md; the `guard_stale_build.py` hook blocks it anyway, and a blocked call runs *nothing*, including any edit chained in front of it).
- **`dotnet test` needs Docker.** The new guard tests do not use the Postgres fixture, but the full-suite run in Task 4 does.
- **Rewrite `items.json` with a script that round-trips byte-exactly**, not by hand. Verified 2026-09-23: `JSON.stringify(JSON.parse(f), null, 2) + '\n' === f` on the current file (LF, no BOM, two-space indent), so a Node filter produces a diff that is exactly the 40 removed objects.
- **The content-validator hook fires only on the Write/Edit tools.** A Node script that rewrites `items.json` bypasses it, so run the validator by hand (Task 2 step 3). In this machine's Git Bash `python` is broken ("Failed to import encodings"); run it from PowerShell.
- **Each task gets its own commit** (see "Commit boundaries"). The guard tests land *before* the deletion, so the deletion commit is visibly the thing that turns the ledger's 40 lines into tombstones.
- **Deploy only after the owner's go-ahead**, and re-run the production check (Task 1 step 2) immediately before deploying — it is cheap, and the only way a legacy row could appear between now and then is if something still grants one.

---

## Findings (researched 2026-09-23, read-only)

### 1. How item ids are assigned: explicit, holes allowed, never renumbered

- Every `items.json` entry carries an explicit `"Id"` field (fields: `Id, RegionTier, BaseValueGold, FlatAttackPower, FlatDefenseRating, BaseId`). The file is sorted by id; 330 entries, highest id 441, **111 holes** already.
- `ContentRegistry.Initialize` (`server/FolkIdle.Server/Engine/ContentRegistry.cs` ~1361-1454):
  - rejects non-positive and duplicate ids, then sizes `_itemDefinitions` and `_itemBaseIds` by the **highest** id, not the entry count;
  - fills holes with `BaseId = string.Empty`, which `GetItemBaseId` returns and `ItemExists(id)` (line 757) reads as "not a thing";
  - stores each entry at `index = it.Id - 1`.
  The block's own `// Modul: THE CATALOGUE MAY HAVE HOLES IN IT.` comment records why: sizing by count would have thrown on the highest id, and renumbering would have "repointed every loot table, recipe and owned row in the database at a different object".
- `ops/validate_content.py` `validate_items` mirrors that: ids positive and unique, **gaps legal**.
- The client builds `new Map(itemList.map((i) => [i.Id, i]))` and a `BaseId` map (`client_web/src/lib/net/content.ts:171-173`) — keyed by the explicit id, never by array position.
- `NEXT_STEPS_BACKLOG.md:2973` records it as a standing trap: "Ids are positional (`_itemBaseIds[id - 1]`) and 111 entries were removed, so never renumber and never assume 1..N."
- **What "positional" means in CLAUDE.md/the audit, then:** the id *is* the array slot, so an id must never change or be reused. It does **not** mean an entry's position in the JSON array matters. Removing the 40 shifts nothing. None of the 40 is the highest id (441), so the array length does not change either.

**Therefore: plain removal, not tombstone entries in `items.json`.** A tombstone left in the file (say, `"BaseId": "retired_184"`) would keep 40 dead items in the Wiki item database, the Market item browser and `sprites.missing.txt`, and the registry already represents a hole perfectly well. The tombstone belongs in a **test ledger** instead (Task 1), where it stops an id being reused without costing the running game anything.

### 2. The 40 items

All 40 are `RegionTier 1, BaseValueGold 10, FlatAttack 0, FlatDefense 0`, except the bars 185-193, which step RegionTier 2..10 and BaseValueGold 40..1000. Every BaseId ends `_crafting_material`.

```
  2 highland_wool          64 runestone_shard          148 locust_wing
  6 ominous_feather        67 ancient_burial_cloth     151 falcon_talon
 12 selkie_skin_fragment   76 pure_aurora_filament     154 desiccated_bone
 22 shadow_feather         79 sentinel_alloy_plate     166 rat_pelt
 25 tainted_tusk           82 prismatic_core_prism     172 sharp_claw
 40 scorpion_stinger       85 ethereal_shroud_fabric   175 wolf_tooth
 43 crystallized_venom    100 waterlogged_cloth        177 thick_wolf_hide_rare
 46 harpy_talon           112 bat_wing                 184 copper_bar   189 gold_bar
 58 frosted_down          115 carapace_shard           185 bronze_bar   190 mithril_bar
 61 glacial_claw          118 chitin_segment           186 iron_bar     191 adamantite_bar
                          130 eagle_feather            187 steel_bar    192 obsidian_bar
                          133 thick_goat_horn          188 silver_bar   193 celestial_bar
                          136 frozen_scale
```

The ten live ones stay: 1 gold_ore, 21 mithril_ore, 39 adamantite_ore, 57 obsidian_ore, 75 celestial_ore, 93 tin_ore, 111 iron_ore, 129 coal_node, 147 silver_ore, 165 copper_ore (all `*_crafting_material`; all in the mining loot rows `_lootEntries` indices 61-76, and 129 at index 21).

### 3. Reference table: every place an item is named, and what it says about the 40

Searched by **numeric id** (the audit's point: recipes and loot tables never use the slug, so a slug grep alone proves nothing) *and* by **BaseId slug**, across `server/`, `client_web/`, `ops/`, `docs/`, `.github/`, `client/`.

| Surface | Keyed by | Hits for the 40 | Action |
|---|---|---|---|
| `server/GameData/items.json` | explicit `Id` | the 40 definitions | **Delete** (Task 2) |
| Loot tables — `ContentRegistry._lootEntries` (monster drops 501-525, gathering nodes, the 3001 table) | numeric `ItemId` | **none** (every `ItemId = N` literal in `server/FolkIdle.Server` enumerated; none in the set) | none — and do not edit this array, it is sliced positionally |
| Recipes — `ContentRegistry._recipes` (`Mat1Id`, `Mat2Id`, `ResultItemId`) | numeric | **none** (the smelting recipes that consumed/produced bars were removed earlier; 408 is now the lowest result the tests use) | none |
| `EquipmentDropTable` | derived from `items.json` by slot BaseId | none — materials are not equipment | none |
| `VillageManagementEngine.TierMaterials`, guild `BuffTierMaterials`, `GuildContributionEngine` | BaseId strings (`copper_ore`, `birch_log`, ...) | none | none |
| `AlchemyCompendium`, larder food ids, `StarterEquipmentGrant` | numeric/BaseId | none | none |
| `DevFixtureSeeder` | stocks recipe materials **derived from `_recipes`** (`CollectRecipeMaterialIds`) + named BaseIds | none (derived set cannot contain them: no recipe names them) | none — but see the local-DB note in Task 2 |
| `server/GameData/localizations.json` | BaseId keys | none | none |
| Quests / daily quests / achievements | — | none (grep for ids and slugs) | none |
| Wire protocol (`protocol.generated.ts`, `--dump-protocol`) | carries ints, no item enum | none | none — no regeneration needed |
| `client_web/scripts/generate-sprites.mjs` `MATERIAL_ALIASES` | BaseId | **`'Iron bar': ['iron_bar_crafting_material', 'iron_ore']` (line 120), `'Silver bar': ['silver_bar_crafting_material', 'silver_ore']` (line 137)** | **Edit** — the generator pushes `alias ... no longer exists in items.json` into `problems` and `process.exit(1)`s, so deleting 186/188 without this breaks `npm run build`, `build:web` and CI's `--check` |
| same file, line 467 comment | prose example `"Iron bar" -> iron_bar_crafting_material` | example names a deleted item | Edit the example to a surviving one (e.g. `"Copper" -> copper_ore_crafting_material`) |
| `client_web/src/lib/ui/sprites.generated.ts` | generated | `iron_bar_crafting_material` (line 57), `silver_bar_crafting_material` (line 83) | **Regenerate** |
| `client_web/src/lib/ui/sprites.missing.txt` | generated | 38 of the 40 (all but the two above), under "crafting materials (46)"; header "total 165 of 330" | **Regenerate** → expect `total 127 of 290`, "crafting materials (8)" |
| `generate-sprites.mjs` `MISSING_ART_BUDGET = 165` | ratchet, may only fall | — | **Lower to 127** (the ratchet wants it lowered whenever the count falls; leaving 165 lets 38 art-less items creep back unnoticed) |
| `client_web/src/routes/Wiki.svelte:594` | player-facing prose | "fifty ids ending `_crafting_material`" | **Edit** to ten (Task 3) |
| `client_web/src/lib/net/content.ts:241` | `/_crafting_material$/` name-suffix stripper | generic; ten live items still need it | none |
| `client_web/tests/*.test.ts` | — | only live ones (`gold_ore_crafting_material` in `itemNames`/`slots`, `copper_ore_crafting_material` in a `sprites.test.ts` comment) | none |
| `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs:11397` | stale comment | `// copper_bar_crafting_material: 3x mat 93 + 1x mat 129, Smelting.` above a test that actually uses 408 (Birch Axe) | Delete the stale line (Task 3); `:12140` already says "was recipe 184" and is accurate history, leave it |
| `SimulationEngine.cs:4765` Kobold weight | `droppedBaseId.Contains("_bar_")` | would match only the deleted bars | leave — see "Not in this plan" |
| `client/` (retired Unity) | — | `StreamingAssets/GameData/items.json` (40), `AssetRegistry.asset`, `AssetRegistryBuilder.cs` | **none** — task 34 |
| Docs | — | `TASK_BOARD.md`, `crafting_material_audit.md` | Update (Task 5) |

The existing test `Test_ContentRegistry_EveryRecipeIngredientIsObtainableFromSomeSource` does **not** catch a dangling id: it only asks whether a recipe input appears in *some* loot table, so a recipe and a loot row that both point at a hole pass it. That is the gap the new guard fills.

### 4. Production checks (Supabase, read-only SELECTs, 2026-09-23)

Real table/column names were taken from `information_schema.columns` (every public column named like `%item%`, `%material%`, `%recipe%`, `%payload%`, `%commodity%`). Each check matched the 40 BaseIds, the bare numeric ids as strings, and the numeric ids as integers where the column is an integer.

| Table.column | Rows naming any of the 40 |
|---|---|
| `"CommodityRecords"."ItemId"` | 0 (also 0 rows ending `_crafting_material` at all, and 0 matching `_bar(_|$)`) |
| `"VillageStashInstances"."ItemId"` | 0 (table is empty) |
| `"EquipmentInstances"."BaseItemId"` | 0 (of 1,811 rows) |
| `"MarketEquipmentInstances"."BaseItemId"` | 0 |
| `"MarketOrderRecords"."BaseItemId"` / `"CommodityId"` | 0 / 0 (1 order row total) |
| `historical_market_archives."BaseItemId"` / `"CommodityId"` | 0 / 0 |
| `"MailboxInstances"."BaseItemId"` | 0 (table is empty) |
| `"GuildDepotBalances"."ItemDefinitionId"` | 0 (3 rows total) |
| `"GuildContributionLedgers"."MaterialId"`, `"GuildLogisticsDepots"."MaterialId"` | 0, 0 |
| `"GuildMaterialSinkLedgers"."CommodityId"` | 0 |
| `"PlayerCraftingSlots"."ActiveRecipeId"` | 0 (table is empty) |
| `"PlayerRecords"."LarderSlot1/2/3ItemId"` | 0 |
| `pending_grants."PayloadJson"` (substring match on each slug) | 0 (table is empty) |
| `characters."ActiveActivityId" >= 5000` (crafting band) | none active |

Controls confirmed the queries can match: `"CommodityRecords"` has 119 rows, and its ore-like slugs are all the catalogued `*_ore` family. There is no bank table in the schema (the chrono bank was deleted 2026-09-02) — the stash and mailbox are the "bank" surfaces and both are covered.

**Conclusion: no data migration and no compensation are needed.** The stranded-ore migration (`20260901194818_FoldStrandedCraftingMaterialOres`) was necessary because players held those rows; here nobody holds anything.

---

## How to approach this (for the implementing agent)

1. **The research is done; re-verify cheaply, don't redo it.** Task 1 step 1 is a mechanical re-check that the reference table above still holds on current `main` (a merge since 2026-09-23 could have added a reference). If it turns up a new hit, stop and classify it before deleting anything.
2. **Guard first, then delete.** Land the ledger + dangling-reference test while the 40 still exist (they pass), then make the deletion commit flip 40 ledger lines to tombstones. That ordering proves the guard actually fails on a shift/reuse (Task 1 step 5 asks you to demonstrate it) and makes the deletion diff self-explaining.
3. **The sprite generator is the only thing that will break**, and it breaks loudly. If `npm run generate:sprites` exits 1 with `alias "Iron bar" -> "iron_bar_crafting_material" no longer exists`, you skipped Task 2 step 4.
4. **Do not "fix" anything in the "Not in this plan" list as a drive-by.** Each is a behaviour change with its own blast radius.
5. Work on a branch (`chore/task-33-delete-legacy-materials`), PR to `main`, as the recent tasks did.

---

### Task 1: Guard tests (before any deletion)

**Files:**
- Create: `server/FolkIdle.Server.Tests/ItemIdLedger.txt`
- Create: `server/FolkIdle.Server.Tests/ItemCatalogueIntegrityTests.cs`
- Modify: `server/FolkIdle.Server/Engine/ContentRegistry.cs` — add one read-only accessor next to `GetLootTable` (~line 1159):
  ```csharp
  /// <summary>Every authored loot row, across all tables. For content-integrity tests.</summary>
  public static ReadOnlySpan<LootTableEntry> AllLootEntries => _lootEntries;
  ```
  (Enumerating tables through monsters and gathering nodes, as the existing obtainability test does, misses any table no monster or node currently points at, e.g. 3001. The whole array is the honest scope.)

- [ ] **Step 1: Re-verify the reference table**

  From Git Bash, repo root:
  ```bash
  SLUGS='highland_wool|ominous_feather|selkie_skin_fragment|shadow_feather|tainted_tusk|scorpion_stinger|crystallized_venom|harpy_talon|frosted_down|glacial_claw|runestone_shard|ancient_burial_cloth|pure_aurora_filament|sentinel_alloy_plate|prismatic_core_prism|ethereal_shroud_fabric|waterlogged_cloth|bat_wing|carapace_shard|chitin_segment|eagle_feather|thick_goat_horn|frozen_scale|locust_wing|falcon_talon|desiccated_bone|rat_pelt|sharp_claw|wolf_tooth|thick_wolf_hide_rare|copper_bar|bronze_bar|iron_bar|steel_bar|silver_bar|gold_bar|mithril_bar|adamantite_bar|obsidian_bar|celestial_bar'
  git grep -n -I -E "\b($SLUGS)" -- . ':!client/' ':!docs/' ':!server/GameData/items.json'
  git grep -nE "(ItemId|Mat1Id|Mat2Id|ResultItemId|ItemDefinitionId|MaterialId)\s*[=:]\s*(2|6|12|22|25|40|43|46|58|61|64|67|76|79|82|85|100|112|115|118|130|133|136|148|151|154|166|172|175|177|18[4-9]|19[0-3])\b" -- server client_web/src client_web/scripts
  ```
  Expected: the first grep prints exactly the `generate-sprites.mjs` lines 120/137/467, `sprites.generated.ts` lines 57/83, 38 lines of `sprites.missing.txt`, and `HardenedEngineIntegrationTests.cs` lines 11397/12140. The second prints nothing. Anything else: stop and classify it.

- [ ] **Step 2: Re-run the production check** (Supabase MCP, SELECT only — `mcp__supabase__execute_sql`). Use the query recorded in the appendix at the bottom of this plan. Expected: every count 0.

- [ ] **Step 3: Generate the ledger from the CURRENT `items.json`** (before deletion)

  ```bash
  node -e "
  const items=JSON.parse(require('fs').readFileSync('server/GameData/items.json','utf8'));
  const by=new Map(items.map(i=>[i.Id,i.BaseId]));
  const max=Math.max(...items.map(i=>i.Id));
  const out=['# id<TAB>BaseId, or id<TAB>- for a retired id. See ItemCatalogueIntegrityTests.',
             '# Append new ids at the end. Retiring an id: replace its BaseId with -  (never delete the line).',
             '# A - line may never become an item again.'];
  for(let i=1;i<=max;i++) out.push(i+'\t'+(by.get(i)??'-'));
  require('fs').writeFileSync('server/FolkIdle.Server.Tests/ItemIdLedger.txt', out.join('\n')+'\n');"
  ```
  Expected: 3 header lines + 441 lines; `grep -c $'\t-$' server/FolkIdle.Server.Tests/ItemIdLedger.txt` prints **111**.

- [ ] **Step 4: Write `ItemCatalogueIntegrityTests.cs`**

  Pure registry tests, no Postgres fixture (pattern: `StarterAndSkillPointTests` calls `ContentRegistry.Initialize()` in its constructor; locate the ledger by walking up from `AppContext.BaseDirectory` the way `CronWorkerGuardTests.ServerSourceRoot()` does, looking for `server/FolkIdle.Server.Tests/ItemIdLedger.txt`). Head it with a `// Modul:` comment explaining that item ids are array slots, that `d423b5b` and task 33 removed 151 items without renumbering, and that the database holds ids/slugs this file cannot see.

  Four facts:

  1. `EveryLiveItemMatchesItsLedgerLine` — for every id `1..ItemDefinitions.Length` whose `GetItemBaseId(id)` is non-empty: the ledger has a line for it (else: "id N is new — append it to ItemIdLedger.txt"), that line is not `-` (else: "id N was retired and has been REUSED as '<slug>' — pick a new id above the ledger's highest"), and the BaseId is equal (else: "id N changed from '<old>' to '<new>' — ids are array slots, a rename/shift repoints every loot row, recipe and owned database row").
  2. `EveryLedgerIdIsStillLiveUnlessRetired` — every non-`-` ledger line resolves via `GetItemBaseId` to the same slug (else: "id N ('<slug>') disappeared from items.json — if deliberate, mark it `-` in the ledger").
  3. `NoLootRowOrRecipeNamesAMissingItem` — every `ContentRegistry.AllLootEntries[i].ItemId` and every `Recipes[i].ResultItemId`, plus `Mat1Id`/`Mat2Id` where the count is > 0, satisfies `ContentRegistry.ItemExists`. Collect all failures into one message (`"loot row index 23 -> 253 (missing)"`, `"recipe 408 Mat2 -> 438 (missing)"`), then assert empty.
  4. `NoTwoItemsShareABaseId` — `_baseIdToItemDefinitionIndex` is last-wins, so a duplicate slug silently shadows an item. Assert the non-empty BaseIds are distinct (verified true today).

- [ ] **Step 5: Prove the guard bites, then revert**

  Stop the server if it is running (`Get-Process FolkIdle.Server -ErrorAction SilentlyContinue`). Then, from PowerShell:
  ```powershell
  dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~ItemCatalogueIntegrityTests"
  ```
  Expected: 4 passed.

  Now make two temporary sabotage edits (do them with Edit and undo them with Edit, not with `git checkout`, which would also throw away the `AllLootEntries` accessor):
  - In `_recipes`, change the birch axe's `Mat2Id = 438` to `Mat2Id = 182`. Id 182 is a hole today; confirm with `grep -P '^182\t-$' server/FolkIdle.Server.Tests/ItemIdLedger.txt`. Re-run: `NoLootRowOrRecipeNamesAMissingItem` should fail and name recipe 408.
  - In `items.json`, change `"Id": 194` to `"Id": 183`, which is another hole. Re-run: facts 1 and 2 should fail, one for "reused 183" and one for "194 disappeared".

  Undo both edits. Re-run: 4 passed, and `git diff --stat` shows only the accessor plus the two new files.

- [ ] **Step 6: Commit**

  ```bash
  git add server/FolkIdle.Server.Tests/ItemIdLedger.txt server/FolkIdle.Server.Tests/ItemCatalogueIntegrityTests.cs server/FolkIdle.Server/Engine/ContentRegistry.cs
  git commit -F <message file>   # write the message with the Write tool (no BOM)
  ```
  Message: `test(content): pin every item id to a ledger and fail on a dangling loot or recipe id`.

---

### Task 2: Delete the 40 definitions and fix the sprite pipeline

**Files:**
- Modify: `server/GameData/items.json`
- Modify: `server/FolkIdle.Server.Tests/ItemIdLedger.txt` (40 lines → `-`)
- Modify: `client_web/scripts/generate-sprites.mjs` (two aliases, one comment, the budget)
- Regenerate: `client_web/src/lib/ui/sprites.generated.ts`, `client_web/src/lib/ui/sprites.missing.txt`

- [ ] **Step 1: Remove the 40 entries**

  ```bash
  node -e "
  const fs=require('fs'),p='server/GameData/items.json';
  const retire=new Set([2,6,12,22,25,40,43,46,58,61,64,67,76,79,82,85,100,112,115,118,130,133,136,148,151,154,166,172,175,177,184,185,186,187,188,189,190,191,192,193]);
  const a=JSON.parse(fs.readFileSync(p,'utf8'));
  const gone=a.filter(i=>retire.has(i.Id));
  if(gone.length!==40||!gone.every(i=>i.BaseId.endsWith('_crafting_material'))) throw new Error('unexpected set: '+gone.map(i=>i.Id+':'+i.BaseId));
  fs.writeFileSync(p, JSON.stringify(a.filter(i=>!retire.has(i.Id)),null,2)+'\n');"
  git diff --stat -- server/GameData/items.json
  ```
  Expected: `1 file changed, 320 deletions(-)` (40 objects × 8 lines), **no insertions**. Any insertion means the round-trip was not byte-exact — stop.

- [ ] **Step 2: Tombstone the ledger**

  ```bash
  node -e "
  const fs=require('fs'),p='server/FolkIdle.Server.Tests/ItemIdLedger.txt';
  const retire=new Set([2,6,12,22,25,40,43,46,58,61,64,67,76,79,82,85,100,112,115,118,130,133,136,148,151,154,166,172,175,177,184,185,186,187,188,189,190,191,192,193]);
  fs.writeFileSync(p, fs.readFileSync(p,'utf8').split('\n').map(l=>{const [id]=l.split('\t'); return retire.has(+id)?id+'\t-':l;}).join('\n'));"
  grep -c $'\t-$' server/FolkIdle.Server.Tests/ItemIdLedger.txt
  ```
  Expected: **151**.

- [ ] **Step 3: Run the content validator by hand** (the hook did not fire — no Write/Edit). From PowerShell:
  ```powershell
  python ops/validate_content.py --path server/GameData
  ```
  Expected: `validate_content: all 4 content files in 'server/GameData' passed validation.`

- [ ] **Step 4: Fix `MATERIAL_ALIASES`** in `client_web/scripts/generate-sprites.mjs` (use Edit, read the surrounding lines first):
  - line 120: `'Iron bar': ['iron_bar_crafting_material', 'iron_ore'],` → `'Iron bar': 'iron_ore',`
  - line 137: `'Silver bar': ['silver_bar_crafting_material', 'silver_ore'],` → `'Silver bar': 'silver_ore',`
  - line ~467 comment: replace the example `("Iron bar" -> iron_bar_crafting_material)` with a surviving one, e.g. `("Copper" -> copper_ore_crafting_material)`.
  - Add one line to the `// Modul: THE INGOT ART IS THE ORE, DELIBERATELY.` block: the `*_bar_crafting_material` items these aliases also used to cover were deleted in task 33 (2026-09-23), so the ingot art now belongs to the ore alone.

- [ ] **Step 5: Regenerate sprites, then lower the budget**

  ```powershell
  cd client_web; npm run generate:sprites
  ```
  Expected output includes `127 of 290 items have no artwork (budget 165)`. Then set `const MISSING_ART_BUDGET = 127;` and run:
  ```powershell
  npm run check:sprites
  ```
  Expected: `sprite check ok: ... 127 without (budget 127)`. `git diff --stat` on the two generated files: `sprites.generated.ts` loses exactly the two bar keys; `sprites.missing.txt` loses 38 lines, header now `total 127 of 290`, section `## crafting materials (8)`.

- [ ] **Step 6: Guard tests**
  ```powershell
  dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~ItemCatalogueIntegrityTests"
  ```
  Expected: 4 passed. (If fact 2 fails naming one of the 40, step 2 missed it.)

- [ ] **Step 7: Local dev database** (not a correctness issue for production, but it is the account `exercise` drives). With the local stack's Postgres up:
  ```sql
  SELECT 'commodity', "ItemId" FROM "CommodityRecords" WHERE "ItemId" LIKE '%\_bar\_crafting\_material' OR "ItemId" IN (<the 30 non-bar slugs>)
  UNION ALL SELECT 'stash', "ItemId" FROM "VillageStashInstances" WHERE <same>;
  ```
  The fixture used to stock every recipe material into the stash, and when smelting existed that included bars. If rows exist, delete them in the local DB only (they are now unresolvable items). Do not write a migration for them — production has none.

- [ ] **Step 8: Commit**
  Message: `feat(content): delete the 40 unreachable legacy crafting materials (task 33)` — body: ids retired, no renumbering, prod holds none (the counts), aliases trimmed, art budget 165 → 127.

---

### Task 3: The two stale texts

**Files:**
- Modify: `client_web/src/routes/Wiki.svelte:594`
- Modify: `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs:11397`

- [ ] **Step 1: Wiki.** Change "fifty ids ending `_crafting_material`" to **ten** and drop "They are kept rather than deleted because they are yours" (forty of them were deleted, after confirming nobody held one). Keep it plain for players; something like: "Legacy crafting materials — ten ids ending `_crafting_material`, all ores. Anything held under one of these names is still yours." Before you write that, check whether the live ten really *can* be obtained and spent today (they are in the mining rows, node 201-205, and 129 is a recipe input), because the current sentence says they cannot. Say only what the code shows.
- [ ] **Step 2:** Delete the stale `// copper_bar_crafting_material: 3x mat 93 + 1x mat 129, Smelting.` line above `const int resultItemId = 408;`. The next line already says what 408 is.
- [ ] **Step 3:** `cd client_web; npm run check:ratchet` — expected: the 4-error `GuildOps.svelte` baseline, no growth.
- [ ] **Step 4: Commit** — `docs(wiki): ten legacy crafting materials, not fifty`.

---

### Task 4: Full verification

The verify skill sets the order: the suite first, then the game.

- [ ] **Step 1: Server suite** (Docker running, server stopped):
  ```powershell
  dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
  dotnet test  server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  ```
  Expected: green. The suites most likely to notice are `HardenedEngineIntegrationTests` (`Test_ContentRegistry_EveryRecipeIngredientIsObtainableFromSomeSource`, `Test_Content_NewRegionalMonstersAndLootTablesResolve`), `DevFixtureInvariantTests`, `GatheringEconomyTests`, `GatheringShareTests`, `EquipmentDropTableTests`, `ArmourSetTests`. None references the 40 (checked), so a failure here is new information. Read it before you "fix" it. Don't compare against a test count written in a doc; the counts in the docs disagree.
- [ ] **Step 2: Server's own content parse**:
  ```powershell
  dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj -- --validate-content
  ```
  Expected: success (the authoritative C# parse path the Python validator mirrors).
- [ ] **Step 3: Client**:
  ```powershell
  cd client_web; npm test; npm run check:sprites; npm run build
  ```
  Expected: vitest green (`serverMirrors.test.ts`, `sprites.test.ts`, `itemNames.test.ts`), sprite check ok at 127/127, build completes.
- [ ] **Step 4: Play the game**: `.\run-dev.ps1`, re-seed if the breeding/village steps report a spent pool (`--seed-dev` is idempotent), then:
  ```powershell
  cd client_web; npm run exercise
  ```
  Expected: all checks pass. Also open by hand: Chest (materials tab), Crafting (recipe tree renders all recipes, the Birch Axe shows its costs), Wiki → Item database (no bars, the ten ores still listed with names), Market item browser. A screen that throws on an unknown item would show here.

---

### Task 5: Docs, PR, deploy

**Files:** `docs/crafting_material_audit.md`, `docs/TASK_BOARD.md`, `docs/architecture/NEXT_STEPS_BACKLOG.md`

- [ ] **Step 1: `crafting_material_audit.md`.** Add a dated top section "Resolved 2026-09-23 (task 33)": the 40 were deleted and their ids retired in `ItemIdLedger.txt`, with the production counts from Findings §4. Correct the "Do not delete them yet" recommendation to past tense and fix its internal contradiction: it says "renumbers nothing (ids are explicit)" and "ids are positional" in one sentence. Both are true, and the doc should say how: explicit ids that act as array slots. Keep the Live (10) table.
- [ ] **Step 2: `TASK_BOARD.md` §33.** Mark done. Link this plan and the PR, and record the three facts a reader will want: ids do not shift, production held nothing, and the sprite aliases were the only live dependency. Update the summary table row at line ~3350.
- [ ] **Step 3: `NEXT_STEPS_BACKLOG.md`.** In the top-of-file status, remove "delete the unused legacy crafting materials" from the open list (line ~34). Extend the standing trap at ~2973 ("The item catalogue has HOLES") with: 151 holes now, and `ItemCatalogueIntegrityTests` + `ItemIdLedger.txt` enforce no shift and no reuse.
- [ ] **Step 4: Commit** (`docs: task 33 done`). Push the branch and open the PR, ending the description with the attribution line.
- [ ] **Step 5: Deploy (after the owner's go-ahead)** with the `deploy` skill. It is an SSH push; `git pull` on the box does not work. First re-run the appendix query against production and expect all zeros. No migration ships with this change. After the deploy: `/gamedata/items.json` serves 290 entries, `npm run smoke:screens` with `FOLKIDLE_E2E_BASE=https://folkidle.duckdns.org` is green (the only check that is safe against production), and the Wiki item database loads on the live site.

---

## Commit boundaries

1. `test(content): pin every item id ...` — ledger (111 holes) + integrity tests + `AllLootEntries` accessor. Green on its own.
2. `feat(content): delete the 40 ...` — `items.json`, ledger tombstones, `generate-sprites.mjs`, both generated sprite files. **These must be one commit**: `items.json` without the alias edit breaks the sprite generator, and the ledger without `items.json` fails the integrity test.
3. `docs(wiki): ...` — Wiki line + stale test comment.
4. `docs: task 33 done` — audit, task board, backlog.

Reverting commit 2 alone restores the 40 cleanly, and the ledger check stays consistent because the tombstones are reverted in the same commit.

## Not in this plan (found on the way; for the owner, not drive-bys)

- **The Kobold packed-weight check reads the wrong namespace.** `SimulationEngine.cs:4764` calls `ContentRegistry.GetMaterialString(lootTable[i].ItemId)` on an items.json id. `GetMaterialString` is the six-slug gathering space (`1 => "copper_ore"` ... `_ => "unknown"`), so for almost every drop it returns `"unknown"`, the `_ore_`/`_bar_` test fails, and Kobold pays weight 2 on ores too. It is the material-namespace trap from CLAUDE.md. After this task the `_bar_` half can match nothing at all. The fix is `GetItemBaseId`, and it changes gameplay, so it is out of scope here.
- **`TryGetItemDefinitionByBaseId("")` returns true.** `Initialize` puts every hole's empty BaseId into `_baseIdToItemDefinitionIndex`, so `""` resolves to the last hole with a default `ItemDefinition` (Id 0). This predates the task (111 holes already exist), and adding 40 more does not change what it does. A one-line `if (newItemBaseIds[i].Length == 0) continue;` would fix it, but callers that pass an empty string would then behave differently, so it needs its own look.
- **Mining nodes 201-205 and the ten live `*_crafting_material` ores.** Production holds zero rows of any `*_crafting_material`, which suggests those rows are not reachable in practice either. This was not investigated. The Wiki step (Task 3) only asks you to avoid claiming anything the code does not show.
- **`client/`** still carries the 40 in its stale Unity content copy. Task 34 deletes it.

## Appendix: the production check (read-only)

```sql
with ids(id) as (select unnest(array[2,6,12,22,25,40,43,46,58,61,64,67,76,79,82,85,100,112,115,118,130,133,136,148,151,154,166,172,175,177,184,185,186,187,188,189,190,191,192,193])),
slugs(s) as (select unnest(array['highland_wool','ominous_feather','selkie_skin_fragment','shadow_feather','tainted_tusk','scorpion_stinger','crystallized_venom','harpy_talon','frosted_down','glacial_claw','runestone_shard','ancient_burial_cloth','pure_aurora_filament','sentinel_alloy_plate','prismatic_core_prism','ethereal_shroud_fabric','waterlogged_cloth','bat_wing','carapace_shard','chitin_segment','eagle_feather','thick_goat_horn','frozen_scale','locust_wing','falcon_talon','desiccated_bone','rat_pelt','sharp_claw','wolf_tooth','thick_wolf_hide_rare','copper_bar','bronze_bar','iron_bar','steel_bar','silver_bar','gold_bar','mithril_bar','adamantite_bar','obsidian_bar','celestial_bar']) || '_crafting_material'),
strs(v) as (select s from slugs union select id::text from ids)
select 'CommodityRecords' t, count(*) from "CommodityRecords" where "ItemId" in (select v from strs)
union all select 'VillageStashInstances', count(*) from "VillageStashInstances" where "ItemId" in (select v from strs)
union all select 'EquipmentInstances', count(*) from "EquipmentInstances" where "BaseItemId" in (select v from strs)
union all select 'MarketEquipmentInstances', count(*) from "MarketEquipmentInstances" where "BaseItemId" in (select v from strs)
union all select 'MarketOrderRecords.BaseItemId', count(*) from "MarketOrderRecords" where "BaseItemId" in (select v from strs)
union all select 'MarketOrderRecords.CommodityId', count(*) from "MarketOrderRecords" where "CommodityId" in (select id from ids)
union all select 'historical_market_archives.BaseItemId', count(*) from historical_market_archives where "BaseItemId" in (select v from strs)
union all select 'historical_market_archives.CommodityId', count(*) from historical_market_archives where "CommodityId" in (select id from ids)
union all select 'MailboxInstances', count(*) from "MailboxInstances" where "BaseItemId" in (select v from strs)
union all select 'GuildDepotBalances', count(*) from "GuildDepotBalances" where "ItemDefinitionId" in (select id from ids)
union all select 'GuildContributionLedgers', count(*) from "GuildContributionLedgers" where "MaterialId" in (select id from ids)
union all select 'GuildLogisticsDepots', count(*) from "GuildLogisticsDepots" where "MaterialId" in (select id from ids)
union all select 'GuildMaterialSinkLedgers', count(*) from "GuildMaterialSinkLedgers" where "CommodityId" in (select v from strs)
union all select 'PlayerCraftingSlots', count(*) from "PlayerCraftingSlots" where "ActiveRecipeId" in (select id from ids)
union all select 'PlayerRecords.Larder', count(*) from "PlayerRecords" where "LarderSlot1ItemId" in (select id from ids) or "LarderSlot2ItemId" in (select id from ids) or "LarderSlot3ItemId" in (select id from ids)
union all select 'pending_grants', count(*) from pending_grants where exists (select 1 from slugs where "PayloadJson" like '%'||s||'%');
```
