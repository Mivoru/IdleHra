import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { StateUpdate } from '../src/lib/net/protocol.generated';
import { lockedRequirement, UNLOCK_RULES, DELVE_FIRST_ENTRY_GOLD } from '../src/lib/ui/unlocks';
import { DISCOVERY_IDS } from '../src/lib/stores/tutorialDiscoveries';

/*
  Task 60: screens unlock as they become useful. A fresh account sees the
  Forge, the Delve, the Market, the Guild and Bloodline greyed; the dev
  fixture (level 40, every building) sees none of them greyed.
*/

function snapshot(fields: Record<string, number> = {}): StateUpdate {
  return {
    CurrentLevel: 1,
    Gold: 0,
    ForgeLevel: 0,
    BreedingLevel: 0,
    HighestUnlockedRegion: 1,
    PremiumCurrencyBalance: 0,
    Slot1_AgePhase: 0,
    Slot2_AgePhase: 0,
    Slot3_AgePhase: 0,
    ...fields,
  } as unknown as StateUpdate;
}

const NO_GUILD = { hasGuild: false };
const NONE = new Set<string>();
const LOCKABLE = ['forge', 'delve', 'market', 'guildops', 'breeding', 'ancestors', 'inheritance'];

describe('screens unlock as they become useful', () => {
  it('greys the five systems for a brand-new account', () => {
    for (const screen of LOCKABLE) {
      expect(lockedRequirement(screen, snapshot(), NO_GUILD, NONE), screen).not.toBeNull();
    }
  });

  it('never greys the screens a new player needs', () => {
    for (const screen of ['hub', 'combat', 'character', 'larder', 'boosts', 'chest', 'village', 'settings', 'wiki']) {
      expect(lockedRequirement(screen, snapshot(), NO_GUILD, NONE), screen).toBeNull();
    }
  });

  it('opens everything for a character like the dev fixture', () => {
    const fixture = snapshot({ CurrentLevel: 40, ForgeLevel: 5, BreedingLevel: 1, Gold: 500_000 });
    for (const screen of LOCKABLE) {
      expect(lockedRequirement(screen, fixture, NO_GUILD, NONE), screen).toBeNull();
    }
  });

  it('greys nothing before the first packet arrives', () => {
    for (const screen of LOCKABLE) {
      expect(lockedRequirement(screen, null, NO_GUILD, NONE)).toBeNull();
    }
  });

  it('opens each screen on its own rule', () => {
    expect(lockedRequirement('forge', snapshot({ CurrentLevel: 5 }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('forge', snapshot({ ForgeLevel: 1 }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('delve', snapshot({ Gold: DELVE_FIRST_ENTRY_GOLD }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('market', snapshot({ CurrentLevel: 10 }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('guildops', snapshot({ CurrentLevel: 10 }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('guildops', snapshot(), { hasGuild: true }, NONE)).toBeNull();
    expect(lockedRequirement('breeding', snapshot({ BreedingLevel: 1 }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('ancestors', snapshot({ Slot1_AgePhase: 2 }), NO_GUILD, NONE)).toBeNull();
    expect(lockedRequirement('inheritance', snapshot({ PremiumCurrencyBalance: 40 }), NO_GUILD, NONE)).toBeNull();
  });

  // Gold goes down and a season resets levels and buildings; a screen whose
  // "new" card was shown must not go grey again.
  it('keeps a screen open once its card has been seen, even after gold is spent', () => {
    expect(lockedRequirement('delve', snapshot({ Gold: 10 }), NO_GUILD, new Set(['delve']))).toBeNull();
    expect(lockedRequirement('market', snapshot({ CurrentLevel: 1 }), NO_GUILD, new Set(['market']))).toBeNull();
  });

  it('keys every rule to discovery moments that exist', () => {
    for (const rule of UNLOCK_RULES) {
      for (const id of rule.discoveries) {
        expect(DISCOVERY_IDS as readonly string[], `${rule.screens[0]} -> ${id}`).toContain(id);
      }
    }
  });

  it('mirrors the server\'s first Delve fee', () => {
    const here = dirname(fileURLToPath(import.meta.url));
    const source = readFileSync(join(here, '..', '..', 'server', 'FolkIdle.Server', 'Engine', 'DelveRegistry.cs'), 'utf8');
    const match = source.match(/EntryFeeByRegion\s*=\s*\{\s*([\d_]+)/);
    expect(match, 'EntryFeeByRegion not found - update the regex, do not delete the check').not.toBeNull();
    expect(Number(match![1].replace(/_/g, ''))).toBe(DELVE_FIRST_ENTRY_GOLD);
  });
});
