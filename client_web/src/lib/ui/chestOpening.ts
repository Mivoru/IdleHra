// Modul: OPENING A CHEST IS A MOMENT, NOT A BUTTON (owner, 2026-10-08).
//
// Pure rules for ChestOpening.svelte, so they are tested without a browser:
// three taps shake the chest and the third one opens it.
//
// Modul: THE VIDEO CLIPS ARE GONE (2026-10-31). The opening used to play the
// owner's renders (shake, open-common ... open-legendary, WebM with alpha and
// an H.264 fallback for Safari). Since 2026-10-30 every chest opens on
// ChestStage, which draws the light itself, so the clips, their picker and the
// idle still were deleted together (3 MB of public/chest).

export const TAPS_TO_OPEN = 3;

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
