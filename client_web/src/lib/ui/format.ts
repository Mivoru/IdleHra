// Modul: HOW A BIG NUMBER IS WRITTEN DOWN, in one place.
//
// An idle game's numbers grow without bound and are read at a GLANCE - that is
// the whole interaction. `5 042 484` is nine glyphs a player has to count in
// groups before they know whether they can afford a 250,000 gate; `5.04M` is
// five they can read at speed. The dev fixture is sitting on five million gold
// and every screen prints it in full.
//
// WHY THE THRESHOLD IS 100,000 AND NOT A THOUSAND.
//
// Compacting early destroys information a player actually uses. A Delve gate
// costs 17,000 and a reroll 2,000; rendered as "17 k" and "2 k" they would
// lose their real precision. Up to 100,000 the thin-space grouping is still
// doing its job. Above it the digits have to be counted, so it becomes
// "123 k" / "5.04 M" (task 74: this was a million until the sweep made every
// screen go through here; the six-digit band read worse than the compact form).
//
// ONE FORMATTER: every quantity on every screen (gold, diamonds, xp, hp,
// materials, counts) goes through formatNumber. A raw `.toLocaleString()` on a
// quantity is now a bug - it prints "5,042,484" in one place and "5.04 M" in
// the next, and separates by the DEVICE's locale rather than the UI language.
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

import { get } from 'svelte/store';
import { language } from './i18n';

/** Up to and including this, a number is written out in full. */
export const COMPACT_THRESHOLD = 100_000;

/** Narrow no-break space (U+202F, a thin space that never wraps mid-number): the group separator in every UI language. */
export const GROUP_SEPARATOR = '\u202F';

const UNITS = [
  { value: 1e12, suffix: 'T' },
  { value: 1e9, suffix: 'B' },
  { value: 1e6, suffix: 'M' },
  { value: 1e3, suffix: 'k' },
] as const;

// Modul: the decimal mark follows the UI language (the picker in Settings),
// NOT navigator.language - the same reason i18n stopped reading the device.
// The group separator is the thin space everywhere, so only the point moves.
// Read at call time (not subscribed): the language changes from Settings, and
// every screen re-renders from server pushes within a second anyway.
function decimalMark(): string {
  return get(language) === 'En' ? '.' : ',';
}

function toNumber(value: number | bigint | string): number {
  const n = Number(value);
  return Number.isFinite(n) ? n : 0;
}

function group(digits: string): string {
  return digits.replace(/\B(?=(\d{3})+(?!\d))/g, GROUP_SEPARATOR);
}

/** The full figure, rounded to a whole number, thin-space grouped. Never compacted. */
export function formatExact(value: number | bigint | string): string {
  const n = toNumber(value);
  const r = Math.round(Math.abs(n));
  // Rounds to zero: no "-0".
  const sign = n < 0 && r !== 0 ? '-' : '';
  return sign + group(r.toLocaleString('en-US', { useGrouping: false }));
}

/** Whether this value would actually be shortened - i.e. whether the exact figure is worth publishing. */
export function isCompacted(value: number | bigint | string, from: number = COMPACT_THRESHOLD): boolean {
  return Math.abs(toNumber(value)) > from;
}

/**
 * THE number format. Whole numbers with a thin-space group separator up to
 * 100,000, compact above with three significant figures:
 *
 * 99,999 -> "99 999"   100,000 -> "100 000"   100,001 -> "100 k"
 * 34,500 -> "34 500"   1,240,000 -> "1.24 M"   152,100,000 -> "152 M"
 *
 * Three significant figures, trailing zeros dropped ("1 M", not "1.00 M"),
 * because the extra digits imply a precision the compaction threw away.
 * Where compacted, pair it with numberTitle() so the exact value is one hover
 * away; Money.svelte also publishes `data-exact` for machines.
 *
 * `compactFrom` exists for the one place that has no room for six digits: the
 * item-icon quantity badge (compacts above 9,999). Same format, earlier cut.
 */
export function formatNumber(value: number | bigint | string, compactFrom: number = COMPACT_THRESHOLD): string {
  const n = toNumber(value);
  if (!isCompacted(n, compactFrom)) return formatExact(n);

  const abs = Math.abs(n);
  let i = UNITS.findIndex((u) => abs >= u.value);
  let scaled = abs / UNITS[i].value;
  let text = roundSig(scaled);
  // 999,999 -> 1000 k must carry into 1 M.
  if (parseFloat(text) >= 1000 && i > 0) {
    i -= 1;
    scaled = abs / UNITS[i].value;
    text = roundSig(scaled);
  }
  const mark = decimalMark();
  return `${n < 0 ? '-' : ''}${mark === '.' ? text : text.replace('.', mark)}${GROUP_SEPARATOR}${UNITS[i].suffix}`;
}

function roundSig(scaled: number): string {
  const digits = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
  return parseFloat(scaled.toFixed(digits)).toString();
}

/** The `title` for a number: the exact figure when formatNumber shortened it, else undefined (no tooltip). */
export function numberTitle(value: number | bigint | string, compactFrom: number = COMPACT_THRESHOLD): string | undefined {
  return isCompacted(value, compactFrom) ? formatExact(value) : undefined;
}

/**
 * A gold amount as PLAIN TEXT, for the places markup cannot reach - a toast
 * string, a button label - where `<Money>` is not an option.
 *
 * Modul: `${formatNumber(x)}g` was written at 27 sites and is wrong past
 * 100,000: the compactor's own suffix is a letter, so 150,000 read "150 kg"
 * and 1.24M "1.24 Mg" - mass units. Money.svelte solved this for markup by
 * spelling the unit out once the number is compacted; this is the same rule
 * for a string. tests/goldSuffix.test.ts fails on the bare-g pattern.
 */
export function formatGold(value: number | bigint | string): string {
  return isCompacted(value) ? `${formatNumber(value)} gold` : `${formatNumber(value)}g`;
}

/** A non-integer rate ("1.5 per hour"), grouped and decimal-marked like everything else. */
export function formatDecimal(value: number, maxFractionDigits = 1): string {
  const n = toNumber(value);
  const fixed = parseFloat(Math.abs(n).toFixed(maxFractionDigits)).toString();
  const [whole, frac] = fixed.split('.');
  const sign = n < 0 && parseFloat(fixed) !== 0 ? '-' : '';
  return sign + group(whole) + (frac ? decimalMark() + frac : '');
}
