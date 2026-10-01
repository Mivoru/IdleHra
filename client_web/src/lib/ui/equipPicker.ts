// Modul: THE EQUIP PICKER IS BOUNDED, because what feeds it is not.
//
// Character's slot picker rendered one row per owned piece that fits the
// slot, unwindowed. EquipmentInstances reached 17,836 rows on one live account
// and the dev fixture holds 7,550 Normal Birch Axes - all of them slot 8 - so
// opening the Axe slot built seven thousand rows on a phone's main thread.
// That is the defect the Chest had before it was windowed.
//
// Two cuts, both safe for equip-by-id:
//   1. IDENTICAL pieces collapse into one row with a count. "Identical" means
//      same BaseItemId, quality tier, affix rolls and lock - a player could not
//      tell two of them apart, so offering both is noise. The row keeps the
//      FIRST instance's id, a concrete piece, so Wear still sends a real id.
//   2. At most `limit` rows, best first (the caller sorts). The rest are
//      counted for a "+N more - open the Chest" line, where the list is
//      windowed and filterable.

export interface PickerPiece {
  Id: number;
  BaseItemId: string;
  QualityTier: number;
  Affixes: Record<string, number>;
  IsAffixLocked: boolean;
}

export interface PickerRow<T extends PickerPiece> {
  /** The instance Wear equips - always a real piece, never a synthetic one. */
  piece: T;
  /** How many indistinguishable pieces this row stands for (>= 1). */
  count: number;
}

export const PICKER_ROW_LIMIT = 50;

function identityOf(piece: PickerPiece): string {
  // Sorted, so two maps with the same rolls in a different key order agree.
  const affixes = Object.keys(piece.Affixes ?? {})
    .sort()
    .map((key) => `${key}=${piece.Affixes[key]}`)
    .join(',');
  return `${piece.BaseItemId}|${piece.QualityTier}|${piece.IsAffixLocked ? 1 : 0}|${affixes}`;
}

/** Collapses duplicates (keeping input order) and caps the row count. */
export function pickerRows<T extends PickerPiece>(
  pieces: readonly T[],
  limit: number = PICKER_ROW_LIMIT,
): { rows: PickerRow<T>[]; hiddenRows: number; hiddenPieces: number } {
  const groups = new Map<string, PickerRow<T>>();
  for (const piece of pieces) {
    const key = identityOf(piece);
    const group = groups.get(key);
    if (group) group.count++;
    else groups.set(key, { piece, count: 1 });
  }
  const all = [...groups.values()];
  const rows = all.slice(0, limit);
  const hidden = all.slice(limit);
  return {
    rows,
    hiddenRows: hidden.length,
    hiddenPieces: hidden.reduce((sum, row) => sum + row.count, 0),
  };
}
