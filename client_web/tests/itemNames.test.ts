// Modul: item display names, which are DERIVED - not one of the 437 items in
// items.json carries a Name field, so prettifyBaseId is the only thing
// standing between a player and a raw BaseId.
//
// The cases that matter are the ones where a plumbing word also appears in the
// real name. Those read correctly only because the suffix list cuts whole
// suffixes rather than a trailing run of known words, and nothing about the
// output makes it obvious which rule produced it - a later "simplification" to
// a token filter would pass any test that only checked the easy names and
// silently rename a third of the catalogue. So the doubled forms are pinned
// first and explicitly.

import { describe, it, expect } from 'vitest';
import { prettifyBaseId } from '../src/lib/net/content';

describe('prettifyBaseId', () => {
  it('keeps a family word that is also part of the name', () => {
    // Armour names its slot separately, so the word appears twice and only the
    // second one is structure.
    expect(prettifyBaseId('eq_obsidian_gloves_gloves_armor_slot_base')).toBe('Obsidian Gloves');
    expect(prettifyBaseId('runed_boots_boots_armor_slot_base')).toBe('Runed Boots');

    // A shield is the other shape: its family word IS the slot marker, so the
    // cut is one token shorter than it looks.
    expect(prettifyBaseId('gilded_round_shield_shield_slot_base')).toBe('Gilded Round Shield');
  });

  it('strips the offhand marker that made the buckler unreadable', () => {
    expect(prettifyBaseId('eq_linen_buckler_helper_offhand_base')).toBe('Linen Buckler');
    expect(prettifyBaseId('eq_hunter_quiver_helper_offhand_base')).toBe('Hunter Quiver');
  });

  it('strips non-equipment scaffolding', () => {
    expect(prettifyBaseId('gold_ore_crafting_material')).toBe('Gold Ore');
    expect(prettifyBaseId('kelpie_mane_unique_regional_boss_material')).toBe('Kelpie Mane');
    expect(prettifyBaseId('cooked_canyon_catfish_t8_food')).toBe('Cooked Canyon Catfish');
    expect(prettifyBaseId('searing_tonic_offensive_potion_consumable')).toBe('Searing Tonic');
  });

  it('keeps name words that merely resemble scaffolding', () => {
    // "_tool" is structure; the tool's own type is the name and must survive.
    expect(prettifyBaseId('voidbark_pickaxe_tool')).toBe('Voidbark Pickaxe');
    expect(prettifyBaseId('ebon_fishing_rod_tool')).toBe('Ebon Fishing Rod');
    // "log" and "twig" end real names and are in no suffix rule.
    expect(prettifyBaseId('birch_log')).toBe('Birch Log');
  });

  it('removes exactly one suffix, never a chain', () => {
    // Cutting greedily would eat "material" here too and leave "Gargoyle Heart
    // Shard" looking right by accident while breaking the doubled cases above.
    expect(prettifyBaseId('gargoyle_heart_shard_alchemy_material')).toBe('Gargoyle Heart Shard');
  });
});
