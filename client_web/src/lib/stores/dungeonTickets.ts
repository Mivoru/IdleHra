import { writable } from 'svelte/store';
import { fetchDungeon, type DungeonView } from '../net/dungeon';

/**
 * Whether any open gate sits at its ticket cap. Drives the dot on the Dungeon
 * menu entry, its menu group and the phone's More tab.
 *
 * Modul: A FULL GATE WASTES TICKETS (2026-11-01). Measured in production the
 * day the dungeons opened to everyone: all 13 gate rows of the 5 players who
 * had looked sat at 4/4, so every daily refill was thrown away. A ticket past
 * the cap is not banked, so a full gate is the one moment the screen is worth
 * a nudge. Refreshed at the start of a session, every ten minutes (the refill
 * lands at midnight UTC), and by the Dungeon screen whenever its view changes.
 */
export const dungeonTicketsFull = writable(false);

export function isAnyGateFull(view: DungeonView | null | undefined): boolean {
  if (!view || !view.Open) return false;
  return view.Dungeons.some((d) => d.Tickets >= view.TicketCap);
}

export function noteDungeonView(view: DungeonView | null | undefined): void {
  if (!view) return;
  dungeonTicketsFull.set(isAnyGateFull(view));
}

let inFlight = false;

export function refreshDungeonTickets(): void {
  if (inFlight) return;
  inFlight = true;
  fetchDungeon()
    .then(noteDungeonView)
    .catch(() => {})
    .finally(() => (inFlight = false));
}
