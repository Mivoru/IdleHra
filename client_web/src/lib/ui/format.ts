// Modul: HOW A BIG NUMBER IS WRITTEN DOWN, in one place.
//
// An idle game's numbers grow without bound and are read at a GLANCE - that is
// the whole interaction. `5 042 484` is nine glyphs a player has to count in
// groups before they know whether they can afford a 250,000 gate; `5.04M` is
// five they can read at speed. The dev fixture is sitting on five million gold
// and every screen prints it in full.
//
// WHY THE THRESHOLD IS A MILLION AND NOT A THOUSAND.
//
// Compacting early destroys information a player actually uses. A Delve gate
// costs 17,000 and a reroll 2,000; rendered as "17.0K" and "2.0K" those two
// stop being comparable at a glance, and the trailing zero is a lie about
// precision. Six digits and under are still readable in groups - the separator
// is doing its job there. Seven and up are not, and that is where this starts.
//
// WHY THE EXACT VALUE NEVER GOES AWAY.
//
// Every compacted number carries `data-exact` and a `title`. A player who wants
// the real figure hovers; a CHECKER that needs it reads the attribute instead
// of parsing the display. That distinction is load-bearing here: exercise.mjs
// pulls numbers straight out of the DOM in four places, and its mastery-XP
// regex already carries a comment about breaking "once a number passes a
// thousand". A display format that tests parse is a display format that cannot
// be changed, so the exact value is published as data rather than inferred
// from text.

/** Below this, a number is written out in full with locale separators. */
export const COMPACT_THRESHOLD = 1_000_000;

const UNITS = [
  { value: 1e12, suffix: 'T' },
  { value: 1e9, suffix: 'B' },
  { value: 1e6, suffix: 'M' },
] as const;

/** The full figure, with this locale's group separators. Never compacted. */
export function formatExact(value: number | bigint | string): string {
  const n = typeof value === 'bigint' ? value : Number(value);
  if (typeof n === 'number' && !Number.isFinite(n)) return '0';
  return n.toLocaleString();
}

/**
 * The figure as a player should read it: full up to a million, then compacted
 * to three significant figures.
 *
 * 999,999 -> "999,999"   1,000,000 -> "1M"   5,042,484 -> "5.04M"
 * 152,100,000 -> "152M"  1,240,000,000 -> "1.24B"
 *
 * Modul: THREE SIGNIFICANT FIGURES, not a fixed decimal count. "1.00M" and
 * "152.10M" carry the same information as "1M" and "152M" while being longer
 * and implying a precision the compaction has already thrown away. Trailing
 * zeros after the point are dropped for the same reason.
 */
export function formatCompact(value: number | bigint | string): string {
  const n = Number(value);
  if (!Number.isFinite(n)) return '0';

  const abs = Math.abs(n);
  if (abs < COMPACT_THRESHOLD) return formatExact(n);

  for (const unit of UNITS) {
    if (abs < unit.value) continue;

    const scaled = abs / unit.value;
    // 3 significant figures: 5.04, 15.2, 152. parseFloat drops the trailing
    // zeros toFixed leaves behind, so 1.00 becomes 1.
    const digits = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
    const body = parseFloat(scaled.toFixed(digits)).toString();
    return `${n < 0 ? '-' : ''}${body}${unit.suffix}`;
  }

  return formatExact(n);
}

/** Whether this value would actually be shortened - i.e. whether the exact figure is worth publishing. */
export function isCompacted(value: number | bigint | string): boolean {
  const n = Number(value);
  return Number.isFinite(n) && Math.abs(n) >= COMPACT_THRESHOLD;
}
