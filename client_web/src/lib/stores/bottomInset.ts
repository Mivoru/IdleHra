/**
 * How much room the page must leave at the bottom for whatever is floating
 * over it.
 *
 * WHY THIS IS SHARED RATHER THAN PER-COMPONENT. Two things pin themselves to
 * the bottom of the viewport - the onboarding coach and the update prompt - and
 * both have to stop content hiding underneath them. Each setting
 * `document.body.style.paddingBottom` itself means the last writer wins: the
 * coach mounts, reserves its height, the update prompt mounts and overwrites
 * with its own, and then whichever UNMOUNTS first clears the padding entirely
 * while the other is still on screen. A control ends up under an overlay, which
 * is precisely what `npm run check:overlap` exists to catch.
 *
 * Reservations are keyed and the largest one wins, so any number of floating
 * things can coexist and each cleans up only after itself.
 */

const reservations = new Map<string, number>();

function apply() {
  if (typeof document === 'undefined') return;

  let largest = 0;
  for (const px of reservations.values()) {
    if (px > largest) largest = px;
  }

  document.body.style.paddingBottom = largest > 0 ? `${largest}px` : '';
}

/**
 * Reserve `pixels` at the bottom of the page under `key`.
 *
 * Callers pass the element's full footprint - its height PLUS how far it sits
 * off the bottom - because an overlay lifted clear of something else still
 * covers everything beneath it. The onboarding coach was short by exactly that
 * offset on phones and buried an Upgrade button.
 */
export function reserveBottom(key: string, pixels: number) {
  reservations.set(key, Math.max(0, Math.round(pixels)));
  apply();
}

/** Give the room back. Safe to call for a key that never reserved. */
export function releaseBottom(key: string) {
  if (!reservations.delete(key)) return;
  apply();
}

/** Test seam: what is currently reserved, largest first. */
export function currentReservations(): Array<[string, number]> {
  return [...reservations.entries()].sort((a, b) => b[1] - a[1]);
}
