import { writable } from 'svelte/store';

/**
 * Cross-screen navigation with a payload.
 *
 * H6: the affix reroll lives in Forge, and a player looking for it did not
 * find it - which is the expected outcome, because the thing being rerolled is
 * an item and items are in the Chest. Nothing in the Chest mentioned reroll at
 * all, so finding it required already knowing where it was.
 *
 * A store rather than another `onNavigate` prop because App.svelte holds
 * `screen` as local state and only Hub receives a way to change it. Threading a
 * prop down every screen to give one button a destination is more plumbing than
 * the button is worth, and the next cross-screen link would need it again.
 *
 * `requestScreen` is a counter-stamped request rather than a plain value: two
 * consecutive requests for the SAME screen must both take effect (send an item
 * to the Forge, go back, send another), and a store holding just the key would
 * not notify on the second.
 */
export interface ScreenRequest {
  screen: string;
  /** Set when the destination should open focused on one thing. */
  focusEquipmentId?: number;
  /** Bumped per request so identical destinations still fire. */
  nonce: number;
}

const requests = writable<ScreenRequest | null>(null);

let nonce = 0;

export const screenRequest = { subscribe: requests.subscribe };

export function requestScreen(screen: string, options: { focusEquipmentId?: number } = {}): void {
  nonce += 1;
  requests.set({ screen, nonce, ...options });
}

/**
 * Read and clear the pending focus target. Consumed once: a player who
 * navigates to the Forge again by hand should not have the previous item
 * silently re-selected.
 */
let pendingFocusEquipmentId = 0;

export function setPendingFocusEquipment(id: number): void {
  pendingFocusEquipmentId = id;
}

export function takePendingFocusEquipment(): number {
  const id = pendingFocusEquipmentId;
  pendingFocusEquipmentId = 0;
  return id;
}
