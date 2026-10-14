// Modul: ONE PLACE THAT DECIDES HOW LONG A MOVEMENT LASTS (2026-10-14).
//
// CSS animations here already honour `prefers-reduced-motion` in their own
// media blocks, but a Svelte transition (`transition:fly`) is JavaScript and
// does not - it plays at full length for a player who asked the OS for less
// motion. Every script-driven movement takes its duration from here, so the
// rule is written once and a new transition cannot forget it.
//
// Durations are short on purpose: motion in this client is feedback on
// something the player just did, never decoration that delays the next tap.
import { prefersReducedMotion } from 'svelte/motion';

/** A duration in ms, or 0 when the player has asked for reduced motion. */
export function motionMs(ms: number): number {
  return prefersReducedMotion.current ? 0 : ms;
}

/** The standard feedback durations - a tap answered, a panel arriving. */
export const MOTION = {
  quick: 160,
  enter: 240,
  /** A currency figure counting to its new value (countUp.svelte.ts). */
  count: 450,
  /** The cross-fade between two screens (Game.svelte, app.css). */
  screen: 160,
} as const;
