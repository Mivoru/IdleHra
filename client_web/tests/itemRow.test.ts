import { describe, it, expect } from 'vitest';
import { GOLD_ITEM_ID, isChestMaterial, summarizeAffixes, itemMetaLine, splitWorn } from '../src/lib/ui/itemRow';
import { rarityName } from '../src/lib/ui/rarity';

// Task 99: the Chest listed Gold as a material with Sell all and Bin, and the
// server answers both by deleting the gold for 0 (it has no items.json price).
describe('isChestMaterial', () => {
  it('drops gold however much of it there is', () => {
    expect(GOLD_ITEM_ID).toBe('gold');
    expect(isChestMaterial({ ItemId: 'gold', Quantity: 2000 })).toBe(false);
  });

  it('keeps a real stack and drops an empty one', () => {
    expect(isChestMaterial({ ItemId: 'copper_ore', Quantity: 3 })).toBe(true);
    expect(isChestMaterial({ ItemId: 'copper_ore', Quantity: 0 })).toBe(false);
  });
});

describe('summarizeAffixes', () => {
  it('leads with the rarest affix and counts the rest', () => {
    const text = summarizeAffixes({ 'flat_hp@1': 40, 'crit_chance_pct@4': 12, 'flat_armor@2': 5, 'dodge_chance_pct@1': 8 });
    expect(text).toBe('+1.2% Crit Chance, +5 Armor, 2 more');
  });

  it('keeps payload order between equal rarities', () => {
    expect(summarizeAffixes({ 'flat_hp@3': 40, 'flat_armor@3': 5 })).toBe('+40 Hp, +5 Armor');
  });

  it('ignores the lock flag the payload carries beside the affixes', () => {
    expect(summarizeAffixes({ is_affix_locked: 1, 'flat_hp@2': 40 })).toBe('+40 Hp');
  });

  it('is empty for a piece with no affixes', () => {
    expect(summarizeAffixes({})).toBe('');
    expect(summarizeAffixes(undefined)).toBe('');
  });

  it('makes two same-named pieces read differently', () => {
    expect(summarizeAffixes({ 'flat_hp@2': 40 })).not.toBe(summarizeAffixes({ 'crit_chance_pct@2': 12 }));
  });
});

describe('itemMetaLine', () => {
  it('reads tier, rarity, then affixes', () => {
    expect(itemMetaLine({ regionTier: 2, qualityTier: 5, affixes: { 'flat_hp@2': 40 } })).toBe(
      `T2 ${rarityName(5)} - +40 Hp`,
    );
  });

  it('leaves out what it does not know', () => {
    expect(itemMetaLine({ regionTier: 0, qualityTier: 0, affixes: {} })).toBe('');
    expect(itemMetaLine({ extra: 'Food' })).toBe('Food');
  });
});

describe('splitWorn', () => {
  it('puts worn pieces in their own group and keeps each half in order', () => {
    const items = [
      { Id: 1, IsEquipped: false },
      { Id: 2, IsEquipped: true },
      { Id: 3, IsEquipped: false },
      { Id: 4, IsEquipped: true },
    ];
    const { worn, loose } = splitWorn(items);
    expect(worn.map((i) => i.Id)).toEqual([2, 4]);
    expect(loose.map((i) => i.Id)).toEqual([1, 3]);
  });
});
