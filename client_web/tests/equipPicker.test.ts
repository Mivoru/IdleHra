import { describe, it, expect } from 'vitest';
import { pickerRows } from '../src/lib/ui/equipPicker';

const piece = (Id: number, over: Partial<{ BaseItemId: string; QualityTier: number; Affixes: Record<string, number>; IsAffixLocked: boolean }> = {}) => ({
  Id,
  BaseItemId: 'birch_axe',
  QualityTier: 0,
  Affixes: {},
  IsAffixLocked: false,
  ...over,
});

describe('equip picker rows', () => {
  it('collapses identical pieces into one row that still wears a real id', () => {
    // The dev fixture's 7,550 Normal Birch Axes are one row, not 7,550.
    const axes = Array.from({ length: 7550 }, (_, i) => piece(1000 + i));
    const { rows, hiddenRows } = pickerRows(axes);
    expect(rows).toHaveLength(1);
    expect(rows[0].count).toBe(7550);
    expect(rows[0].piece.Id).toBe(1000);
    expect(hiddenRows).toBe(0);
  });

  it('keeps pieces apart when anything a player could see differs', () => {
    const { rows } = pickerRows([
      piece(1),
      piece(2, { QualityTier: 3 }),
      piece(3, { Affixes: { crit_pct: 2 } }),
      piece(4, { Affixes: { crit_pct: 3 } }),
      piece(5, { IsAffixLocked: true }),
      piece(6, { BaseItemId: 'oak_axe' }),
    ]);
    expect(rows).toHaveLength(6);
  });

  it('treats affix maps with the same rolls in another order as identical', () => {
    const { rows } = pickerRows([
      piece(1, { Affixes: { a: 1, b: 2 } }),
      piece(2, { Affixes: { b: 2, a: 1 } }),
    ]);
    expect(rows).toHaveLength(1);
    expect(rows[0].count).toBe(2);
  });

  it('caps the rows in input order and counts what it hid', () => {
    const many = Array.from({ length: 60 }, (_, i) => piece(i, { Affixes: { roll: i } }));
    const { rows, hiddenRows, hiddenPieces } = pickerRows(many, 50);
    expect(rows).toHaveLength(50);
    expect(rows[0].piece.Id).toBe(0);
    expect(hiddenRows).toBe(10);
    expect(hiddenPieces).toBe(10);
  });
});
