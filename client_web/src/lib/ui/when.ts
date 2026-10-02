import { readable } from 'svelte/store';

// Modul: ONE WAY TO SAY WHEN SOMETHING HAPPENS (task 95).
//
// The same server instant - the Monday reset - was "midnight UTC" on the World
// Boss and "Monday, 02:00 CEST" on the Delve. A player in Prague had to know
// that those were one moment, and a player anywhere else had to do timezone
// arithmetic to find out when their strike came back. Every reset text now
// goes through here and reads the same way: the player's own clock, then how
// far away that is - "Mon 02:00 - in 3 d".
//
// en-GB for the weekday, not the device locale: the sentence around it is
// English, and a Czech phone printed "arrives on pondělí" mid-sentence once
// (WorldBoss.svelte). 24-hour time for the same reason - "02:00" cannot be
// misread as afternoon.

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** "in 3 d", "in 5 h", "in 12 min", "now". Rounded DOWN, so it never promises early. */
export function formatRelative(at: Date, now: Date): string {
  const ms = at.getTime() - now.getTime();
  if (ms < MINUTE) return 'now';
  if (ms < HOUR) return `in ${Math.floor(ms / MINUTE)} min`;
  if (ms < DAY) return `in ${Math.floor(ms / HOUR)} h`;
  return `in ${Math.floor(ms / DAY)} d`;
}

/** "Mon 02:00" in the device's own time zone. */
export function formatLocalTime(at: Date): string {
  const day = at.toLocaleDateString('en-GB', { weekday: 'short' });
  const time = at.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false });
  return `${day} ${time}`;
}

/** "Mon 02:00 - in 3 d": the local time, then how far away it is. */
export function formatWhen(at: Date, now: Date = new Date()): string {
  return `${formatLocalTime(at)} - ${formatRelative(at, now)}`;
}

/**
 * The current time, ticking once a minute while anything listens - so a
 * relative "in 5 h" written into a screen moves on without each screen
 * keeping its own interval.
 */
export const minuteClock = readable(new Date(), (set) => {
  const id = setInterval(() => set(new Date()), MINUTE);
  return () => clearInterval(id);
});
