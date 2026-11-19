import { writable } from 'svelte/store';
import { fetchNav, type NavState } from '../net/nav';

/**
 * Whether Nav's descents sit at their cap. Drives the dot on the Nav menu
 * entry, like dungeonTickets.ts does for a full gate.
 *
 * Modul: A FULL STACK STOPS REFILLING (2026-11-19). One descent comes back
 * every 20 minutes up to twelve; past the cap nothing is banked, so four hours
 * away is the most a player can be gone without losing runs. The dot says so
 * only when it is true - never for a closed Nav, whose descents cannot be
 * spent anyway.
 */
export const navChargesFull = writable(false);

export function noteNavView(view: NavState | null | undefined): void {
  if (!view) return;
  navChargesFull.set(view.Open && view.Charges >= view.ChargeCap);
}

let inFlight = false;

export function refreshNavCharges(): void {
  if (inFlight) return;
  inFlight = true;
  fetchNav()
    .then(noteNavView)
    .catch(() => navChargesFull.set(false))
    .finally(() => (inFlight = false));
}
