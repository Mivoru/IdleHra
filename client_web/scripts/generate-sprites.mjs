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
const gameData = join(repoRoot, 'client', 'Assets', 'StreamingAssets', 'GameData');
const outFile = join(here, '..', 'src', 'lib', 'ui', 'sprites.generated.ts');

// ---------------------------------------------------------------------------
// Alias tables
// ---------------------------------------------------------------------------

/** Character sprite race name -> ContentRegistry.RaceIds.
 *
 * Four of the six match by name. Bes and Leshy are the art's Slavic-folklore
 * names for Kobold and Moosleute - Bes is a Slavic house-spirit analogous to a
 * kobold, Leshy a forest spirit analogous to "Moosleute" (German "moss folk").
 * No other pairing fits the remaining two races. Carried over verbatim from
 * the Unity builder, which reached the same conclusion. */
const RACE_NAME_TO_ID = {
  Human: 1,
  Vila: 2,
  Draugr: 3,
  Bes: 4,
  Vodnik: 5,
  Leshy: 6,
};

/** Sprite basename (exactly as on disk) -> item BaseId. */
const MATERIAL_ALIASES = {
  // Region 01 - from the Unity builder
  'Birch Tree': 'birch_trees_woodcutting_material',
  Copper: 'copper_ore_crafting_material',
  Malachite: 'malachite_ore',
  'Viper Venom Elixir': 'mat_viper_venom',

  // Region 02
  'Golden Willow log': 'whispering_willow_log',
  'Golden Willow twig': 'whispering_willow_twig',
  'Willow tree': 'willow_logs_woodcutting_material',
  Hematite: 'hematite_ore',
  'Iron bar': 'iron_bar_crafting_material',

  // Region 03 - "Acatia" is the art's spelling of acacia, and the "Golden"
  // variant of each tree is the upgraded species, matching the log/twig pairs
  // in items.json (birch->golden_birch, willow->whispering_willow,
  // acacia->ironwood, frostpine->glacier_pine, ebon->void_bark).
  'Acatia log': 'acacia_log',
  'Acatia twig': 'acacia_twig',
  'Golden Acatia log': 'ironwood_log',
  'Golden Acatia twig': 'ironwood_twig',
  Absidian: 'obsidian_ore_crafting_material',
  'volcanic sulfur': 'sulfur_ore',
  'Bear stew': 'bear_stew_food_consumable',

  // Region 04
  'Golden Frostpine log': 'glacier_pine_log',
  'Golden Frostpine twig': 'glacier_pine_twig',
  'Silver bar': 'silver_bar_crafting_material',
  'yeti meat platter': 'yeti_platter_food_consumable',

  // Region 05
  'Golden Ebon log': 'void_bark_log',
  'Golden Ebon twig': 'void_bark_twig',
  'Astralite crystals': 'astralite_ore',

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
  'Cobalt bar': 'no cobalt bar exists in items.json',
  'Darksteel bar': 'no darksteel bar exists in items.json',
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
  // gloves
  gauntlets: 'gloves', wraps: 'gloves', mitts: 'gloves', claws: 'gloves',
  grips: 'gloves', fists: 'gloves', gloves: 'gloves', bracers: 'gloves',
  // chest
  cuirass: 'chest', shroud: 'chest', vest: 'chest', robe: 'chest',
  harness: 'chest', pelt: 'chest', carapace: 'chest', body: 'chest',
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
 * the last icon instead of nothing. */
const TOOL_WOOD_ORDER = [
  'Normal',
  'Birch',
  'Golden Birch',
  'Willow',
  'Whisper Willow',
  'Acatia',
  'Ironwood',
  'Frostpine',
  'Glacier pine',
  'Ebon',
  'Voidbark',
];

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
    const tier = TOOL_WOOD_ORDER.indexOf(wood);
    if (tier < 0) problems.push(`tool sprite "${name}" has an unknown wood tier "${wood}"`);
    else toolIcons[kind][tier] = file;
    continue;
  }

  // --- currency ------------------------------------------------------------
  if (name === 'Gold') { misc.gold = file; continue; }
  if (name === 'Gem') { misc.diamond = file; continue; }

  // --- items: explicit alias first, then an EXACT-only auto match ----------
  const alias = MATERIAL_ALIASES[name];
  if (alias) {
    if (!itemIds.has(alias)) problems.push(`alias "${name}" -> "${alias}" no longer exists in items.json`);
    else itemIcons[alias] = file;
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
  const candidates = items.filter((i) => {
    const b = slug(i.BaseId);
    return b === s || b === `eq_${s}` || b.startsWith(`${s}_`) || b.startsWith(`eq_${s}_`);
  });

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

writeFileSync(outFile, lines.join('\n'), 'utf8');

console.log(`wrote ${outFile}`);
console.log(
  `  ${Object.keys(monsterIcons).length} monsters, ${Object.keys(itemIcons).length} items, ` +
    `${Object.keys(raceIcons).length} races, ${Object.values(toolIcons).flat().filter(Boolean).length} tools`,
);
if (unmatched.length > 0) {
  console.log(`  ${unmatched.length} sprite(s) matched no single item and were skipped:`);
  for (const u of unmatched) console.log('    ' + u);
}
