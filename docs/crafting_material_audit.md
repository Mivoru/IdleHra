# The `*_crafting_material` namespace: what is live and what is not

## Resolved 2026-09-24, part 2: the ten ores are gone too

The owner approved deleting the ten ores in the "Live (10)" table below, which
turned out to be unreachable (see the correction further down). They are
deleted on branch `chore/delete-legacy-ores`: ids 1, 21, 39, 57, 75, 93, 111,
129, 147 and 165, with ledger tombstones (161 retired ids now). **The whole
`*_crafting_material` namespace is now empty.** Production was checked again
the same day, with the same 16 read-only checks: 0 rows each, and 0
`CommodityRecords` rows ending `_crafting_material` at all.

Their only loot rows (`_lootEntries` 21 and 61-76) **stay in the array as
dead padding**. `_lootSegments` slices the array by position, so removing
those rows would re-slice every table after them.
`ItemCatalogueIntegrityTests` now tells reachable rows from dead ones. A
reachable row must name a live item. A dead row may name a live or a retired
id. The set of dead rows is pinned, currently 0-21 and 47-76. The `Copper`
and `Absidian` sprite aliases now point at the ore alone, and the missing-art
budget went from 127 to 119.

## Resolved 2026-09-24 (task 33)

**The 40 legacy entries were deleted** from `items.json` and their ids retired
in `server/FolkIdle.Server.Tests/ItemIdLedger.txt`, where
`ItemCatalogueIntegrityTests` fails if any of them is ever reused, if a live id
shifts, or if a loot row or recipe names a hole. Nothing was renumbered: item
ids are **explicit** (every entry carries `"Id"`) and they **act as array
slots** (`ContentRegistry` stores each at `Id - 1` in arrays sized by the
highest id), so a removed entry leaves a hole and every other id means what it
meant before. That is how both halves of the old sentence below are true at
once. 151 ids are retired now (111 from `d423b5b`, 40 here).

Production was checked first (Supabase, read-only SELECTs, 2026-09-24): **0
rows** naming any of the 40, by slug or numeric id, in `CommodityRecords`,
`VillageStashInstances`, `EquipmentInstances`, `MarketEquipmentInstances`,
`MarketOrderRecords` (`BaseItemId`, `CommodityId`), `historical_market_archives`
(both), `MailboxInstances`, `GuildDepotBalances`, `GuildContributionLedgers`,
`GuildLogisticsDepots`, `GuildMaterialSinkLedgers`, `PlayerCraftingSlots`, the
three `PlayerRecords` larder slots and `pending_grants`. No migration, no
compensation. The only live dependency was the sprite generator's
`MATERIAL_ALIASES` (`Iron bar`, `Silver bar`), now pointing at the ore alone.

**Correction to the "Live (10)" table below.** It says those ten are reached by
"loot table". The rows exist (`_lootEntries` indices 21 and 61-76) but **no
table points at them any more**: they belonged to mining nodes 201-205, which
were renumbered to 2001-2005, and those use indices 87-96 (the regional
`*_ore` family). No recipe consumes id 129 either. So the ten are defined but,
as of 2026-09-24, obtainable from nothing and spent by nothing - the same state
the 40 were in. They were left alone because task 33 was scoped to the 40;
whether to delete them too is a separate decision (production holds 0 rows
ending `_crafting_material` at all).

Written 2026-09-02, closing out the last open point of Task Board item 7 ("decide
what to do about the ~70 `*_crafting_material` entries").

The count in the task board was wrong: there are **50** of them in `items.json`,
not ~70. Of those, **10 are live and 40 are unreachable**.

## How this was measured

Recipes and loot tables reference materials by **numeric `ItemId`, never by
`BaseId` string**. A grep for the slug therefore finds nothing and proves
nothing — that is the trap, and it is the same shape as the two-material-namespace
trap already recorded in `CLAUDE.md`.

An id counts as live here if it appears as a `RecipeDefinition.Mat1Id`/`Mat2Id`
(a recipe input), a `ResultItemId` (a recipe output), a `LootTableEntry.ItemId`
(any drop table), a `VillageManagementEngine` tier material, or in
`DevFixtureSeeder`. All of those live in
`server/FolkIdle.Server/Engine/ContentRegistry.cs` except the last two.

## Live (10) — do not touch

| Id | BaseId | Reached by |
|---|---|---|
| 1 | `gold_ore_crafting_material` | loot table |
| 21 | `mithril_ore_crafting_material` | loot table |
| 39 | `adamantite_ore_crafting_material` | loot table |
| 57 | `obsidian_ore_crafting_material` | loot table |
| 75 | `celestial_ore_crafting_material` | loot table |
| 93 | `tin_ore_crafting_material` | loot table |
| 111 | `iron_ore_crafting_material` | loot table, dev fixture |
| 129 | `coal_node_crafting_material` | loot table, dev fixture, **recipe input** |
| 147 | `silver_ore_crafting_material` | loot table |
| 165 | `copper_ore_crafting_material` | loot table, **village tier material** |

Every one of these is an ore. `coal_node` is the only crafting material any
recipe actually consumes.

## Legacy (40) — defined, and reachable by nothing

Two coherent groups, which is what makes this a design decision rather than a
cleanup.

**The ten bars (ids 184-193)** — `copper_bar`, `bronze_bar`, `iron_bar`,
`steel_bar`, `silver_bar`, `gold_bar`, `mithril_bar`, `adamantite_bar`,
`obsidian_bar`, `celestial_bar`.

These are the remains of a **smelting tier that was never built**: ore → bar →
equipment. No recipe produces a bar and no recipe consumes one. This is settled
design, not an oversight — the game deliberately has no smelting step, which is
also why the ore icons are drawn as ingots (see `MATERIAL_ALIASES` in
`generate-sprites.mjs`). The bars are the dead half of that decision.

**The thirty monster-drop materials** — `wolf_tooth`, `bat_wing`, `harpy_talon`,
`chitin_segment`, `runestone_shard`, `ancient_burial_cloth` and the rest. These
predate the per-monster drop tables and were superseded by them; nothing grants
them any more.

Full list of the 40, by id:

```
  2 highland_wool          64 runestone_shard          148 locust_wing
  6 ominous_feather        67 ancient_burial_cloth     151 falcon_talon
 12 selkie_skin_fragment   76 pure_aurora_filament     154 desiccated_bone
 22 shadow_feather         79 sentinel_alloy_plate     166 rat_pelt
 25 tainted_tusk           82 prismatic_core_prism     172 sharp_claw
 40 scorpion_stinger       85 ethereal_shroud_fabric   175 wolf_tooth
 43 crystallized_venom    100 waterlogged_cloth        177 thick_wolf_hide_rare
 46 harpy_talon           112 bat_wing                 184-193 the ten bars
 58 frosted_down          115 carapace_shard
 61 glacial_claw          118 chitin_segment
                         130 eagle_feather
                         133 thick_goat_horn
                         136 frozen_scale
```

## Recommendation

**Do not commission art for any of the 40.** That was the question Task 7 asked,
and the answer is that 40 of the 50 would be art for items no player can ever
hold.

*(Historical - the deletion below was done in task 33; see the top section.)*
**Do not delete them yet either.** Deleting an `items.json` entry renumbers
nothing (ids are explicit, and each id is the array slot it occupies, so a
removal leaves a hole rather than shifting anything) but it is not free: live `ItemInstanceRecords` rows could still
reference a legacy id from before the drop tables changed, and the stranded-ore
migration (`20260901194818_FoldStrandedCraftingMaterialOres`) is the precedent
for how much care that takes.

The honest sequence is: query production for any `ItemInstanceRecords` /
`CommodityRecords` row holding one of the 40, fold or compensate whatever is
found the way the stranded ores were, and only then remove the definitions. Until
that happens they are harmless — they cost one line each in `items.json` and 40
lines in `sprites.missing.txt`, which is exactly where a reader should find them.

**Modul:** the reason this file exists rather than a decision is that the
previous four defects in this area all came from someone acting on a
material-namespace assumption without checking production first. The list is the
deliverable; the deletion is a separate, evidenced pass.
