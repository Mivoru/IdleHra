// Modul: equipment slots and activity classification.
//
// Both are wire contracts the server owns and the client must mirror exactly,
// so each one names its server-side source. Nothing here is inferred from a
// naming convention - this codebase already lost a whole region to an id-space
// collision that looked like a convention (see ActivityIdBands).

import type { StateUpdate } from '../net/protocol.generated';

// ---------------------------------------------------------------------------
// Equipment slots
// ---------------------------------------------------------------------------

/**
 * The seven slots the ACTIVE character's equipment occupies on StateUpdate.
 *
 * Slots 2 and 3 deliberately do not ship their equipment on the hot path -
 * gear changes on a button press, not at 10 Hz - so the roster reads the other
 * characters' loadouts from /api/v1/player/inventory instead. See
 * StateUpdatePacket's own comment on why that is 96 bytes a frame saved.
 */
export const SLOT_WEAPON = 0;
export const SLOT_HELMET = 1;
export const SLOT_CHEST = 2;
export const SLOT_GLOVES = 3;
export const SLOT_LEGGINGS = 4;
export const SLOT_BOOTS = 5;
export const SLOT_OFFHAND = 6;

// Modul: tools are worn. They were stackable materials in the chest, which is
// why every axe of a given wood was identical to every other one - a stack has
// no room for a rarity or an affix. Three slots, because a character carries
// an axe, a pickaxe and a rod at once and each accelerates its own profession.
export const SLOT_AXE = 7;
export const SLOT_PICKAXE = 8;
export const SLOT_ROD = 9;

export interface EquipmentSlot {
  index: number;
  label: string;
  /** The StateUpdate field carrying this slot's EquipmentInstance id, where
   *  the wire carries one. The three tool slots deliberately do not: gear
   *  changes on a button press, not ten times a second, so the paper doll
   *  reads them from the inventory snapshot with everyone else's. */
  field?: keyof StateUpdate;
  /** Its affix-lock companion field, where one exists on the wire. */
  lockField?: keyof StateUpdate;
}

export const EQUIPMENT_SLOTS: readonly EquipmentSlot[] = [
  { index: SLOT_WEAPON, label: 'Weapon', field: 'EquippedWeaponId', lockField: 'EquippedWeaponAffixLocked' },
  { index: SLOT_OFFHAND, label: 'Offhand', field: 'EquippedOffhandId' },
  { index: SLOT_HELMET, label: 'Helmet', field: 'EquippedHelmetId' },
  // EquippedArmorId became EquippedChestId: the old single "Armor" slot held
  // all four pieces, and chest is where the generic "_armor_slot_" fallback
  // still resolves - see resolveSlotIndex below.
  { index: SLOT_CHEST, label: 'Chest', field: 'EquippedChestId', lockField: 'EquippedArmorAffixLocked' },
  { index: SLOT_GLOVES, label: 'Gloves', field: 'EquippedGlovesId' },
  { index: SLOT_LEGGINGS, label: 'Leggings', field: 'EquippedLeggingsId', lockField: 'EquippedLeggingsAffixLocked' },
  { index: SLOT_BOOTS, label: 'Boots', field: 'EquippedBootsId' },
  // The wire does not carry tool instance ids - the paper doll reads them from
  // /api/v1/player/inventory like it reads every other character's gear - so
  // these three have no `field`.
  { index: SLOT_AXE, label: 'Axe' },
  { index: SLOT_PICKAXE, label: 'Pickaxe' },
  { index: SLOT_ROD, label: 'Rod' },
];

/**
 * A faithful port of EquipmentSlotEngine.ResolveSlotIndex. Returns -1 for
 * anything not equippable.
 *
 * THE ORDER OF THESE TESTS IS THE CONTRACT, not a style choice. Every armour
 * BaseId carries the generic "_armor_slot_" marker IN ADDITION to its specific
 * one - "transcendent_platelegs_leggings_armor_slot_base" matches both - so
 * every specific marker must be tested before the generic fallback. The server
 * comment records that it once had this bug in miniature. Sorting these tests
 * "tidily", or trusting a first-match-wins regex alternation, silently files
 * every helmet, glove, boot and legging into the chest slot.
 *
 * The offhand test must also precede the weapon and generic-armour tests: the
 * helper BaseIds carry neither of those markers, and before it existed they
 * fell through to -1 and were silently unequippable.
 */
export function resolveSlotIndex(baseItemId: string): number {
  if (!baseItemId) return -1;

  // Tools first. A "_tool" id carries none of the armour or weapon markers
  // below, so the order is not load-bearing - it just says plainly that a tool
  // is its own thing rather than a weapon that happens to chop.
  if (baseItemId.endsWith('_tool')) {
    if (baseItemId.includes('_pickaxe_')) return SLOT_PICKAXE;
    if (baseItemId.includes('_fishing_rod_')) return SLOT_ROD;
    if (baseItemId.includes('_axe_')) return SLOT_AXE;
  }

  if (baseItemId.includes('_helmet_')) return SLOT_HELMET;
  if (baseItemId.includes('_gloves_')) return SLOT_GLOVES;
  if (baseItemId.includes('_boots_')) return SLOT_BOOTS;
  if (baseItemId.includes('_leggings_')) return SLOT_LEGGINGS;
  if (baseItemId.includes('_chest_')) return SLOT_CHEST;
  if (baseItemId.includes('_helper_offhand_')) return SLOT_OFFHAND;
  if (baseItemId.includes('_weapon_slot_')) return SLOT_WEAPON;
  // Unrecognised armour falls back to chest rather than becoming
  // unequippable, matching the server exactly.
  if (baseItemId.includes('_armor_slot_')) return SLOT_CHEST;

  return -1;
}

export function equippedIdFor(snapshot: StateUpdate, slot: EquipmentSlot): number {
  // A tool slot has no wire field - see EquipmentSlot.field.
  if (!slot.field) return 0;
  const value = snapshot[slot.field];
  return typeof value === 'number' ? value : 0;
}

export function isAffixLocked(snapshot: StateUpdate, slot: EquipmentSlot): boolean {
  if (!slot.lockField) return false;
  const value = snapshot[slot.lockField];
  return typeof value === 'number' && value !== 0;
}

// ---------------------------------------------------------------------------
// Activity id bands
// ---------------------------------------------------------------------------

// Mirrors server/FolkIdle.Server/Engine/ActivityIdBands.cs exactly.
//
// These bands exist because combat and gathering used to SHARE a numeric space
// and collided: Region 3's five monsters carried ids 101-105, which were also
// Woodcutting nodes 101-105, and the tick resolved gathering first - so Region
// 3 could not be fought at all and the race unlock behind its boss was
// unreachable. Gathering moved to thousand-bands. Any client that hardcodes
// the pre-move numbering (101-412) is talking about monsters.
export const ACTIVITY_BANDS = {
  combatFirst: 1,
  combatLast: 999,
  woodcutting: 1000,
  mining: 2000,
  fishing: 3000,
  herbalism: 4000,
  bandSize: 1000,
  worldBoss: 9999,
} as const;

export const GATHERING_FIRST = ACTIVITY_BANDS.woodcutting;
export const GATHERING_LAST = ACTIVITY_BANDS.herbalism + ACTIVITY_BANDS.bandSize - 1;

export function isCombatActivity(activityId: number): boolean {
  return activityId >= ACTIVITY_BANDS.combatFirst && activityId <= ACTIVITY_BANDS.combatLast;
}

export function isGatheringActivity(activityId: number): boolean {
  return activityId >= GATHERING_FIRST && activityId <= GATHERING_LAST;
}

/** 0 Woodcutting, 1 Mining, 2 Fishing, 3 Herbalism - GatheringNodeDefinition's own numbering. */
export const PROFESSIONS: readonly { id: number; name: string; band: number }[] = [
  { id: 0, name: 'Woodcutting', band: ACTIVITY_BANDS.woodcutting },
  { id: 1, name: 'Mining', band: ACTIVITY_BANDS.mining },
  { id: 2, name: 'Fishing', band: ACTIVITY_BANDS.fishing },
  // Modul: Herbalism is gone - the design list has no herb in it, and no
  // herbalism tool where axes, pickaxes and rods all exist in five tiers.
];

export function professionName(professionType: number): string {
  return PROFESSIONS.find((p) => p.id === professionType)?.name ?? `Profession ${professionType}`;
}

// ---------------------------------------------------------------------------
// Crafting professions - A DIFFERENT ENUM FROM GATHERING'S
// ---------------------------------------------------------------------------

// Modul: `RecipeDefinition.ProfessionType` and `GatheringNodeDefinition.
// ProfessionType` share a field NAME and nothing else. Gathering runs
// 0 Woodcutting / 1 Mining / 2 Fishing / 3 Herbalism; crafting runs
// 2 Smelting / 3 Equipment / 4 Cooking / 5 Alchemy, and the authority for that
// is CraftingEngine.GrantCraftedOutputAsync, which routes the output by it -
// profession 3 becomes a real EquipmentInstance while the rest are stackables.
//
// The overlap is what makes this dangerous rather than merely untidy: values 2
// and 3 are valid in BOTH enums and mean different things, so reusing the
// gathering names labelled a Copper Bar recipe "Fishing" and an equipment
// recipe "Herbalism". Two enums that look alike is the same trap as two
// sources of truth, and it reads as plausible right up until someone notices.
export const CRAFTING_PROFESSIONS: Record<number, string> = {
  2: 'Smelting',
  3: 'Equipment',
  4: 'Cooking',
  5: 'Alchemy',
};

export function craftingProfessionName(professionType: number): string {
  return CRAFTING_PROFESSIONS[professionType] ?? `Profession ${professionType}`;
}

// ---------------------------------------------------------------------------
// Halt reasons
// ---------------------------------------------------------------------------

// Mirrors ActivityHaltReason. Naming the cause is the entire point of the
// field: every one of these states used to be indistinguishable from "idle by
// choice", which is what made a stopped character impossible to explain.
// Modul: REASON 3 SAID THE OPPOSITE OF WHAT HAPPENS, and the server's own
// comment is where the wrong version came from.
//
// SimulationEngine says, next to where it raises this reason, that "a full
// backpack does not stop the activity - it keeps running and throws every drop
// away". That is not what the code does. ProcessSubTick's first line is
// `if (payload.ActiveActivityId <= 0 || payload.InventorySpaceRemaining <= 0)
// return;`, so a full backpack returns before combat, before gathering, before
// XP - everything stops.
//
// Verified on the wire rather than by reading: with the dev fixture at 20/20,
// ChangeActivity sets ActiveActivityId to 91 and CurrentMonsterId never leaves
// 0, because no tick reaches the spawn.
//
// Telling a player they are "still running, still losing loot" while they earn
// nothing at all is worse than telling them nothing, because it is the one
// message that makes them NOT go and fix it. Whether the server should behave
// the way its comment claims is a gameplay decision; saying what it actually
// does is not.
// ActivityIdBands.CraftingBand. A crafting activity is the band plus the
// recipe's INDEX in ContentRegistry.Recipes - not its result item id, which
// would collide with the gathering bands.
export const CRAFTING_BAND = 5000;
export const CRAFTING_BAND_SIZE = 1000;

export function isCraftingActivity(activityId: number): boolean {
  return activityId >= CRAFTING_BAND && activityId < CRAFTING_BAND + CRAFTING_BAND_SIZE;
}

export function craftingActivityId(recipeIndex: number): number {
  return CRAFTING_BAND + recipeIndex;
}

export const HALT_REASONS: Record<number, string> = {
  0: '',
  1: 'Out of food - auto-eat has nothing left, so you are fighting without healing.',
  2: 'Died and respawned. Combat activities stop on death; gathering does not.',
  // 3 was "backpack full". The backpack is gone - storage is one unlimited
  // village chest - and the server no longer sets this reason. Kept mapped so
  // an old queued packet cannot render "undefined".
  3: '',
  4: 'No eligible character - the only one may be lent out as an Academy mentor.',
};

export const HALT_REASON_SHORT: Record<number, string> = {
  0: '',
  1: 'Out of food',
  2: 'Died',
  3: '',
  4: 'No character',
};

// ---------------------------------------------------------------------------
// Age phases
// ---------------------------------------------------------------------------

/** ProcessAgeSlot's thresholds: 36000 / 72000 / 108000 AgeTicks. */
export const AGE_PHASES = ['Child', 'Adult', 'Veteran', 'Elder'];

export function agePhaseName(phase: number): string {
  return AGE_PHASES[phase] ?? `Phase ${phase}`;
}
