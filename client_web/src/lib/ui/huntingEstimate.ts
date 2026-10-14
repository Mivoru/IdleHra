// Task 78: the one line a Combat row shows under a monster. Only wording lives
// here. The numbers are the server's (HuntingProjection) and nothing on this
// side recomputes them - a client-side kill time would be the third damage
// model this codebase has had to delete.

import type { HuntingEstimate } from '../net/rest';
import { formatGold, formatNumber } from './format';

function seconds(s: number): string {
  if (s >= 90) return `${Math.round(s / 60)} min`;
  return s >= 10 ? `${Math.round(s)} s` : `${s.toFixed(1)} s`;
}

/** "8-11 s", or one figure when the band has collapsed onto it. */
export function killTimeText(e: HuntingEstimate): string {
  const low = seconds(e.SecondsPerKillLow);
  const high = seconds(e.SecondsPerKillHigh);
  return low === high ? low : `${low.replace(/ (s|min)$/, '')}-${high}`;
}

export type SafetyTone = 'safe' | 'food' | 'danger';

/** Whether farming it holds, in the player's terms. */
export function safety(e: HuntingEstimate): { tone: SafetyTone; text: string } {
  if (e.SurvivesWithoutFood) return { tone: 'safe', text: 'safe' };
  if (e.SurvivesWithFood) {
    const bites = Math.ceil(e.FoodPerHour);
    return { tone: 'food', text: `needs food (~${formatNumber(bites)}/h)` };
  }
  return { tone: 'danger', text: 'you would die' };
}

/** The whole line, for a title attribute and for tests. */
export function estimateLine(e: HuntingEstimate): string {
  if (!e.CanDamage) return 'Estimate: you cannot hurt it yet';
  return `Estimate: ${killTimeText(e)} a kill · ${formatNumber(e.XpPerHour)} XP/h · ${formatGold(e.GoldPerHour)}/h · ${safety(e).text}`;
}

/**
 * The one monster the Combat screen recommends: the most XP an hour among the
 * open monsters this character farms WITHOUT food. Null when none qualifies.
 *
 * Modul: ONE MAIN ACTION PER SCREEN (design wave 2, 2026-10-14). Fifteen
 * filled Fight buttons are fifteen equal answers to "what should I do", and
 * the server already sends the numbers that answer it. Food-dependent fights
 * are left out on purpose - "best" has to mean "go and leave it running",
 * and a fight that empties the larder is not that. Ties keep the earlier
 * (easier) monster, so the pick does not flicker between two equal rows.
 */
export function bestPick(estimates: Iterable<HuntingEstimate>, open: (monsterId: number) => boolean): number | null {
  let best: HuntingEstimate | null = null;
  for (const e of estimates) {
    if (!e.CanDamage || !e.SurvivesWithoutFood || !open(e.MonsterId)) continue;
    if (best === null || e.XpPerHour > best.XpPerHour) best = e;
  }
  return best?.MonsterId ?? null;
}
