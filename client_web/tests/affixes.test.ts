import { describe, it, expect } from 'vitest';
import {
  parseAffixKey,
  isPercentageAffix,
  formatAffixValue,
  affixLabel,
  toDisplayAffixes,
  LEGACY_AFFIX_RARITY,
  KNOWN_AFFIX_IDS,
} from '../src/lib/ui/affixes';

// Modul: this file exists because the first version was wrong in production
// and the browser caught it. Real payload keys arrive as "crit_dmg_pct@2" -
// affix id, then rarity - and a "_pct" suffix test run against the UNSTRIPPED
// key fails, so a +6.0% critical-damage affix rendered as "+60".
//
// That is not cosmetic. One integer field carries two units (whole points for
// flat affixes, TENTHS OF A PERCENT for percentage ones), so getting the
// discriminator wrong makes the client lie about what an item does.

describe('parseAffixKey', () => {
  it('parses a bare id', () => {
    expect(parseAffixKey('flat_hp')).toEqual({ id: 'flat_hp', stack: 1, rarity: LEGACY_AFFIX_RARITY });
  });

  it('parses the rarity suffix', () => {
    expect(parseAffixKey('flat_hp@4')).toEqual({ id: 'flat_hp', stack: 1, rarity: 4 });
  });

  it('parses a stacked duplicate', () => {
    expect(parseAffixKey('flat_hp#2')).toEqual({ id: 'flat_hp', stack: 2, rarity: LEGACY_AFFIX_RARITY });
  });

  it('parses both, in the documented order (rarity AFTER stack)', () => {
    expect(parseAffixKey('flat_hp#2@4')).toEqual({ id: 'flat_hp', stack: 2, rarity: 4 });
  });

  it('reports a legacy key as Rare, matching the server', () => {
    // Deliberate on the server's part: magnitudes are absolute and never
    // recomputed on read, so Common would misrepresent strong old gear as junk
    // and Legendary would misrepresent junk as a trophy.
    expect(parseAffixKey('crit_dmg_pct').rarity).toBe(3);
  });

  it('ignores an out-of-range or unparseable rarity rather than trusting it', () => {
    expect(parseAffixKey('flat_hp@9').rarity).toBe(LEGACY_AFFIX_RARITY);
    expect(parseAffixKey('flat_hp@x').rarity).toBe(LEGACY_AFFIX_RARITY);
  });

  it('recovers the bare id for every known affix, suffixed or not', () => {
    for (const id of KNOWN_AFFIX_IDS) {
      expect(parseAffixKey(id).id).toBe(id);
      expect(parseAffixKey(`${id}@5`).id).toBe(id);
      expect(parseAffixKey(`${id}#3`).id).toBe(id);
      expect(parseAffixKey(`${id}#3@5`).id).toBe(id);
    }
  });
});

describe('unit selection', () => {
  it('treats every _pct affix as tenths of a percent', () => {
    // Derived rather than a literal count: KNOWN_AFFIX_IDS is the server's
    // registry order and grows when the registry does (it gained the three
    // tool affixes when the auto-reroll index mismatch was fixed). Pinning a
    // number here made a correct registry change look like a regression.
    const percentage = KNOWN_AFFIX_IDS.filter((id) => id.endsWith('_pct'));
    const flat = KNOWN_AFFIX_IDS.filter((id) => !id.endsWith('_pct'));
    expect(flat).toEqual(['flat_hp', 'flat_armor', 'armor_pen_flat']);
    expect(percentage).toHaveLength(KNOWN_AFFIX_IDS.length - flat.length);
    for (const id of percentage) expect(isPercentageAffix(id)).toBe(true);
  });

  it('treats the three flat affixes as whole points', () => {
    // armor_pen_flat is the one that breaks a "flat_" PREFIX rule, which is
    // why the discriminator is the _pct suffix instead.
    for (const id of ['flat_hp', 'flat_armor', 'armor_pen_flat']) {
      expect(isPercentageAffix(id)).toBe(false);
    }
  });

  it('renders the exact case that shipped wrong', () => {
    // The screenshot said "+60" for what the server means as +6.0%.
    const parsed = parseAffixKey('crit_dmg_pct@2');
    expect(formatAffixValue(parsed.id, 60)).toBe('+6.0%');
  });

  it('renders flat affixes as whole points', () => {
    // Grouped by the viewer's locale, so the expectation is derived rather
    // than hardcoded - "+1,250" and "+1 250" are both correct depending on
    // where the browser is, and pinning one makes this test fail on a Czech
    // machine for no reason.
    expect(formatAffixValue('flat_hp', 1250)).toBe(`+${(1250).toLocaleString()}`);
    expect(formatAffixValue('armor_pen_flat', 3)).toBe('+3');
  });
});

describe('affixLabel', () => {
  it('strips the unit markers rather than showing them to the player', () => {
    expect(affixLabel('crit_dmg_pct')).toBe('Crit Dmg');
    expect(affixLabel('flat_hp')).toBe('Hp');
    expect(affixLabel('armor_pen_flat')).toBe('Armor Pen');
  });
});

describe('toDisplayAffixes', () => {
  it('renders a real payload map correctly', () => {
    const rows = toDisplayAffixes({ 'crit_dmg_pct@2': 60, 'magic_dmg_pct@1': 19, 'flat_hp@5': 800 });

    expect(rows.map((r) => r.value)).toEqual(['+6.0%', '+1.9%', '+800']);
    expect(rows.map((r) => r.rarity)).toEqual([2, 1, 5]);
    expect(rows.map((r) => r.rarityName)).toEqual(['Uncommon', 'Common', 'Legendary']);
  });

  it('marks a stacked affix so two identical rows are distinguishable', () => {
    const rows = toDisplayAffixes({ 'flat_hp@3': 100, 'flat_hp#2@3': 90 });
    expect(rows[0].label).toBe('Hp');
    expect(rows[1].label).toBe('Hp (2)');
  });
});

// Modul: THE DISPLAYED LIST IS THE REROLL COMMAND'S ARGUMENT.
//
// The reroll addresses an affix by INDEX - the packet carries a position, not
// an affix id - and AffixRerollEngine builds the list it indexes into while
// SKIPPING "is_affix_locked". toDisplayAffixes kept every key, so on any item
// carrying that flag the two lists were off by one and a player selecting the
// first affix rerolled the second.
//
// Reported twice, both times as an affix "jumping in" that the player had not
// chosen. Two independent bugs produced that one symptom; fixing the server's
// ordering left this.
describe('toDisplayAffixes membership matches the server', () => {
  it('skips is_affix_locked so the indices line up', () => {
    const rows = toDisplayAffixes({
      is_affix_locked: 1,
      'lifesteal_pct@3': 25,
      'crit_chance_pct@2': 40,
    } as unknown as Record<string, number>);

    expect(rows).toHaveLength(2);
    expect(rows[0].key).toBe('lifesteal_pct@3');
    expect(rows[1].key).toBe('crit_chance_pct@2');
  });

  it('keeps payload order, because index 0 must mean the same thing on both sides', () => {
    const rows = toDisplayAffixes({
      'flat_armor@1': 6,
      'lifesteal_pct@3': 25,
      'crit_dmg_pct@2': 40,
    });

    expect(rows.map((r) => r.key)).toEqual([
      'flat_armor@1',
      'lifesteal_pct@3',
      'crit_dmg_pct@2',
    ]);
  });

  it('ignores anything that is not a numeric magnitude', () => {
    const rows = toDisplayAffixes({
      'flat_armor@1': 6,
      some_flag: true,
    } as unknown as Record<string, number>);

    expect(rows).toHaveLength(1);
    expect(rows[0].key).toBe('flat_armor@1');
  });
});
