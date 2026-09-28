import { writable } from 'svelte/store';
import { fetchCosmetics, type CosmeticsView } from '../net/cosmetics';

/**
 * Task 54: how many cosmetic chests are waiting to be opened. Drives the dot
 * on the Character tab and the Character screen's link to the Wardrobe.
 * Refreshed at the start of a session (the backfill lands then), whenever a
 * chest arrives on the loot feed, and by the Wardrobe after every action.
 */
export const unopenedChests = writable(0);

export function noteCosmeticsView(view: CosmeticsView | null | undefined): void {
  if (!view) return;
  unopenedChests.set(view.Chests.reduce((a, b) => a + b, 0));
}

let inFlight = false;

export function refreshUnopenedChests(): void {
  if (inFlight) return;
  inFlight = true;
  fetchCosmetics()
    .then(noteCosmeticsView)
    .catch(() => {})
    .finally(() => (inFlight = false));
}
