import { describe, it, expect } from 'vitest';
import { compareDrop, comparisonLine, isUpgrade, pieceTotals } from '../src/lib/ui/lootCompare';
import { powerMultiplier } from '../src/lib/ui/rarity';
import type { ContentRegistry, ItemDefinition } from '../src/lib/net/content';

const sword: ItemDefinition = { Id: 1, BaseId: 'eq_r1_iron_melee_weapon_slot_sword', RegionTier: 1, BaseValueGold: 1, FlatAttackPower: 20, FlatDefenseRating: 0 };
const helm: ItemDefinition = { Id: 2, BaseId: 'eq_r1_iron_helmet_armor_slot_base', RegionTier: 1, BaseValueGold: 1, FlatAttackPower: 0, FlatDefenseRating: 10 };
const registry = {
  items: new Map([[1, sword], [2, helm]]),
  itemsByBaseId: new Map([[sword.BaseId, sword], [helm.BaseId, helm]]),
} as unknown as ContentRegistry;

describe('powerMultiplier mirrors RarityTier.PowerMultiplier', () => {
  it('is 1 at Normal, 2.12 at Transcendent and geometric between', () => {
    expect(powerMultiplier(1)).toBe(1);
    expect(powerMultiplier(14)).toBe(2.12);
    expect(powerMultiplier(7)).toBeCloseTo(Math.pow(2.12, 6 / 13), 10);
  });
});

describe('compareDrop', () => {
  it('scales both sides by rarity, so a rarer copy of the same sword is better', () => {
    const worn = [{ InstanceId: 9, BaseItemId: sword.BaseId, QualityTier: 1, SlotIndex: 0 }];
    const c = compareDrop(registry, sword.BaseId, 7, worn)!;
    expect(c.slotIndex).toBe(0);
    expect(c.attackDelta).toBe(pieceTotals(registry, sword.BaseId, 7).attack - 20);
    expect(c.attackDelta).toBeGreaterThan(0);
    expect(c.tierStep).toBe(6);
    expect(isUpgrade(c)).toBe(true);
    expect(comparisonLine(c)).toMatch(/^\+\d+ ATK · 6 tiers above your Normal$/);
  });

  it('compares against the SAME slot only', () => {
    const worn = [{ InstanceId: 9, BaseItemId: sword.BaseId, QualityTier: 14, SlotIndex: 0 }];
    const c = compareDrop(registry, helm.BaseId, 1, worn)!;
    expect(c.worn).toBeNull();
    expect(c.defenseDelta).toBe(10);
    expect(comparisonLine(c)).toBe('+10 DEF · helmet slot is empty');
  });

  it('says worse when it is worse', () => {
    const worn = [{ InstanceId: 9, BaseItemId: sword.BaseId, QualityTier: 10, SlotIndex: 0 }];
    const c = compareDrop(registry, sword.BaseId, 4, worn)!;
    expect(c.attackDelta).toBeLessThan(0);
    expect(isUpgrade(c)).toBe(false);
    expect(comparisonLine(c)).toContain('6 tiers below your');
  });

  it('sends a same-base, same-rarity copy to the affixes, and is no upgrade', () => {
    const worn = [{ InstanceId: 9, BaseItemId: sword.BaseId, QualityTier: 5, SlotIndex: 0 }];
    const c = compareDrop(registry, sword.BaseId, 5, worn)!;
    expect(comparisonLine(c)).toBe('same base stats - compare affixes');
    expect(isUpgrade(c)).toBe(false);
  });

  it('returns null for something with no slot', () => {
    expect(compareDrop(registry, 'raw_log', 1, [])).toBeNull();
  });
});
