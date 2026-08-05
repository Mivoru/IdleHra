import { describe, it, expect } from 'vitest';
import {
  resolveSlotIndex,
  SLOT_WEAPON,
  SLOT_HELMET,
  SLOT_CHEST,
  SLOT_GLOVES,
  SLOT_LEGGINGS,
  SLOT_BOOTS,
  SLOT_OFFHAND,
  isCombatActivity,
  isGatheringActivity,
  ACTIVITY_BANDS,
  professionName,
  craftingProfessionName,
} from '../src/lib/ui/slots';

// Modul: resolveSlotIndex is a port of EquipmentSlotEngine.ResolveSlotIndex,
// and the ORDER of its tests is the contract rather than a style choice.
//
// 60 of the real items in GameData/items.json carry the generic
// "_armor_slot_" marker IN ADDITION to their specific one - e.g.
// "gilded_sallet_helmet_armor_slot_base" matches both "_helmet_" and
// "_armor_slot_". If the generic test runs first, or if someone "tidies" these
// into a regex alternation whose order is not guaranteed, every helmet, glove,
// boot and legging silently files into the chest slot. The server carries a
// comment saying it once had exactly this bug.
//
// The ids below are taken verbatim from GameData/items.json, not invented, so
// this tests the shapes that actually exist.
describe('resolveSlotIndex', () => {
  it('files each real item into its specific slot, not the generic fallback', () => {
    expect(resolveSlotIndex('gilded_sallet_helmet_armor_slot_base')).toBe(SLOT_HELMET);
    expect(resolveSlotIndex('eq_linen_mitts_gloves_armor_slot_base')).toBe(SLOT_GLOVES);
    expect(resolveSlotIndex('gilded_sabatons_boots_armor_slot_base')).toBe(SLOT_BOOTS);
    expect(resolveSlotIndex('gilded_chausses_leggings_armor_slot_base')).toBe(SLOT_LEGGINGS);
    expect(resolveSlotIndex('gilded_hauberk_chest_armor_slot_base')).toBe(SLOT_CHEST);
    expect(resolveSlotIndex('loch_crossbow_range_weapon_slot_base')).toBe(SLOT_WEAPON);
    expect(resolveSlotIndex('eq_linen_buckler_helper_offhand_base')).toBe(SLOT_OFFHAND);
  });

  it('falls back to chest for armour carrying only the generic marker', () => {
    // Deliberate: such an item stays equippable rather than silently becoming
    // unequippable, which is the server's stated behaviour.
    expect(resolveSlotIndex('plain_tunic_armor_slot_base')).toBe(SLOT_CHEST);
  });

  it('resolves an offhand before the weapon and generic-armour tests', () => {
    // Helper BaseIds carry neither "_weapon_slot_" nor "_armor_slot_", so
    // before the offhand test existed they fell through to -1 and were
    // silently unequippable.
    expect(resolveSlotIndex('eq_linen_buckler_helper_offhand_base')).not.toBe(-1);
  });

  it('returns -1 for anything not equippable', () => {
    expect(resolveSlotIndex('gold_ore_crafting_material')).toBe(-1);
    expect(resolveSlotIndex('cooked_mud_carp_t3_food')).toBe(-1);
    expect(resolveSlotIndex('')).toBe(-1);
  });
});

// Modul: the bands exist because combat and gathering once SHARED a numeric
// space. Region 3's five monsters carried ids 101-105, which were also
// Woodcutting nodes 101-105, and the tick resolved gathering first - so Region
// 3 was unfightable and the race unlock behind its boss unreachable.
describe('activity id bands', () => {
  it('classifies the canonical monsters as combat', () => {
    for (const id of [1, 91, 95, 115, 999]) {
      expect(isCombatActivity(id)).toBe(true);
      expect(isGatheringActivity(id)).toBe(false);
    }
  });

  it('classifies every gathering band as gathering', () => {
    for (const id of [1001, 1005, 2001, 2005, 3001, 3009, 4001, 4012]) {
      expect(isGatheringActivity(id)).toBe(true);
      expect(isCombatActivity(id)).toBe(false);
    }
  });

  it('keeps the pre-move numbering out of the gathering space', () => {
    // 101-412 were the OLD gathering ids and are monsters now. A client that
    // still sends 101 expecting to chop wood starts a fight instead.
    for (const legacy of [101, 105, 205, 412]) {
      expect(isGatheringActivity(legacy)).toBe(false);
      expect(isCombatActivity(legacy)).toBe(true);
    }
  });

  it('leaves the world boss outside both bands', () => {
    expect(isCombatActivity(ACTIVITY_BANDS.worldBoss)).toBe(false);
    expect(isGatheringActivity(ACTIVITY_BANDS.worldBoss)).toBe(false);
  });

  it('treats idle (0) as neither', () => {
    expect(isCombatActivity(0)).toBe(false);
    expect(isGatheringActivity(0)).toBe(false);
  });
});

// Modul: RecipeDefinition.ProfessionType and GatheringNodeDefinition.
// ProfessionType share a field NAME and nothing else, and values 2 and 3 are
// valid in BOTH with different meanings. Reusing the gathering names labelled
// a Copper Bar recipe "Fishing" - plausible-looking and completely wrong.
describe('profession enums do not overlap in meaning', () => {
  it('names gathering professions', () => {
    expect(professionName(0)).toBe('Woodcutting');
    expect(professionName(1)).toBe('Mining');
    expect(professionName(2)).toBe('Fishing');
  });

  // Modul: Herbalism is GONE, deliberately - the design list has no herb in
  // it and no herbalism tool where axes, pickaxes and rods all exist in five
  // tiers (see PROFESSIONS in slots.ts). This test asserted it was named, and
  // had been failing since the removal.
  //
  // Kept as an assertion rather than deleted, because the band constant 4000
  // still exists in ActivityIdBands and a future reader could reasonably
  // conclude the profession does too. The fallback string is the answer:
  // an id nothing authors resolves to a placeholder, not to a name that
  // implies content behind it.
  it('has no name for the removed herbalism profession', () => {
    expect(professionName(3)).toBe('Profession 3');
  });

  it('names crafting professions from the OTHER enum', () => {
    expect(craftingProfessionName(2)).toBe('Smelting');
    expect(craftingProfessionName(3)).toBe('Equipment');
    expect(craftingProfessionName(4)).toBe('Cooking');
    expect(craftingProfessionName(5)).toBe('Alchemy');
  });

  it('disagrees on the two values both enums define - which is the whole point', () => {
    expect(craftingProfessionName(2)).not.toBe(professionName(2));
    expect(craftingProfessionName(3)).not.toBe(professionName(3));
  });
});
