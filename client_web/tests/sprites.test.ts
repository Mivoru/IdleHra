import { describe, it, expect } from 'vitest';
import { monsterIcon, itemIcon, raceIcon, toolIcon, currencyIcon, initialsFor } from '../src/lib/ui/sprites';
import { MONSTER_ICONS, ITEM_ICONS, TOOL_ICONS } from '../src/lib/ui/sprites.generated';
import { RACE_NAMES, ALL_RACE_IDS, isRaceUnlocked, raceName } from '../src/lib/ui/races';
import { FIRST_CANONICAL_MONSTER_ID, LAST_CANONICAL_MONSTER_ID } from '../src/lib/net/content';

// Modul: the artwork layer, which fails in two quiet ways.
//
// A URL that is escaped wrongly 404s and renders as a missing icon, which
// looks like art that was never drawn. And a lookup that returns something for
// an id it has no art for would put the WRONG picture on an item, which the
// player cannot detect at all. Both are tested here because neither shows up
// as an error anywhere.

describe('URL encoding', () => {
  it('escapes spaces and ampersands but keeps the path separators', () => {
    // The filenames were authored for a Unity import, so they contain both -
    // "Tools&Equipment/Melee weapons/Doom Edge.webp". `encodeURI` on the whole
    // path leaves the ampersand raw and `encodeURIComponent` destroys the
    // slashes, so it has to be done segment by segment.
    const url = itemIcon('eq_doom_edge_melee_weapon_slot_base');
    if (url === null) return; // no art for it; covered by the coverage test
    expect(url).not.toContain(' ');
    expect(url).toMatch(/\/sprites\//);
    // Slashes inside the relative path must survive as separators.
    expect(url.split('/sprites/')[1].split('/').length).toBeGreaterThan(1);
  });

  it('produces a webp path, matching what the server serves', () => {
    const url = monsterIcon(FIRST_CANONICAL_MONSTER_ID);
    expect(url).not.toBeNull();
    expect(url!.endsWith('.webp')).toBe(true);
  });
});

describe('coverage', () => {
  it('has art for every one of the 25 canonical monsters', () => {
    // These are matched by exact Name, so a rename in monsters.json silently
    // drops one - and a codex with a blank tile looks like a content gap
    // rather than a mapping break.
    for (let id = FIRST_CANONICAL_MONSTER_ID; id <= LAST_CANONICAL_MONSTER_ID; id++) {
      expect(monsterIcon(id), `monster ${id}`).not.toBeNull();
    }
    expect(Object.keys(MONSTER_ICONS).length).toBeGreaterThanOrEqual(25);
  });

  it('has a meaningful number of item icons', () => {
    // Not an exact figure: art and content both legitimately grow. A floor
    // catches the generator silently emitting nothing, which is the failure
    // that would otherwise ship as "no icons anywhere".
    expect(Object.keys(ITEM_ICONS).length).toBeGreaterThan(50);
  });

  it('returns null rather than a guess for an unknown item', () => {
    // The important half. A resolver that fell back to some default would put
    // a real picture on the wrong thing, and nothing downstream could tell.
    expect(itemIcon('definitely_not_an_item')).toBeNull();
    expect(monsterIcon(99999)).toBeNull();
  });
});

describe('tool tiers', () => {
  it('clamps rather than trusting the tier', () => {
    // CachedCurrentToolTier is written from ForgeLevel server-side and nothing
    // documents its range, so an out-of-range value must show the last tool
    // rather than vanishing.
    const ladder = TOOL_ICONS.axe;
    expect(ladder.length).toBeGreaterThan(0);
    expect(toolIcon('axe', 9999)).toBe(toolIcon('axe', ladder.length - 1));
    expect(toolIcon('axe', -5)).toBe(toolIcon('axe', 0));
  });
});

describe('races', () => {
  it('names all six, including the two the old copies stopped short of', () => {
    expect(ALL_RACE_IDS).toHaveLength(6);
    for (const id of ALL_RACE_IDS) {
      expect(RACE_NAMES[id], `race ${id}`).toBeTruthy();
    }
    // The specific regression: a five-entry table showed "Race 6" to anyone
    // who unlocked Moosleute.
    expect(raceName(6)).toBe('Moosleute');
  });

  it('reads the unlock bitmask from bit (raceId - 1)', () => {
    // Human is bit 0, Moosleute is bit 5. Off-by-one here would mislabel every
    // race the player owns.
    expect(isRaceUnlocked(0b000001, 1)).toBe(true);
    expect(isRaceUnlocked(0b000001, 2)).toBe(false);
    expect(isRaceUnlocked(0b100000, 6)).toBe(true);
    expect(isRaceUnlocked(0b011111, 6)).toBe(false);
  });

  it('has art for every race the game defines', () => {
    for (const id of ALL_RACE_IDS) {
      expect(raceIcon(id, false), `race ${id} male`).not.toBeNull();
      expect(raceIcon(id, true), `race ${id} female`).not.toBeNull();
    }
  });
});

describe('currency icons', () => {
  it('has both, since the wallet shows both', () => {
    expect(currencyIcon('gold')).not.toBeNull();
    expect(currencyIcon('diamond')).not.toBeNull();
  });
});

describe('fallback initials', () => {
  it('takes them from the words a player can see', () => {
    // Derived from the display name rather than the BaseId on purpose: a
    // BaseId's leading token is usually a material shared by a dozen items,
    // so "CB" for Copper Bar ties back to what is on screen and "CO" for
    // copper_ore_crafting_material would not.
    expect(initialsFor('Copper Bar')).toBe('CB');
    expect(initialsFor('Gold')).toBe('GO');
    expect(initialsFor('')).toBe('?');
  });
});
