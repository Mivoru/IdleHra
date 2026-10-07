import { describe, it, expect } from 'vitest';
import {
  buildFusionRows,
  fusionFeeCeiling,
  hiddenByPending,
  settlePending,
  type FusionPiece,
} from '../src/lib/ui/fusionRows';

const AXE = 'eq_birch_axe_tool_slot_base';
const HELM = 'eq_sentry_helm_helmet_armor_slot_base';
const SWORD = 'eq_iron_sword_melee_weapon_slot_base';

let nextId = 1;
function piece(base: string, tier: number, extra: Partial<FusionPiece> = {}): FusionPiece {
  return { Id: nextId++, BaseItemId: base, QualityTier: tier, IsAffixLocked: false, Affixes: {}, ...extra };
}

describe('buildFusionRows (task 100)', () => {
  it('makes one row per item, offering the highest rarity that has three', () => {
    const owned = [piece(HELM, 1), piece(HELM, 1), piece(HELM, 1), piece(HELM, 4), piece(HELM, 4), piece(HELM, 4), piece(HELM, 4)];
    const rows = buildFusionRows(owned, new Set(), new Set(), 14);

    expect(rows).toHaveLength(1);
    expect(rows[0].counts).toEqual([
      { tier: 4, count: 4 },
      { tier: 1, count: 3 },
    ]);
    expect(rows[0].next?.tier).toBe(4);
    expect(rows[0].stack?.tier).toBe(1);
    expect(rows[0].total).toBe(7);
  });

  it('keeps the best-rolled piece as the target', () => {
    const plain = piece(HELM, 2);
    const rolled = piece(HELM, 2, { Affixes: { a: 1, b: 2 } });
    const other = piece(HELM, 2);
    const [row] = buildFusionRows([plain, rolled, other], new Set(), new Set(), 14);

    expect(row.next?.target).toBe(rolled.Id);
    expect(row.next?.sacrifices).toEqual([plain.Id, other.Id]);
  });

  it('never offers a worn or locked piece, and puts the worn line first even with nothing to fuse', () => {
    const worn = piece(SWORD, 6);
    const locked = [piece(AXE, 1, { IsAffixLocked: true }), piece(AXE, 1), piece(AXE, 1)];
    const helms = [piece(HELM, 1), piece(HELM, 1), piece(HELM, 1)];
    const rows = buildFusionRows([worn, ...locked, ...helms], new Set([worn.Id]), new Set(), 14);

    expect(rows.map((r) => r.baseItemId)).toEqual([SWORD, HELM]);
    expect(rows[0].worn).toBe(true);
    expect(rows[0].wornTier).toBe(6);
    expect(rows[0].next).toBeNull();
    // Two unlocked axes are not a fusion.
    expect(rows.find((r) => r.baseItemId === AXE)).toBeUndefined();
  });

  it('offers nothing at the top of the ladder', () => {
    const rows = buildFusionRows([piece(HELM, 14), piece(HELM, 14), piece(HELM, 14)], new Set(), new Set(), 14);
    expect(rows).toHaveLength(0);
  });

  it('skips pieces a fusion in flight has claimed', () => {
    const trio = [piece(HELM, 1), piece(HELM, 1), piece(HELM, 1)];
    const rest = [piece(HELM, 1), piece(HELM, 1)];
    const hidden = new Set(trio.map((p) => p.Id));
    const rows = buildFusionRows([...trio, ...rest], new Set(), hidden, 14);

    // Two left is not a fusion; the claimed three are never re-sent.
    expect(rows).toHaveLength(0);
  });
});

describe('the in-flight ledger: the quick second Fuse', () => {
  it('hides a sent fusion until the list stops holding its sacrifices', () => {
    const entry = { target: 1, sacrifices: [2, 3], expiresAt: 10_000 };

    expect([...hiddenByPending([entry], 0)].sort()).toEqual([1, 2, 3]);
    // The stale list (fetched before the commit) still holds them: kept.
    expect(settlePending([entry], new Set([1, 2, 3, 4]), 100)).toHaveLength(1);
    // The fresh list has the target (one rarity up) and not the sacrifices.
    expect(settlePending([entry], new Set([1, 4]), 200)).toHaveLength(0);
  });

  it('gives a refused fusion its pieces back when the entry expires', () => {
    const entry = { target: 1, sacrifices: [2, 3], expiresAt: 10_000 };
    expect(hiddenByPending([entry], 10_000).size).toBe(0);
    expect(settlePending([entry], new Set([1, 2, 3]), 10_001)).toHaveLength(0);
  });
});

describe('fusionFeeCeiling', () => {
  it('is BaseGoldCost * 2.5^tier, rounded up (10,000 and 2.5 since 2026-10-07)', () => {
    expect(fusionFeeCeiling(1)).toBe(25_000);
    expect(fusionFeeCeiling(8)).toBe(Math.ceil(10_000 * Math.pow(2.5, 8)));
  });
});
