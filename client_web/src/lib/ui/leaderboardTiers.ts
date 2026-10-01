// Modul: THE COLOUR OF A RANK, and ONLY the colour.
//
// The server owns what a tier IS - its rank threshold, its name and what it
// pays - in LeaderboardTierRegistry, and sends `TierId` and `TierName` down
// with every board row. This file adds the one thing that is genuinely the
// client's business and cannot live in C#: how it looks.
//
// WHY THE THRESHOLDS ARE NOT HERE. An ordered table that crosses the wire as a
// position is this codebase's most expensive recurring mistake:
// `KNOWN_AFFIX_IDS` mirrored the server's affix ordering by index, ten of its
// twelve entries drifted, and auto-reroll silently stopped working because the
// index it sent meant a different stat on the other side. So nothing here
// re-derives a tier from a rank - the rank-to-tier decision happens once, on
// the server, and arrives already made.
//
// What IS mirrored is the NAME, and only so `serverMirrors.test.ts` can prove
// these entries still line up with the server's table element by element. That
// test is the mechanical guard; this comment is not.

export interface TierStyle {
  /** Positional - must match LeaderboardTierRegistry.Tiers[i].Id. */
  id: number;
  /** Mirrored from the server purely so the mirror test can compare them. */
  name: string;
  /** The name's colour on the board, on the dark (charred oak) theme. */
  color: string;
  /**
   * The same rung on the light (parchment) theme.
   *
   * Modul: the dark ramp vanishes on parchment - Champion #ffd970 and Second
   * #e2e8f0 on #f6edd8 sit near 1.3:1, so the two names the board exists to
   * celebrate were the hardest to read. Hue is kept rung for rung (the way
   * app.css's light rarity ramp does it) and only lightness drops.
   */
  lightColor: string;
  /**
   * How strong the glow is, 0 for none.
   *
   * Modul: it is a GLOW and not a background or a badge because the board is a
   * dense list on a phone, and the thing being decorated is a name somebody is
   * scanning for. Kept subtle on purpose - the lower rungs are barely lit, so
   * the top three actually read as special instead of everything shouting.
   */
  glow: number;
}

/**
 * Indexed by TierId. Order is positional and must not be rearranged - see the
 * mirror test.
 */
export const TIER_STYLES: readonly TierStyle[] = [
  { id: 0, name: 'Champion', color: '#ffd970', lightColor: '#8a6400', glow: 1.0 },
  { id: 1, name: 'Second', color: '#e2e8f0', lightColor: '#4a5568', glow: 0.8 },
  { id: 2, name: 'Third', color: '#e0a06a', lightColor: '#94501a', glow: 0.7 },
  { id: 3, name: 'Top 10', color: '#c084fc', lightColor: '#7e22ce', glow: 0.5 },
  { id: 4, name: 'Top 50', color: '#7dd3fc', lightColor: '#0369a1', glow: 0.35 },
  { id: 5, name: 'Top 100', color: '#6ee7b7', lightColor: '#047857', glow: 0.25 },
  { id: 6, name: 'Top 500', color: '#a3b18a', lightColor: '#556b2f', glow: 0.15 },
  { id: 7, name: 'Top 1000', color: '#9aa39a', lightColor: '#5c635c', glow: 0.1 },
];

/** The style for a tier id, or null for -1 / anything unrecognised. */
export function tierStyle(tierId: number): TierStyle | null {
  if (tierId < 0 || tierId >= TIER_STYLES.length) return null;
  return TIER_STYLES[tierId];
}

/**
 * Inline style for a ranked name: CUSTOM PROPERTIES, not a colour.
 *
 * Modul: returns '' rather than a default colour for an untiered row, so an
 * ordinary player's name keeps the page's own text colour and the decorated
 * ones stand out by contrast. A board where every row is coloured is a board
 * where no row is.
 *
 * The style only DECLARES `--tier-color`, `--tier-color-light` and
 * `--tier-shadow`; the consuming element's `.tiered` rule picks one per theme
 * (Leaderboards.svelte). An inline `color:` cannot be overridden by a
 * prefers-color-scheme rule without !important, which is how the dark ramp
 * ended up on parchment. On an untiered row the properties are undefined, so
 * `color: var(--tier-color)` falls back to inherit.
 */
export function tierNameStyle(tierId: number): string {
  const style = tierStyle(tierId);
  if (style === null) return '';

  // Two shadows rather than one: a tight bright core and a wider soft halo.
  // A single large-radius shadow on text reads as a blur, not a glow.
  const a = (0.55 * style.glow).toFixed(2);
  const b = (0.35 * style.glow).toFixed(2);
  return (
    `--tier-color: ${style.color}; --tier-color-light: ${style.lightColor}; ` +
    `--tier-shadow: 0 0 4px rgba(255,255,255,${a}), 0 0 10px ${style.color}${Math.round(
      Number(b) * 255,
    )
      .toString(16)
      .padStart(2, '0')};`
  );
}
