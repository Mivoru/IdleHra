// Modul: THE POTIONS, CLIENT SIDE (herbs and potions, 2026-10-31).
//
// A mirror of PotionRegistry.Table on the server - the screens need each
// potion's effect and length to say what a recipe brews and what a shelf
// holds, and the snapshot carries only ids. tests/potions.test.ts reads
// PotionRegistry.cs and compares every row, so this cannot drift silently:
// a changed magnitude on the server fails the client suite until it is
// changed here too. The server alone decides what a potion does.

/** The three potion slots, as PotionSlot on the server. */
export const POTION_SLOTS = [
  { index: 0, name: 'Rewards', hint: 'more from every kill' },
  { index: 1, name: 'Combat', hint: 'stronger in a fight' },
  { index: 2, name: 'Gathering', hint: 'faster, luckier picking' },
] as const;

export type PotionSlotIndex = 0 | 1 | 2;

export interface PotionRow {
  /** The BaseId stem: `hunt_elixir` in `strong_hunt_elixir_offensive_potion_consumable`. */
  stem: string;
  name: string;
  slot: PotionSlotIndex;
  minutes: number;
  normal: number;
  strong: number;
  /** How a magnitude reads, e.g. `+20% XP from kills`. */
  effect: (magnitude: number) => string;
}

export const POTION_TABLE: readonly PotionRow[] = [
  { stem: 'hunt_elixir', name: 'Elixir of the Hunt', slot: 0, minutes: 60, normal: 20, strong: 30, effect: (m) => `+${m}% XP from kills` },
  { stem: 'fortune_draught', name: 'Draught of Fortune', slot: 0, minutes: 60, normal: 20, strong: 30, effect: (m) => `+${m} loot luck` },
  { stem: 'goldmoss_tonic', name: 'Goldmoss Tonic', slot: 0, minutes: 60, normal: 20, strong: 30, effect: (m) => `+${m}% gold from kills` },
  { stem: 'ironbark_brew', name: 'Ironbark Brew', slot: 1, minutes: 60, normal: 6, strong: 9, effect: (m) => `+${m}% armour` },
  { stem: 'swiftroot_tea', name: 'Swiftroot Tea', slot: 1, minutes: 60, normal: 10, strong: 15, effect: (m) => `+${m} attack speed` },
  { stem: 'bossbane_philtre', name: 'Boss-bane Philtre', slot: 1, minutes: 60, normal: 15, strong: 25, effect: (m) => `+${m}% damage to region bosses` },
  { stem: 'gatherers_draught', name: "Gatherer's Draught", slot: 2, minutes: 120, normal: 25, strong: 40, effect: (m) => `+${m}% gathering speed` },
  { stem: 'kupala_night', name: 'Kupala Night', slot: 2, minutes: 120, normal: 50, strong: 80, effect: (m) => `+${(m / 10).toFixed(1)}% rare find while gathering` },
];

export interface PotionInfo {
  row: PotionRow;
  strong: boolean;
  magnitude: number;
  /** "Strong Elixir of the Hunt". */
  title: string;
  /** "+30% XP from kills for 1 h". */
  summary: string;
}

const BASE_ID = /^(strong_)?([a-z_]+?)_(offensive|defensive|gathering)_potion_consumable$/;

/** What a potion BaseId brews, or null for anything that is not one of ours. */
export function potionInfo(baseId: string | undefined | null): PotionInfo | null {
  if (!baseId) return null;
  const match = BASE_ID.exec(baseId);
  if (!match) return null;
  const row = POTION_TABLE.find((r) => r.stem === match[2]);
  if (!row) return null;
  const strong = match[1] !== undefined;
  const magnitude = strong ? row.strong : row.normal;
  return {
    row,
    strong,
    magnitude,
    title: strong ? `Strong ${row.name}` : row.name,
    summary: `${row.effect(magnitude)} for ${formatMinutes(row.minutes)}`,
  };
}

export function formatMinutes(minutes: number): string {
  return minutes % 60 === 0 ? `${minutes / 60} h` : `${minutes} min`;
}

/** "42:07" or "1:02:07" from a millisecond countdown. */
export function formatCountdown(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = h > 0 ? String(m).padStart(2, '0') : String(m);
  return `${h > 0 ? `${h}:` : ''}${mm}:${String(s).padStart(2, '0')}`;
}

/** The most one shelf holds - LarderLimits.SlotCapacity, as the food slots. */
export const SHELF_CAPACITY = 9999;
