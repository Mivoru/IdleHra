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
 *   - Every Rare+ drop: the rare-loot clip, pitched a semitone higher per tier
 *     above Rare, so the ear can tell an Ancient from a Rare without looking.
 *     No new clip - the owner's sound set has one rare-loot sound, and the rule
 *     is to use only clips that exist.
 */
import { MAX_QUALITY_TIER } from './rarity';

export const FLASH_MIN_TIER = 4; // Rare
export const REVEAL_MIN_TIER = 7; // Legendary
export const FLASH_INTERVAL_MS = 5000;
export const REVEAL_VISIBLE_MS = 1500;

/** playbackRate for the rare-loot clip: 1.0 at Rare, +1 semitone per tier. */
export function lootPitch(qualityTier: number): number {
  const steps = Math.max(0, Math.min(qualityTier, MAX_QUALITY_TIER) - FLASH_MIN_TIER);
  return Math.pow(2, steps / 12);
}

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
