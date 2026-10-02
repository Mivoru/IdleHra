// The profile stack (stores/profile.ts): profile -> guild -> member -> back.
import { describe, it, expect, beforeEach } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get } from 'svelte/store';
import {
  pushEntry,
  profileStack,
  openProfile,
  openGuildView,
  popProfile,
  closeProfiles,
  MAX_PROFILE_DEPTH,
  type ProfileEntry,
} from '../src/lib/stores/profile';

const player = (playerId: number): ProfileEntry => ({ kind: 'player', playerId });
const guild = (guildId: number): ProfileEntry => ({ kind: 'guild', guildId });

describe('pushEntry', () => {
  it('stacks a guild on a profile and a member on the guild', () => {
    const s = pushEntry(pushEntry(pushEntry([], player(1)), guild(7)), player(2));
    expect(s).toEqual([player(1), guild(7), player(2)]);
  });

  it('re-opening what is on top changes nothing', () => {
    expect(pushEntry([player(1)], player(1))).toEqual([player(1)]);
  });

  it('re-opening something deeper unwinds to it instead of stacking a copy', () => {
    // profile 1 -> guild 7 -> member 1 (the same player) goes back to profile 1.
    expect(pushEntry([player(1), guild(7)], player(1))).toEqual([player(1)]);
  });

  it('a guild id and a player id that are equal are different entries', () => {
    expect(pushEntry([player(7)], guild(7))).toEqual([player(7), guild(7)]);
  });

  it('never grows past its cap', () => {
    let s: ProfileEntry[] = [];
    for (let i = 1; i <= MAX_PROFILE_DEPTH + 5; i++) s = pushEntry(s, player(i));
    expect(s.length).toBe(MAX_PROFILE_DEPTH);
    expect(s[s.length - 1]).toEqual(player(MAX_PROFILE_DEPTH + 5));
  });
});

describe('the store', () => {
  beforeEach(() => closeProfiles());

  it('opens, pops one level and closes all', () => {
    openProfile(5, 'Ada');
    openGuildView(3, 'Folk');
    expect(get(profileStack).map((e) => e.kind)).toEqual(['player', 'guild']);
    popProfile();
    expect(get(profileStack)).toEqual([{ kind: 'player', playerId: 5, name: 'Ada' }]);
    openGuildView(3);
    closeProfiles();
    expect(get(profileStack)).toEqual([]);
  });

  it('ignores ids that cannot be players or guilds (system chat rows are id 0)', () => {
    openProfile(0);
    openGuildView(-1);
    expect(get(profileStack)).toEqual([]);
  });
});

// Modul: ONE HOST. The per-screen copies are what kept the guild roster from
// ever getting a profile; a screen that mounts its own again is the regression.
describe('the profile modal is mounted once', () => {
  const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
  const files = (dir: string): string[] =>
    readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
      e.isDirectory() ? files(join(dir, e.name)) : e.name.endsWith('.svelte') ? [join(dir, e.name)] : [],
    );

  it('only App.svelte renders <PlayerProfileModal', () => {
    const mounting = files(srcRoot)
      .filter((f) => /<PlayerProfileModal\b/.test(readFileSync(f, 'utf8')))
      .map((f) => relative(srcRoot, f).replace(/\\/g, '/'));
    expect(mounting).toEqual(['App.svelte']);
  });
});
