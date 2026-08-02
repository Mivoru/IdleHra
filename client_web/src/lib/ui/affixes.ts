// Modul: affix display. Mirrors AffixRegistry's key encoding and its twelve
// definitions.
//
// AN AFFIX KEY IS NOT AN AFFIX ID. The payload key is
//
//     id[#stack][@rarity]
//
// so "flat_hp", "flat_hp@4", "flat_hp#2@4" are all valid keys for the same
// definition. `#N` is a stacked duplicate (RollAffixes can roll the same affix
// twice); `@N` is the affix's own five-tier rarity, which drives its magnitude.
// Every reader must strip both before looking anything up.
//
// This was got wrong first time and the browser showed it: keys arrived as
// "crit_dmg_pct@2", the "_pct" suffix test failed against the trailing "@2",
// and a +6.0% critical damage affix rendered as "+60". That is the exact class
// of error this module exists to prevent - one integer field carrying two
// different units, where guessing wrong makes the client lie about what an
// item does.
//
// The two units: AffixScalingLaw.Flat magnitudes are whole points,
// AffixScalingLaw.Percentage magnitudes are TENTHS OF A PERCENT. The
// discriminator is the id's "_pct" suffix, tested AFTER stripping - and it is
// a suffix rather than a "flat_" prefix because the registry contains
// `flat_hp` and `flat_armor` but also `armor_pen_flat`.

export const STACK_SEPARATOR = '#';
export const RARITY_SEPARATOR = '@';

/** AffixRarity. Index 0 is unused - the enum starts at Common = 1. */
export const AFFIX_RARITY_NAMES = ['', 'Common', 'Uncommon', 'Rare', 'Epic', 'Legendary'];

/**
 * A key with no "@" is a legacy affix rolled before per-affix rarity existed.
 * The server reports those as Rare - the middle of the scale - deliberately:
 * the stored magnitude is absolute and never recomputed on read, so calling
 * them Common would misrepresent strong old gear as junk and Legendary would
 * misrepresent junk as trophies.
 */
export const LEGACY_AFFIX_RARITY = 3;

/** Every affix id in AffixRegistry.Definitions, as of 2026-08-02. */
export const KNOWN_AFFIX_IDS: readonly string[] = [
  'flat_hp',
  'flat_armor',
  'armor_pen_flat',
  'melee_dmg_pct',
  'range_dmg_pct',
  'magic_dmg_pct',
  'crit_chance_pct',
  'crit_dmg_pct',
  'attack_speed_pct',
  'lifesteal_pct',
  'block_chance_pct',
  'dodge_chance_pct',
];

export interface ParsedAffixKey {
  /** The bare definition id, with both suffixes removed. */
  id: string;
  /** 1 for the first roll of an affix, 2+ for stacked duplicates. */
  stack: number;
  /** 1-5. Legacy keys with no "@" report as Rare, matching the server. */
  rarity: number;
}

export function parseAffixKey(key: string): ParsedAffixKey {
  let rarity = LEGACY_AFFIX_RARITY;
  let rest = key;

  // Rarity is encoded AFTER any stack suffix, so it must come off first.
  const rarityAt = rest.lastIndexOf(RARITY_SEPARATOR);
  if (rarityAt >= 0) {
    const parsed = Number.parseInt(rest.slice(rarityAt + 1), 10);
    if (Number.isFinite(parsed) && parsed >= 1 && parsed <= 5) rarity = parsed;
    rest = rest.slice(0, rarityAt);
  }

  let stack = 1;
  const stackAt = rest.lastIndexOf(STACK_SEPARATOR);
  if (stackAt >= 0) {
    const parsed = Number.parseInt(rest.slice(stackAt + 1), 10);
    if (Number.isFinite(parsed) && parsed >= 1) stack = parsed;
    rest = rest.slice(0, stackAt);
  }

  return { id: rest, stack, rarity };
}

export function isPercentageAffix(affixId: string): boolean {
  return affixId.endsWith('_pct');
}

/** Formats a magnitude for a BARE id - call parseAffixKey first. */
export function formatAffixValue(affixId: string, magnitude: number): string {
  if (isPercentageAffix(affixId)) return `+${(magnitude / 10).toFixed(1)}%`;
  return `+${magnitude.toLocaleString()}`;
}

export function affixLabel(affixId: string): string {
  return affixId
    .replace(/_pct$/, '')
    .replace(/^flat_/, '')
    .replace(/_flat$/, '')
    .split('_')
    .filter((part) => part.length > 0)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function affixRarityName(rarity: number): string {
  return AFFIX_RARITY_NAMES[rarity] ?? `Rarity ${rarity}`;
}

export interface DisplayAffix {
  key: string;
  label: string;
  value: string;
  rarity: number;
  rarityName: string;
}

/** Turns a raw payload affix map into rows ready to render. */
export function toDisplayAffixes(affixes: Record<string, number>): DisplayAffix[] {
  return Object.entries(affixes).map(([key, magnitude]) => {
    const parsed = parseAffixKey(key);
    return {
      key,
      label: parsed.stack > 1 ? `${affixLabel(parsed.id)} (${parsed.stack})` : affixLabel(parsed.id),
      value: formatAffixValue(parsed.id, magnitude),
      rarity: parsed.rarity,
      rarityName: affixRarityName(parsed.rarity),
    };
  });
}
