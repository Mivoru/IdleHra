// Modul: TIME TO A GOAL, FROM THE GOAL'S OWN COUNTER.
//
// An idle player plans in hours, not in units - "412 / 5,000 kills" does not
// say whether to check back tonight or next week. The tempting way to answer is
// to model the rate (XP curve, kill speed, gathering ticks), which would copy
// three server formulas onto the client: the two-sources-of-truth shape this
// codebase has lost most of its bugs to. Instead this watches the deed's own
// counter move while the page is open and extrapolates THAT. It knows nothing
// about the game, so it cannot drift from it; it only needs two readings.

export interface PaceSample {
  /** Milliseconds. */
  at: number;
  value: number;
}

/** Below this much observed time an extrapolation is noise. */
export const MIN_OBSERVED_MS = 2 * 60_000;

/** Past this, "about 47 days" is not a plan, so nothing is shown. */
export const MAX_ETA_SECONDS = 30 * 24 * 3600;

/**
 * Seconds until `target` at the pace between `first` and `latest`, or null
 * when there is not enough to say: too little time watched, no progress, or a
 * counter that went backwards (a season rollover resets deeds).
 */
export function etaSeconds(first: PaceSample, latest: PaceSample, target: number): number | null {
  const elapsed = latest.at - first.at;
  const gained = latest.value - first.value;
  if (elapsed < MIN_OBSERVED_MS || gained <= 0) return null;
  const remaining = target - latest.value;
  if (remaining <= 0) return 0;
  const seconds = (remaining / gained) * (elapsed / 1000);
  return seconds > MAX_ETA_SECONDS ? null : seconds;
}

/** Deliberately coarse - an estimate written as "1h47" claims a precision it does not have. */
export function formatEta(seconds: number): string {
  if (seconds < 60) return 'under a minute';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `about ${minutes} min`;
  const hours = Math.round(seconds / 3600);
  if (hours < 48) return `about ${hours} h`;
  return `about ${Math.round(seconds / 86400)} days`;
}
