// Modul: THE CHEST AS STILL ART MOVED IN CODE (LOOT_DUNGEON_VISUALS.md
// section 2). The video chest had three faults no encoding fixes: alpha that
// Safari cannot draw, a keyed edge that shimmers, and a jump between the
// shake clip and the open clip. Two stills - closed and open, the open one
// painted FROM the closed one so they are the same chest - and transforms and
// opacity on top of them have none of those.
//
// Pure rules for ChestStage.svelte, tested without a browser.
//
// No lid layer: a lid drawn on its own never sat on its body at the same
// camera, so the hand-over is a light burst that covers the swap.

export type ChestArt = 'cosmetic' | 'loot';
/** 1 Common, 2 Rare, 3 Epic, 4 Legendary - the same scale as ChestClip. */
export type ChestRarity = 1 | 2 | 3 | 4;

const RARITY_NAMES: Record<ChestRarity, string> = { 1: 'common', 2: 'rare', 3: 'epic', 4: 'legendary' };

export function clampRarity(value: number): ChestRarity {
  const r = Math.round(value);
  return (r < 1 ? 1 : r > 4 ? 4 : r) as ChestRarity;
}

/** The two stills of one chest, under public/chest/stage. */
export function chestStills(art: ChestArt, rarity: ChestRarity): { closed: string; open: string } {
  const stem = art === 'cosmetic' ? 'cosmetic' : `loot-${RARITY_NAMES[rarity]}`;
  return { closed: `/chest/stage/${stem}-closed.webp`, open: `/chest/stage/${stem}-open.webp` };
}

/**
 * The light of each rarity, as an RGB triple the canvas tints with. Common is
 * a warm white rather than grey, so even the plainest chest opens in light.
 * Saturated on 2026-10-30 (owner): the pale tints read as one white light.
 */
export const RARITY_LIGHT: Record<ChestRarity, readonly [number, number, number]> = {
  1: [255, 226, 170],
  2: [70, 170, 255],
  3: [180, 90, 255],
  4: [255, 170, 40],
};

/**
 * One shake per tap, harder each time: rotation in degrees and how long it
 * takes. The third is the one the open waits behind.
 */
export function shakeFor(tap: number): { degrees: number; ms: number } {
  const t = Math.max(1, Math.min(3, Math.round(tap)));
  return { degrees: 3 + t * 2.5, ms: 380 + t * 90 };
}

/** The burst: the flash peaks here, and the open still is shown at the peak. */
export const BURST_PEAK_MS = 180;
export const BURST_MS = 900;

/**
 * Particles for one burst. Rarer chests throw more; "reduce motion" throws
 * none, and a low-end device (few cores) throws a third.
 */
export function particleCount(rarity: ChestRarity, reduceMotion: boolean, cores: number): number {
  if (reduceMotion) return 0;
  const base = [0, 24, 40, 64, 110][rarity];
  return cores > 0 && cores <= 4 ? Math.round(base / 3) : base;
}

/** Light rays behind the open chest: more and longer for rarer ones. */
export function rayCount(rarity: ChestRarity): number {
  return [0, 8, 10, 12, 16][rarity];
}
