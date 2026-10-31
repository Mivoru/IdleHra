/*
 * Modul: TASK 50 - A DROP WORTH HAVING LOOKS LIKE ONE. The audit's game-feel
 * section: a Legendary arrived as one more row. What a drop earns, decided in
 * one pure place so the thresholds are tested rather than scattered:
 *
 *   - Rare and up (tier 4+): a burst on its loot row, at most one every 5 s,
 *     because an effect that fires constantly is an effect on nothing - the
 *     same reason Burst.svelte keeps itself for rare things.
 *   - Legendary and up (tier 7+): a card over the screen for about 1.5 s, with
 *     Wear on it.
 *   - Every Rare+ drop: a loot clip by BAND (Rare-Epic, Legendary-Mythic,
 *     Relic and up), so the ear can tell an Ancient from a Rare without
 *     looking. It used to be one clip pitched a semitone per tier, which made
 *     the best drops the shrillest (owner, 2026-10-31); see LootSoundGate.
 */
import { MAX_QUALITY_TIER } from './rarity';

export const FLASH_MIN_TIER = 4; // Rare
export const REVEAL_MIN_TIER = 7; // Legendary
export const FLASH_INTERVAL_MS = 5000;
export const REVEAL_VISIBLE_MS = 1500;

export type LootClip = 'lootRare' | 'lootLegendary' | 'lootRelic';

/** Relic is tier 9 - the same floor the server's News announcement uses. */
export const RELIC_TIER = 9;

/** The loot clip for a quality tier, or null below Rare. */
export function lootClipFor(qualityTier: number): LootClip | null {
  const tier = Math.min(qualityTier, MAX_QUALITY_TIER);
  if (tier < FLASH_MIN_TIER) return null;
  if (tier >= RELIC_TIER) return 'lootRelic';
  if (tier >= REVEAL_MIN_TIER) return 'lootLegendary';
  return 'lootRare';
}

/** How long a burst of drops is gathered before its one sound is chosen. */
export const LOOT_SOUND_WINDOW_MS = 350;
/** After a loot sound, only a BETTER band may sound again within this. */
export const LOOT_SOUND_COOLDOWN_MS = 2500;

/**
 * Modul: A BURST OF DROPS IS ONE SOUND (owner, 2026-10-31). An offline
 * catch-up delivers dozens of drops in the same second, and each one used to
 * start its own clip. The gate collects the best tier of a short window and
 * answers it once; inside the cooldown that follows, only a higher band than
 * the last one heard gets through. Pure - the caller owns the timer.
 */
export class LootSoundGate {
  private pendingTier = 0;
  private lastBand = 0;
  private lastPlayedAt = Number.NEGATIVE_INFINITY;

  /** Records a drop; true when the caller should open a window (none is open). */
  offer(qualityTier: number): boolean {
    if (lootClipFor(qualityTier) === null) return false;
    const opens = this.pendingTier === 0;
    this.pendingTier = Math.max(this.pendingTier, qualityTier);
    return opens;
  }

  /** Closes the window: the clip to play now, or null. */
  flush(nowMs: number): LootClip | null {
    const tier = this.pendingTier;
    this.pendingTier = 0;
    const clip = lootClipFor(tier);
    if (clip === null) return null;
    const band = BANDS.indexOf(clip) + 1;
    if (nowMs - this.lastPlayedAt < LOOT_SOUND_COOLDOWN_MS && band <= this.lastBand) return null;
    this.lastBand = band;
    this.lastPlayedAt = nowMs;
    return clip;
  }
}

const BANDS: readonly LootClip[] = ['lootRare', 'lootLegendary', 'lootRelic'];

export interface FeelDecision {
  flash: boolean;
  reveal: boolean;
}

/**
 * Stateful only in the one thing it must remember: when the last row burst
 * fired. The reveal is not throttled here - a better drop replaces a showing
 * card (see `shouldReplaceReveal`), and the card times itself out.
 */
export class LootFeelGate {
  private lastFlashAt = Number.NEGATIVE_INFINITY;

  accept(qualityTier: number, dropKind: number, nowMs: number): FeelDecision {
    if (dropKind !== 1 || qualityTier < FLASH_MIN_TIER) return { flash: false, reveal: false };
    const flash = nowMs - this.lastFlashAt >= FLASH_INTERVAL_MS;
    if (flash) this.lastFlashAt = nowMs;
    return { flash, reveal: qualityTier >= REVEAL_MIN_TIER };
  }
}

/** An offline catch-up can land several Legendaries at once: show the best. */
export function shouldReplaceReveal(showingTier: number | null, incomingTier: number): boolean {
  return showingTier === null || incomingTier >= showingTier;
}
