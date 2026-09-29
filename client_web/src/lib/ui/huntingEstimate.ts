// Task 78: the one line a Combat row shows under a monster. Only wording lives
// here. The numbers are the server's (HuntingProjection) and nothing on this
// side recomputes them - a client-side kill time would be the third damage
// model this codebase has had to delete.

import type { HuntingEstimate } from '../net/rest';
import { formatNumber } from './format';

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
  return `Estimate: ${killTimeText(e)} a kill · ${formatNumber(e.XpPerHour)} XP/h · ${formatNumber(e.GoldPerHour)} g/h · ${safety(e).text}`;
}
