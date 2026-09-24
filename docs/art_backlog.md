# Art backlog: items that still render as two initials

Written 2026-09-24 for task 35c (`docs/TASK_BOARD.md` §35). The source list
is `client_web/src/lib/ui/sprites.missing.txt`, which `generate-sprites.mjs`
regenerates. After the task-33 deletions (#26) and the ten-ore follow-up
(#28), it held **119 of 280 items**. After the tier-D deletion below, it
holds **37 of 198**: the 24 in tiers A-C plus the 13 kept from tier D. The number is a ratchet
(`MISSING_ART_BUDGET`). Lower it only when art actually lands, not because
of this list.

## How this was ranked

Score = how many players see the item × how often.

1. **Production holdings.** One read-only Supabase query, 2026-09-24. It counts
   distinct holders per `CommodityRecords.ItemId`, excluding the `exercise%`
   throwaway accounts. Very few players are real, so this under-ranks
   everything past region 2. It breaks ties but does not lead.
2. **Reachability in code.** For each item, what can actually put it in a
   player's hands today:
   - a loot row that some `_lootSegments` table reaches (monster drops
     501-525, gathering nodes 1001-3005);
   - a recipe (all 30 are tools, profession 3);
   - a literal reference anywhere in `server/FolkIdle.Server`.

   This was scripted, not read by eye.
3. **Everywhere an item shows up.** Every catalogued item appears in the
   Wiki's item database and the Market item browser whether or not it can
   be obtained. An unobtainable item is still seen there, but only by
   someone browsing.

Tiers:

- **A.** Every new player sees it in the first hour: region-1 drops.
- **B.** Mid-game: regions 2-3.
- **C.** Late or rare: regions 4-5, and boss-only drops.
- **D.** Nothing in the game can grant it. Do not draw it. Delete it or
  wire it first (see the end of this file).

Filenames go under `client/Assets/Images/SpritesWeb/`, named **exactly after
the BaseId** (an exact match wins outright, see `generate-sprites.mjs`). Keep
the existing style: a transparent background, one object, readable at 32 px.

## A: draw first (4)

| BaseId | id | Shown as | Source | Region | Holders | Brief |
|---|---|---|---|---|---|---|
| `mat_mouse_fur` | 250 | Mat Mouse Fur | Field Mouse drop (weight 25/25) | 1 | **2** | A small tuft of grey-brown fur. It comes from **the first monster every player fights**, so it is the most-seen missing icon in the game. |
| `mat_rabbit_foot` | 253 | Mat Rabbit Foot | Horned Rabbit drop | 1 | 1 | A lucky rabbit's foot, pale fur, tied with a string. |
| `mat_boar_tusk` | 259 | Mat Boar Tusk | Wild Boar drop | 1 | 1 | A single curved ivory tusk, chipped at the root. |
| `mat_wolf_essence` | 262 | Mat Wolf Essence | Alpha Wolf (region-1 boss, 100%) | 1 | **2** | A swirl of pale blue spirit-mist in the shape of a wolf's head, in a small glass vial. |

## B: mid-game (10)

| BaseId | id | Shown as | Source | Region | Holders | Brief |
|---|---|---|---|---|---|---|
| `mat_sharp_thorn` | 274 | Mat Sharp Thorn | Thorny Vine | 2 | 1 | A long dark-green thorn with a red tip. |
| `mat_magic_bark` | 280 | Mat Magic Bark | Forest Dryad | 2 | 1 | A curl of bark with faint glowing green runes. |
| `mat_wolf_hide` | 277 | Mat Wolf Hide | Gray Direwolf | 2 | 1 | A folded grey pelt. |
| `mat_bear_claw` | 283 | Mat Bear Claw | Mountain Bear | 2 | 1 | One heavy brown claw. |
| `mat_lynx_eye` | 286 | Mat Lynx Eye | Shadow Lynx (region-2 boss, 100%) | 2 | 1 | A glowing amber cat's eye set in a dark stone. |
| `mat_chitin_shell` | 298 | Mat Chitin Shell | Desert Crab | 3 | 1 | A sand-coloured crab-shell plate. |
| `mat_basilisk_scale` | 301 | Mat Basilisk Scale | Ashen Basilisk | 3 | 1 | An ash-grey scale with an ember-orange edge. |
| `mat_flame_core` | 304 | Mat Flame Core | Ember Elemental | 3 | 1 | A fist-sized orb of contained fire. |
| `mat_lodestone` | 307 | Mat Lodestone | Sandstone Golem | 3 | 1 | A dark magnetic stone with iron filings clinging to it. |
| `mat_lava_heart` | 310 | Mat Lava Heart | Magma Wyrm (region-3 boss, 100%) | 3 | 0 | A cracked black stone with molten light inside. |

## C: late and boss drops (10)

| BaseId | id | Shown as | Source | Region | Holders | Brief |
|---|---|---|---|---|---|---|
| `mat_frozen_wing` | 323 | Mat Frozen Wing | Ice Bat | 4 | 1 | A frost-rimed leathery bat wing. |
| `mat_yeti_pelt` | 326 | Mat Yeti Pelt | Snowy Yeti | 4 | 1 | A shaggy white pelt. |
| `mat_spectral_ice` | 329 | Mat Spectral Ice | Glacial Wraith | 4 | 1 | A translucent ice shard with a ghostly face inside. |
| `mat_rime_crystal` | 332 | Mat Rime Crystal | Rock Giant | 4 | 1 | A cluster of pale-blue crystals on a grey rock. |
| `mat_eternal_ice` | 335 | Mat Eternal Ice | Frost Titan (region-4 boss, 100%) | 4 | 1 | A perfect deep-blue ice gem that never melts, with a faint aura. |
| `mat_plague_flesh` | 347 | Mat Plague Flesh | Grave Ghoul | 5 | 1 | A sickly green-grey lump. Make it grim, not gory. |
| `mat_gargoyle_stone` | 350 | Mat Gargoyle Stone | Fortress Gargoyle | 5 | 1 | A carved stone fragment with part of a snarling face. |
| `mat_necrotic_core` | 353 | Mat Necrotic Core | Dark Necromancer | 5 | 1 | A black orb with a purple glow and a bone cage. |
| `mat_broken_blade` | 356 | Mat Broken Blade | Death Knight | 5 | 1 | A snapped dark-steel sword tip. |
| `mat_demon_heart` | 359 | Mat Demon Heart | Malakor (the final boss, 100%) | 5 | 0 | A crimson crystalline heart, still beating with fire. The last drop in the game. |

**24 items in A-C.** These are all the drops of the 25 canonical monsters
except `mat_viper_venom`, which already has art through an alias. Art for
these 24 would clear every icon a player earns by fighting.

## D: do not draw (95). Nothing in the game grants these (82 deleted, see below)

Each group was checked the same way. No reachable loot row, no recipe, and no
literal reference in server code grants any of them.

| Group | Count | Why nothing grants it |
|---|---|---|
| Pre-canon gathering and profession materials (`*_raw_fishing_material`, `*_woodcutting_material`, `*_herbalism_material`, `*_alchemy_material`, `*_unique_regional_boss_material`, `lightning_bolt_fragment_ultimate_mythic_upgrade_material`) | 46 | Their only loot rows are the dead padding rows 0-20 and 47-60, which no `_lootSegments` table reaches since the nodes were renumbered to 1001-3005. Gathering pays the canonical `*_log`, `*_ore` and fish items instead, and those already have art. |
| `alc_off_t01`...`t10`, `alc_def_t01`...`t10` | 20 | No recipe (all 30 are tools), no drop and no code reference. |
| `cooked_*_tN_food` (ids 194-203) | 10 | `FoodRegistry` would accept them in the larder, but the cooking recipes that produced them are gone. The larder is fed raw fish, which has art. |
| `*_rare_alchemy_ingredient`, `*_alchemy_ingredient`, `blasting_powder_rare_crafting_ingredient`, `field_marigold_herbalism/cooking_ingredient`, `pond_minnow_raw_cooking_ingredient` | 12 | No source. |
| `searing_tonic_`, `obsidian_skin_`, `doom_herald_` `*_potion_consumable` (376-378) | 3 | `ConsumableEngine` can apply them, but nothing grants one. |
| `obsidian_chunk` | 1 | No source. |
| `premium_diamond`, `premium_diamond_cluster`, `premium_diamond_cluster_guaranteed_currency_payout` | 3 | Diamonds are a currency (`PlayerRecords` diamonds, with their own icon via `currencyIcon('diamond')`), not a commodity row. Only a comment in `AffixRerollEngine` names `premium_diamond`. This is plumbing. |

**Resolved 2026-09-24 (branch `chore/delete-dead-tier-d-items`).** The owner
approved deleting tier D the same way as task 33. **82 of the 95 are
deleted.** A production SELECT found that nobody held any of them. Their ids
are retired in `ItemIdLedger.txt`, and the art budget went from 119 to 37.

**13 were kept deliberately.** Nothing grants them, but code addresses them
by number, so deleting them would be a code rework, not a content removal:

- **The ten cooked foods (194-203).** `FoodRegistry` indexes its heal table
  by exactly this id block. `LarderEngine` and `AlchemyCompendium` accept
  them, `BossGearBenchmark` uses the first one, and the dev fixture's larder
  is 196-198.
- **The three `*_potion_consumable` items (376-378).** They are the only
  content `ConsumableEngine`'s potion slots have, and `searing_tonic` is the
  fixture of the potion-lifecycle test.

`ItemCatalogueIntegrityTests.EveryIdTheCodeAddressesByNumberIsLive` pins
them. For these, the owner has to choose between two things: give them a
source (a cooking or alchemy profession) and then draw them, or rework
FoodRegistry and ConsumableEngine and then delete them.

## Method, for a rerun

The holdings query:

```sql
select c."ItemId", count(distinct c."PlayerId") as holders, sum(c."Quantity") as total
from "CommodityRecords" c join "PlayerRecords" p on p."Id" = c."PlayerId"
where coalesce(p."Username",'') not ilike 'exercise%'
group by c."ItemId" order by holders desc, total desc;
```

For reachability, parse the rows of `_lootEntries` and the `_lootSegments`
slices in `ContentRegistry.cs`. Map each table id to `monsters.json`
(`LootTableId`) or to `gathering_nodes.json` (`ActivityId`). Add the
`RecipeDefinition` inputs and outputs, and grep `server/FolkIdle.Server`
(excluding `Migrations/`) for each BaseId as a string literal.
