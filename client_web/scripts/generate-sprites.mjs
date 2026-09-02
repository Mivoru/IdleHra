// Modul: builds the sprite lookup tables from what is actually on disk.
//
// Same principle as generate-protocol.mjs: the client must not carry a
// hand-written list of what art exists, because that list rots the moment
// someone adds a PNG. This walks the real sprite tree and the real items.json
// and emits the mapping, failing loudly on an alias that no longer resolves.
//
// WHY AN ALIAS TABLE AND NOT A NORMALIZER. The art was named for humans
// ("Golden Willow log") and the content file for code
// ("whispering_willow_log"). A fuzzy normalizer would either miss real matches
// or - far worse - confidently pick the wrong item and put the wrong picture
// on it, which is a lie the player has no way to detect. So anything that does
// not match exactly is either an explicitly verified alias or it gets no icon.
//
// The alias table below is ported from the Unity AssetRegistryBuilder, whose
// entries were hand-verified against items.json, and extended to cover regions
// 03-05 and Tools&Equipment (art that landed after that file was written).
// Every target was checked to exist before being written here.

import { readdirSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import { join, dirname, basename, extname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..', '..');
// Reads the CLEANED artwork, not the masters.
//
// Two reasons it must be this directory and not client/Assets/Images/Sprites:
// the extensions differ (.webp, not .png), and a master that the cleaner has
// not processed yet would be mapped to a URL the server cannot serve - a 404
// that looks like a missing icon rather than a stale build step.
const spriteRoot = join(repoRoot, 'client', 'Assets', 'Images', 'SpritesWeb');
// Modul: THE SERVER'S CONTENT, not the retired Unity copy.
//
// This used to read client/Assets/StreamingAssets/GameData, which is a Unity
// leftover and is now 111 items STALE - it still carries the whole legacy
// equipment line and the five `_helper_offhand_base` pieces that the catalogue
// cut removed, and it lacks copper/iron/obsidian/silver_ore entirely. The
// client gets its catalogue from the server at runtime, so anything measured
// against the Unity copy is measured against a catalogue no player ever sees.
// Switching the source changed no mapping (verified by diffing the generated
// file); what it changes is that the coverage report below is now honest.
const gameData = join(repoRoot, 'server', 'GameData');
const outFile = join(here, '..', 'src', 'lib', 'ui', 'sprites.generated.ts');
const missingFile = join(here, '..', 'src', 'lib', 'ui', 'sprites.missing.txt');

/** `--check` regenerates into memory and diffs, the way generate-protocol does. */
const checkOnly = process.argv.includes('--check');

/** How many catalogued items are allowed to have no artwork.
 *
 * Modul: THIS NUMBER MAY ONLY GO DOWN. It is not a target, it is a ratchet -
 * the point is that adding an item without art, or breaking an alias so a
 * mapping silently stops resolving, fails CI instead of quietly making the
 * game uglier. Lower it whenever art lands; never raise it to make a build
 * pass. The per-item breakdown lives in the committed sprites.missing.txt, so
 * a change to this number always comes with a reviewable list of which items
 * moved. */
const MISSING_ART_BUDGET = 165;

// ---------------------------------------------------------------------------
// Alias tables
// ---------------------------------------------------------------------------

/** Character sprite race name -> ContentRegistry.RaceIds.
 *
 * Modul: Bes and Leshy were SWAPPED. The pairing was inferred from folklore -
 * Bes as a house-spirit like a kobold, Leshy as a forest spirit like the
 * German "Moosleute" - and the design list settles it instead: the six races
 * are human, vila, draugr, LESHY, vodnik, BES, in that order. So 4 is Leshy
 * and 6 is Bes, and every roster was showing the other one's picture.
 *
 * The server still calls those two slots Kobold and Moosleute internally.
 * Renaming a C# identifier used in forty places is a separate change from
 * fixing what players see, and only the second one is a bug. */
const RACE_NAME_TO_ID = {
  Human: 1,
  Vila: 2,
  Draugr: 3,
  Leshy: 4,
  Vodnik: 5,
  Bes: 6,
};

/** Sprite basename (exactly as on disk) -> item BaseId, or several BaseIds.
 *
 * Modul: A LIST IS FOR A DUPLICATED ITEM, NEVER FOR TWO DIFFERENT THINGS.
 *
 * Four logs and two ores exist TWICE in items.json under two naming schemes:
 * the rare region-2 log is both `whispering_willow_log` (id 293) and
 * `golden_willow_log` (id 401), same RegionTier, same BaseValueGold. Only the
 * second is live - VillageManagementEngine.TierMaterials, the woodcutting loot
 * tables and GuildContributionEngine all name the `golden_*` ids, and the
 * older family appears in no C# file at all - but players can still be holding
 * the older one in an inventory row written before the rename. So the art goes
 * on both ids. Anything else would be two items sharing one picture, which is
 * the lie this whole file exists to avoid. */
const MATERIAL_ALIASES = {
  // Modul: THE INGOT ART IS THE ORE, DELIBERATELY.
  //
  // `Copper.webp`, `Iron bar.webp`, `Cobalt bar.webp`, `Silver bar.webp` and
  // `Darksteel bar.webp` are drawn as ingots and are the intended icons for the
  // ORE commodities. There is no smelting step in this game - you never forge
  // an ore into a bar - so an ingot is simply how a refined metal is pictured,
  // and it reads better at inventory size than a lump of rock would. Do not
  // "fix" this by drawing ore nuggets: that was tried, and it invents a
  // distinction the mechanics do not have.
  //
  // Region 01 - from the Unity builder
  'Birch Tree': 'birch_trees_woodcutting_material',
  Copper: ['copper_ore_crafting_material', 'copper_ore'],
  Malachite: 'malachite_ore',
  'Viper Venom Elixir': 'mat_viper_venom',

  // Region 02
  'Golden Willow log': ['golden_willow_log', 'whispering_willow_log'],
  'Golden Willow twig': 'whispering_willow_twig',
  'Willow tree': 'willow_logs_woodcutting_material',
  Hematite: 'hematite_ore',
  'Iron bar': ['iron_bar_crafting_material', 'iron_ore'],

  // Region 03 - "Acatia" is the art's spelling of acacia, and the "Golden"
  // variant of each tree is the upgraded species, matching the log/twig pairs
  // in items.json (birch->golden_birch, willow->whispering_willow,
  // acacia->ironwood, frostpine->glacier_pine, ebon->void_bark).
  'Acatia log': 'acacia_log',
  'Acatia twig': 'acacia_twig',
  'Golden Acatia log': ['golden_acacia_log', 'ironwood_log'],
  'Golden Acatia twig': 'ironwood_twig',
  Absidian: ['obsidian_ore', 'obsidian_ore_crafting_material'],
  'volcanic sulfur': 'sulfur_ore',
  'Bear stew': 'bear_stew_food_consumable',

  // Region 04
  'Golden Frostpine log': ['golden_frostpine_log', 'glacier_pine_log'],
  'Golden Frostpine twig': 'glacier_pine_twig',
  'Silver bar': ['silver_bar_crafting_material', 'silver_ore'],
  'Cobalt bar': 'cobalt_ore',
  'yeti meat platter': 'yeti_platter_food_consumable',

  // Region 05
  'Golden Ebon log': ['golden_ebon_log', 'void_bark_log'],
  'Golden Ebon twig': 'void_bark_twig',
  'Astralite crystals': 'astralite_ore',
  'Darksteel bar': 'darksteel_ore',

  // Weapons whose art name differs from the item noun. Each target was
  // confirmed to be the ONLY weapon of its kind for that set/material, so
  // these are identifications rather than guesses.
  'brawler battleaxe': 'eq_brawler_axe_melee_weapon_slot_base',
  'broadsword claymore': 'eq_steel_claymore_melee_weapon_slot_base',
  'frost staff': 'eq_frost_crystal_staff_magic_weapon_slot_base',
  'ashwood shortbow': 'eq_ash_shortbow_range_weapon_slot_base',
  'recurve bow': 'eq_composite_recurve_range_weapon_slot_base',
  'Void Shadow longbow': 'eq_void_shadow_bow_range_weapon_slot_base',

  // Set pieces the (set, slot) rule cannot resolve because the set token in
  // the art is not the set token in items.json.
  obsidian_plate: 'eq_obsidian_plate_chest_armor_slot_base',
  magus_slippers: 'eq_magus_slippers_boots_armor_slot_base',

  // The Eternal Dreadnought set is `eq_dreadnought_*` for its helmet and
  // `eq_dread_*` for everything else, so the (set, slot) rule cannot reach it
  // from an art file called dread_helm.
  dread_helm: 'eq_dreadnought_helm_helmet_armor_slot_base',

  // Named by the art brief, which lists this file as
  // "pot_r5_death_ward_elixir.png (Emergency Revive)" - the art's title and
  // the item's name differ, and only the brief connects them.
  'Emergency Revive': 'death_ward_elixir_defensive_potion_consumable',
};

/** Sprites that deliberately get no item icon, and why.
 *
 * Listed rather than silently skipped so the coverage report stays honest and
 * nobody re-adds them as a guess.
 *
 * This list used to also carry every ring and amulet in the game, on the note
 * that the art brief had commissioned them and items.json contained none. That
 * was true and is no longer: the twenty missing pieces - five helmets, five
 * gloves and all ten accessories - were added to items.json, so the art now
 * resolves. What remains below is art that genuinely maps to no single item. */
const DELIBERATELY_UNMAPPED = {
  CopperMalachiteOre: 'combined-ore node art, not a single item',
  IronHematitOre: 'combined-ore node art, not a single item',
  'Cobalt&Silver ore': 'combined-ore node art, not a single item',
  DarksteelAstralite: 'combined-ore node art, not a single item',
  'Ebon tree': 'no ebon woodcutting_material exists in items.json',
  'Defensive Shield Potion': 'no matching potion; the four real ones are named differently',
  'vampiric lifesteal potion': 'no matching potion; the four real ones are named differently',
  'resistance potion': 'no matching potion; the four real ones are named differently',
  // "Emergency Revive" used to be listed here as unmappable. It is not - the
  // art brief names it as the Death Ward Elixir - so it now has an alias
  // above and this entry would be dead, contradictory documentation.
  Acacia: 'ambiguous between acacia_log and acacia_twig',
  Frostpine: 'ambiguous between frostpine_log and frostpine_twig',
  Sulfur: 'duplicate of "volcanic sulfur", which is mapped',
  Gold: 'currency icon, mapped separately',
  Gem: 'currency icon, mapped separately',
};

/** Equipment-art piece noun -> the slot token used in items.json BaseIds.
 *
 * Only the slot is taken from the noun; the item is then required to be the
 * unique piece of that set in that slot, so a noun landing in the wrong bucket
 * produces a skipped sprite rather than a wrong icon.
 *
 * The one worth naming: GREAVES ARE LEGGINGS HERE, not boots
 * (`eq_steel_greaves_leggings_armor_slot_base`), while sabatons are boots.
 * Both appear in the same TIER 1 folder, so guessing from the English would
 * swap two pieces of the same set. */
const SLOT_BY_PIECE_NOUN = {
  // helmet
  helm: 'helmet', hood: 'helmet', cowl: 'helmet', circlet: 'helmet',
  greathelm: 'helmet', visor: 'helmet', crown: 'helmet',
  // A PELT IS A HELMET HERE, and it was filed as a chest piece.
  //
  // brawler_pelt.webp is a wolf's head worn as a hood, and items.json agrees:
  // `eq_brawler_pelt_helmet_armor_slot_base` names the slot outright. Under
  // 'chest' it collided with brawler_harness.webp - both resolved to the set's
  // one chest piece, one overwrote the other, and the helmet the art was drawn
  // for got no icon at all. The Frost Brawler set is the only one with either
  // noun, so this is an identification rather than a rule.
  pelt: 'helmet',
  // gloves
  gauntlets: 'gloves', wraps: 'gloves', mitts: 'gloves', claws: 'gloves',
  grips: 'gloves', fists: 'gloves', gloves: 'gloves', bracers: 'gloves',
  // chest
  cuirass: 'chest', shroud: 'chest', vest: 'chest', robe: 'chest',
  harness: 'chest', carapace: 'chest', body: 'chest',
  hauberk: 'chest', bulwark_chest: 'chest',
  // boots
  boots: 'boots', sabatons: 'boots', treads: 'boots', stompers: 'boots',
  striders: 'boots', walkers: 'boots', footplates: 'boots',
  glacier_treads: 'boots',
  // leggings
  trousers: 'leggings', leggings: 'leggings', chausses: 'leggings',
  breeches: 'leggings', pants: 'leggings', tassets: 'leggings',
  guards: 'leggings', greaves: 'leggings',
  // amulet
  pendant: 'amulet', amulet: 'amulet', talisman: 'amulet', gorget: 'amulet',
  // ring
  band: 'ring', loop: 'ring', signet: 'ring', ring: 'ring',
};

/** Tool wood tiers, in the order the log progression uses.
 *
 * ASSUMPTION, stated because it is one: CachedCurrentToolTier is written from
 * ForgeLevel server-side and its range is not documented anywhere, so the
 * index is clamped to this list rather than trusted. A tier past the end shows
 * the last icon instead of nothing.
 *
 * `art` is the wood as the FILENAME spells it, `id` as items.json spells it -
 * and they differ in exactly one place, because the art says "Acatia" and the
 * catalogue says "acacia". That single letter is why this is a table of pairs
 * rather than a slug() call: a normalizer would produce `acatia_axe_tool`,
 * which is not an item, and the tool would go on rendering as initials with
 * nothing anywhere saying why. */
const TOOL_WOOD_ORDER = [
  { art: 'Normal', id: 'normal' },
  { art: 'Birch', id: 'birch' },
  { art: 'Golden Birch', id: 'golden_birch' },
  { art: 'Willow', id: 'willow' },
  { art: 'Whisper Willow', id: 'whisper_willow' },
  { art: 'Acatia', id: 'acacia' },
  { art: 'Ironwood', id: 'ironwood' },
  { art: 'Frostpine', id: 'frostpine' },
  { art: 'Glacier pine', id: 'glacier_pine' },
  { art: 'Ebon', id: 'ebon' },
  { art: 'Voidbark', id: 'voidbark' },
];

/** Tool kind -> the token items.json uses in the BaseId.
 *
 * The shape is `<wood>_<kind>_tool`, e.g. `golden_birch_fishing_rod_tool`. Note
 * that the rod's token is two words where the client's kind is one. */
const TOOL_KIND_ID_TOKEN = { axe: 'axe', pickaxe: 'pickaxe', rod: 'fishing_rod' };

// ---------------------------------------------------------------------------

function readJson(name) {
  // These files carry a UTF-8 BOM, which JSON.parse rejects.
  return JSON.parse(readFileSync(join(gameData, name), 'utf8').replace(/^\uFEFF/, ''));
}

function walk(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) out.push(...walk(full));
    else if (extname(entry).toLowerCase() === '.webp') out.push(full);
  }
  return out;
}

function slug(text) {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '');
}

const items = readJson('items.json');
const monsters = readJson('monsters.json');
const itemIds = new Set(items.map((i) => i.BaseId));

const files = walk(spriteRoot).map((f) => relative(spriteRoot, f).split('\\').join('/'));

const monsterIcons = {};
const itemIcons = {};
const raceIcons = {};
const toolIcons = { axe: [], pickaxe: [], rod: [] };
const misc = {};

const monsterByName = new Map(monsters.map((m) => [m.Name, m.Id]));
const problems = [];
const unmatched = [];

for (const file of files) {
  const name = basename(file, '.webp');

  // --- monsters: exact Name match, which covers all 25 canonical ones ------
  if (file.includes('/Monsters/')) {
    const id = monsterByName.get(name);
    if (id === undefined) problems.push(`monster sprite "${name}" matches no monster Name`);
    else monsterIcons[id] = file;
    continue;
  }

  // --- races ---------------------------------------------------------------
  if (file.startsWith('Characters/')) {
    const [race, sex] = name.split('_');
    const raceId = RACE_NAME_TO_ID[race];
    if (raceId === undefined) {
      problems.push(`character sprite "${name}" has no RaceId alias`);
      continue;
    }
    raceIcons[raceId] ??= {};
    raceIcons[raceId][sex?.toLowerCase() === 'female' ? 'female' : 'male'] = file;
    continue;
  }

  // --- gathering tools -----------------------------------------------------
  //
  // Keyed off the DIRECTORY, not the filename. Matching a trailing "axe" would
  // also swallow "brawler battleaxe", which is an equipment weapon in
  // Melee weapons/ and has nothing to do with the gathering tool ladder.
  const TOOL_DIRS = { 'Tools&Equipment/axes/': 'axe', 'Tools&Equipment/pickaxes/': 'pickaxe', 'Tools&Equipment/fishing rods/': 'rod' };
  const toolDir = Object.keys(TOOL_DIRS).find((d) => file.startsWith(d));
  if (toolDir) {
    const kind = TOOL_DIRS[toolDir];
    const wood = name.replace(/\s*(axe|pickaxe|fishing rod)$/i, '').trim();
    const tier = TOOL_WOOD_ORDER.findIndex((w) => w.art === wood);
    if (tier < 0) {
      problems.push(`tool sprite "${name}" has an unknown wood tier "${wood}"`);
      continue;
    }
    toolIcons[kind][tier] = file;

    // Modul: A TOOL IS ALSO AN ITEM, AND ONLY ONE LOOKUP KNEW THAT.
    //
    // The (kind, tier) matrix above is what the Gathering screen asks for -
    // "show me the axe I am holding" - and it worked. But the paper doll, the
    // chest and the forge all draw through ItemIcon.svelte, which asks
    // itemIcon(baseItemId) and reads ITEM_ICONS only. A `_tool` BaseId was
    // never in that table, so all 33 crafted tools rendered as two-letter
    // initials in every place except the one screen that asks by tier.
    //
    // Emitting the same path under both keys fixes it in the one place the
    // truth is derived, rather than teaching itemIcon() to reverse-engineer a
    // (kind, tier) out of a string - toolIcon() is untouched by this.
    const baseId = `${TOOL_WOOD_ORDER[tier].id}_${TOOL_KIND_ID_TOKEN[kind]}_tool`;
    if (!itemIds.has(baseId)) {
      problems.push(`tool sprite "${name}" implies BaseId "${baseId}", which is not in items.json`);
      continue;
    }
    itemIcons[baseId] = file;
    continue;
  }

  // --- currency ------------------------------------------------------------
  if (name === 'Gold') { misc.gold = file; continue; }
  if (name === 'Gem') { misc.diamond = file; continue; }

  // --- items: explicit alias first, then an EXACT-only auto match ----------
  const alias = MATERIAL_ALIASES[name];
  if (alias) {
    for (const target of Array.isArray(alias) ? alias : [alias]) {
      if (!itemIds.has(target)) problems.push(`alias "${name}" -> "${target}" no longer exists in items.json`);
      else itemIcons[target] = file;
    }
    continue;
  }

  if (DELIBERATELY_UNMAPPED[name]) continue;

  // --- equipment set pieces: match on SET + SLOT, never on the noun --------
  //
  // The art and the content file use different words for the same piece:
  // `linen_wraps.png` is `eq_linen_mitts_gloves_armor_slot_base`,
  // `steel_cuirass.png` is `eq_steel_harness_chest_armor_slot_base`. The nouns
  // are decoration; what is unambiguous is that each set has exactly ONE piece
  // per slot.
  //
  // So the noun is used only to derive a SLOT, and the item is then required
  // to be the unique member of that set in that slot. If two candidates
  // survive, the sprite is skipped rather than assigned - a wrong icon on a
  // piece of equipment is a lie the player acts on.
  const setPiece = /^([a-z]+)_(.+)$/.exec(name);
  if (setPiece && file.startsWith('Tools&Equipment/TIER ')) {
    const [, setToken, pieceNoun] = setPiece;
    const slot = SLOT_BY_PIECE_NOUN[pieceNoun];
    if (slot === undefined) {
      unmatched.push(`${file} (no slot known for piece noun "${pieceNoun}")`);
      continue;
    }

    // THE `eq_` FAMILY WINS WHEN BOTH EXIST.
    //
    // items.json carries two unrelated families that share a word: the
    // designed sets are `eq_steel_*` at region tier 1, while `steel_*` with no
    // prefix is a legacy region-4 line that predates them. Accepting either
    // left four Chiming Steel pieces and the iron signet with "2 candidates"
    // and no icon. The art belongs to the designed set, so that is preferred
    // and the legacy family is only consulted when there is no `eq_` match.
    const prefixed = items.filter(
      (i) => i.BaseId.startsWith(`eq_${setToken}_`) && i.BaseId.includes(`_${slot}_`),
    );
    const legacy = items.filter(
      (i) => i.BaseId.startsWith(`${setToken}_`) && i.BaseId.includes(`_${slot}_`),
    );
    const setCandidates = prefixed.length > 0 ? prefixed : legacy;

    if (setCandidates.length === 1) itemIcons[setCandidates[0].BaseId] = file;
    else unmatched.push(`${file} (set "${setToken}" slot "${slot}": ${setCandidates.length} candidates)`);
    continue;
  }

  const s = slug(name);
  const loose = items.filter((i) => {
    const b = slug(i.BaseId);
    return b === s || b === `eq_${s}` || b.startsWith(`${s}_`) || b.startsWith(`eq_${s}_`);
  });

  // AN EXACT BaseId MATCH BEATS A PREFIX ONE, and is not ambiguous with it.
  //
  // The prefix arm exists because most art is named for the material and the
  // item id adds a category suffix ("Iron bar" -> iron_bar_crafting_material).
  // But `copper_ore.webp` matches BOTH `copper_ore` and its legacy twin
  // `copper_ore_crafting_material`, so the ambiguity rule threw away a sprite
  // whose filename is character-for-character the item's own id. A file named
  // exactly after an item is the strongest signal in this whole script - there
  // is nothing to guess - so it wins outright and the prefix arm is only
  // consulted when nothing matched exactly.
  const exact = loose.filter((i) => slug(i.BaseId) === s);
  const candidates = exact.length > 0 ? exact : loose;

  if (candidates.length === 1) itemIcons[candidates[0].BaseId] = file;
  else unmatched.push(`${file}${candidates.length > 1 ? ` (ambiguous: ${candidates.map((c) => c.BaseId).join(', ')})` : ''}`);
}

if (problems.length > 0) {
  console.error('sprite generation failed:');
  for (const p of problems) console.error('  ' + p);
  process.exit(1);
}

const header = `// GENERATED by scripts/generate-sprites.mjs - do not edit.
//
// Sprite paths are relative to the server's /sprites route. The server owns
// the files (linked out of client/Assets/Images/Sprites by its csproj), so
// there is exactly one copy of the art and the two clients cannot drift.
//
// Anything absent here has NO artwork, which is a fact about the project
// rather than a bug - callers fall back to a readable placeholder.
`;

const lines = [header];
lines.push(`export const MONSTER_ICONS: Readonly<Record<number, string>> = ${JSON.stringify(monsterIcons, null, 2)};\n`);
lines.push(`export const ITEM_ICONS: Readonly<Record<string, string>> = ${JSON.stringify(itemIcons, null, 2)};\n`);
lines.push(`export const RACE_ICONS: Readonly<Record<number, { male?: string; female?: string }>> = ${JSON.stringify(raceIcons, null, 2)};\n`);
lines.push(`/** Indexed by tool tier; a tier past the end clamps to the last entry. */`);
lines.push(`export const TOOL_ICONS: Readonly<Record<'axe' | 'pickaxe' | 'rod', readonly string[]>> = ${JSON.stringify(toolIcons, null, 2)};\n`);
lines.push(`export const CURRENCY_ICONS: Readonly<{ gold?: string; diamond?: string }> = ${JSON.stringify(misc, null, 2)};\n`);

// ---------------------------------------------------------------------------
// The coverage report
// ---------------------------------------------------------------------------
//
// Modul: THE GAP HAS TO BE A COMMITTED FILE, not a number in a console line.
//
// Two thirds of the catalogue has no artwork. That was known, in the sense
// that someone had counted it once by hand and written the count in a
// document - which is exactly how it stopped being true. A generated,
// committed, sorted list means the diff of any content change says which items
// gained or lost art, and MISSING_ART_BUDGET means the total cannot drift
// upwards without somebody editing the number and being asked why.
//
// Grouped by the BaseId's own shape rather than by anything semantic: the
// suffix is what the content file actually guarantees, and a category derived
// from a guess would put items in the wrong bucket and make the list less
// trustworthy than no list.

/** BaseId -> the bucket it is reported under. Order is the reporting order. */
function categoryOf(baseId) {
  if (/_tool$/.test(baseId)) return 'tools';
  if (/_(log|twig)$/.test(baseId) || /_ore$/.test(baseId)) return 'logs & ores';
  if (/_crafting_material$/.test(baseId)) return 'crafting materials';
  if (/^eq_/.test(baseId) || /_slot_base$/.test(baseId)) return 'equipment';
  if (/_consumable$/.test(baseId)) return 'consumables';
  if (/_material$/.test(baseId)) return 'gathering & profession materials';
  return 'other';
}

const CATEGORY_ORDER = [
  'logs & ores',
  'tools',
  'equipment',
  'crafting materials',
  'gathering & profession materials',
  'consumables',
  'other',
];

const missingByCategory = new Map(CATEGORY_ORDER.map((c) => [c, []]));
for (const item of items) {
  if (itemIcons[item.BaseId]) continue;
  const bucket = missingByCategory.get(categoryOf(item.BaseId));
  bucket.push(item.BaseId);
}
const missingCount = [...missingByCategory.values()].reduce((n, v) => n + v.length, 0);

const missingReport = [
  '# GENERATED by client_web/scripts/generate-sprites.mjs - do not edit.',
  '#',
  '# Every catalogued item (server/GameData/items.json) with no entry in',
  '# ITEM_ICONS, i.e. every item that renders as two initials instead of a',
  '# picture. Sorted and grouped so the diff is readable; the count is asserted',
  '# against MISSING_ART_BUDGET in the generator, and that budget may only fall.',
  '#',
  '# To clear an entry: add art under client/Assets/Images/SpritesWeb/ named',
  '# exactly after the BaseId (an exact filename match wins outright), or add an',
  '# explicitly verified alias to MATERIAL_ALIASES. Then lower the budget.',
  '',
  `total ${missingCount} of ${items.length} catalogued items have no artwork`,
  '',
];
for (const category of CATEGORY_ORDER) {
  const ids = missingByCategory.get(category);
  if (ids.length === 0) continue;
  missingReport.push(`## ${category} (${ids.length})`);
  for (const id of [...ids].sort()) missingReport.push(id);
  missingReport.push('');
}
const missingText = missingReport.join('\n');

// ---------------------------------------------------------------------------

const generatedText = lines.join('\n');

if (checkOnly) {
  // Modul: --check is the CI gate, and it checks TWO different things.
  //
  // The drift half is the same contract generate-protocol.mjs enforces: the
  // committed file must be what this script would write, or someone edited art
  // or content and did not regenerate. The budget half is the ratchet - it
  // catches the opposite failure, where the generator is perfectly up to date
  // and the game simply got uglier.
  const failures = [];
  for (const [path, expected] of [[outFile, generatedText], [missingFile, missingText]]) {
    let actual = null;
    try {
      actual = readFileSync(path, 'utf8');
    } catch {
      failures.push(`${path} does not exist - run: npm run generate:sprites`);
      continue;
    }
    if (actual !== expected) failures.push(`${path} is stale - run: npm run generate:sprites`);
  }
  if (missingCount > MISSING_ART_BUDGET) {
    failures.push(
      `${missingCount} items have no artwork, budget is ${MISSING_ART_BUDGET}. ` +
        'This number may only go down: something either added art-less items or broke a mapping. ' +
        'See src/lib/ui/sprites.missing.txt for which ones.',
    );
  }
  if (failures.length > 0) {
    console.error('sprite check failed:');
    for (const f of failures) console.error('  ' + f);
    process.exit(1);
  }
  console.log(
    `sprite check ok: ${Object.keys(itemIcons).length} items with art, ` +
      `${missingCount} without (budget ${MISSING_ART_BUDGET})`,
  );
} else {
  writeFileSync(outFile, generatedText, 'utf8');
  writeFileSync(missingFile, missingText, 'utf8');

  console.log(`wrote ${outFile}`);
  console.log(
    `  ${Object.keys(monsterIcons).length} monsters, ${Object.keys(itemIcons).length} items, ` +
      `${Object.keys(raceIcons).length} races, ${Object.values(toolIcons).flat().filter(Boolean).length} tools`,
  );
  console.log(`wrote ${missingFile}`);
  console.log(`  ${missingCount} of ${items.length} items have no artwork (budget ${MISSING_ART_BUDGET})`);
  if (missingCount > MISSING_ART_BUDGET) {
    console.error(
      `  ERROR: that is above the budget of ${MISSING_ART_BUDGET}, which may only go down.`,
    );
    process.exit(1);
  }
  if (unmatched.length > 0) {
    console.log(`  ${unmatched.length} sprite(s) matched no single item and were skipped:`);
    for (const u of unmatched) console.log('    ' + u);
  }
}
