// Modul: OPENING A CHEST IS A MOMENT, NOT A BUTTON (owner, 2026-10-08).
//
// Pure rules for ChestOpening.svelte, so they are tested without a browser:
// three taps shake the chest, the third one opens it, and the opening clip is
// chosen by what CAME OUT (the server's roll), not by the chest that went in -
// a Rare chest that rolls a Legendary plays the Legendary burst.
//
// The clips are the owner's renders, cut to the strip the chest lives in and
// re-encoded as VP9 WebM WITH ALPHA: the source had a black background, and
// keying it to transparency is what lets the chest stand in the dungeon
// instead of in a black box. Safari cannot draw VP9 alpha, so it gets the
// plain H.264 cut and a `screen` blend (black disappears, the chest is a
// little ghostly - a fallback, not the look). A browser that plays neither
// (Playwright's Chromium has no H.264) falls back to the still and a CSS
// shake; the taps, the open and the reveal still happen.

export const TAPS_TO_OPEN = 3;

/** Shake clip length; the open is triggered off its `ended`, this is only a safety net. */
export const SHAKE_MS = 1460;
/** The shake plays 20% faster than rendered (owner, 2026-10-08). */
export const SHAKE_RATE = 1.25;
export const OPEN_MS = 2460;

export type ChestClip = 'shake' | 1 | 2 | 3 | 4;

const OPEN_NAMES: Record<number, string> = { 1: 'common', 2: 'rare', 3: 'epic', 4: 'legendary' };

/** The two encodings of one clip, under public/chest (served from the root). */
export function chestClip(clip: ChestClip): { webm: string; mp4: string } {
  const name = clip === 'shake' ? 'shake' : `open-${OPEN_NAMES[clip] ?? 'common'}`;
  return { webm: `/chest/${name}.webm`, mp4: `/chest/${name}.mp4` };
}

export const CHEST_IDLE_IMAGE = '/chest/idle.webp';
export const CHEST_BACKGROUND = { landscape: '/chest/bg-pc.jpg', portrait: '/chest/bg-mobile.jpg' } as const;

/**
 * The chest stage's painting for the seasonal event that is on, if it has one
 * (owner, 2026-10-09: the event's version replaces the ordinary one while the
 * event runs). Keyed on the `data-event` attribute eventTheme.ts puts on
 * <html> from the wire, so the server's calendar decides, not this file.
 */
const EVENT_CHEST_BACKGROUNDS: Record<string, { landscape: string; portrait: string }> = {
  samhain: { landscape: '/chest/bg-pc-samhain.jpg', portrait: '/chest/bg-mobile-samhain.jpg' },
};

export function chestBackground(eventKey: string | undefined | null): { landscape: string; portrait: string } {
  return (eventKey && EVENT_CHEST_BACKGROUNDS[eventKey]) || CHEST_BACKGROUND;
}

/**
 * Which encoding this browser should get. WebKit decodes VP9 in recent
 * versions but ignores its alpha plane - it would draw the black box - so a
 * WebKit browser that is not Chromium gets the MP4 even when it could play
 * the WebM.
 */
export function videoFlavour(canPlay: (type: string) => string, userAgent: string): 'webm' | 'mp4' | 'none' {
  const webkitOnly = /AppleWebKit/.test(userAgent) && !/Chrome|Chromium|CriOS|Edg|Android/.test(userAgent);
  if (!webkitOnly && canPlay('video/webm; codecs="vp9"') !== '') return 'webm';
  if (canPlay('video/mp4; codecs="avc1.4D401E"') !== '') return 'mp4';
  return 'none';
}

/** "TAP!" on a touch screen, "CLICK!" with a mouse. */
export function tapWord(coarsePointer: boolean): string {
  return coarsePointer ? 'TAP!' : 'CLICK!';
}

/**
 * The state of one opening. `taps` counts presses; the third sends the open
 * and the chest bursts once both the third shake has played AND the server
 * has answered - whichever comes last.
 */
export type ChestPhase = 'waiting-taps' | 'final-shake' | 'opening' | 'revealed' | 'failed';

export interface ChestOpeningState {
  phase: ChestPhase;
  taps: number;
}

export function initialChestState(): ChestOpeningState {
  return { phase: 'waiting-taps', taps: 0 };
}

/** A press. Returns the next state and whether this press sends the open. */
export function pressChest(state: ChestOpeningState): { next: ChestOpeningState; sendOpen: boolean; shake: boolean } {
  if (state.phase !== 'waiting-taps') return { next: state, sendOpen: false, shake: false };
  const taps = state.taps + 1;
  if (taps >= TAPS_TO_OPEN) return { next: { phase: 'final-shake', taps }, sendOpen: true, shake: true };
  return { next: { phase: 'waiting-taps', taps }, sendOpen: false, shake: true };
}

/** "75% Common · 20% Rare · 4.5% Epic · 0.5% Legendary" from a per-mille row. */
export function oddsLine(permille: readonly number[] | undefined, names: readonly string[]): string {
  if (!permille) return '';
  const parts: string[] = [];
  for (let r = 1; r < permille.length; r++) {
    const value = permille[r] ?? 0;
    if (value <= 0) continue;
    const pct = value / 10;
    parts.push(`${Number.isInteger(pct) ? pct : pct.toFixed(1)}% ${names[r] ?? ''}`.trim());
  }
  return parts.join(' · ');
}
