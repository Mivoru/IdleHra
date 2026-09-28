// Modul: TASK 54 - the client's cosmetic mirrors, compared to the server's own
// source (the serverMirrors.test.ts approach: read the C#, never retype it).
// The client keeps three things the server also knows - the result names, the
// chest drop kind, and one drawing per frame id - and each is checked here.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { COSMETIC_RESULT_SENTENCES } from '../src/lib/net/cosmetics';
import { FRAME_DRAWINGS } from '../src/lib/ui/frames';
import { MONSTER_ICONS } from '../src/lib/ui/sprites.generated';

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, '..', '..', 'server', 'FolkIdle.Server');
const read = (...parts: string[]) => readFileSync(join(server, ...parts), 'utf8');

const engine = read('Domain', 'Progression', 'CosmeticEngine.cs');
const registry = read('Domain', 'Progression', 'CosmeticRegistry.cs');
const packet = read('Network', 'ResponseLootDropPacket.cs');
const gameStore = readFileSync(join(here, '..', 'src', 'lib', 'stores', 'game.ts'), 'utf8');

function slug(name: string): string {
  return name.toLowerCase().replace(/ /g, '_');
}

/** The string arrays of one pool block, e.g. FramePools or AvatarPools. */
function poolNames(block: string): string[] {
  const start = registry.indexOf(block);
  expect(start, `${block} not found in CosmeticRegistry.cs`).toBeGreaterThan(0);
  const end = registry.indexOf('};', start);
  return [...registry.slice(start, end).matchAll(/"([^"]+)"/g)].map((m) => m[1]);
}

describe('task 54 cosmetics mirrors', () => {
  it('knows exactly the results the server can answer', () => {
    const body = engine.slice(engine.indexOf('public enum CosmeticResult'), engine.indexOf('}', engine.indexOf('public enum CosmeticResult')));
    const names = [...body.matchAll(/^\s*(\w+)\s*(?:=\s*\d+)?,?\s*$/gm)].map((m) => m[1]);
    expect(names.length).toBeGreaterThan(2);
    expect(Object.keys(COSMETIC_RESULT_SENTENCES).sort()).toEqual([...names].sort());
    for (const name of names) {
      if (name === 'Ok') continue;
      expect(COSMETIC_RESULT_SENTENCES[name as keyof typeof COSMETIC_RESULT_SENTENCES]).toMatch(/\w{3,}/);
    }
  });

  it('reads the chest drop kind the server sends', () => {
    const server = packet.match(/DropKindCosmeticChest = (\d+);/);
    const client = gameStore.match(/DROP_KIND_COSMETIC_CHEST = (\d+);/);
    expect(server?.[1]).toBeDefined();
    expect(client?.[1]).toBe(server?.[1]);
  });

  it('draws every frame the server can hand out, and no other', () => {
    const frames = poolNames('FramePools').map((n) => `frame_${slug(n)}`);
    expect(frames).toHaveLength(16);
    expect(Object.keys(FRAME_DRAWINGS).sort()).toEqual([...frames].sort());
  });

  it('has a portrait for every avatar', () => {
    const avatars = poolNames('AvatarPools');
    expect(avatars).toHaveLength(25);
    const files = new Set(Object.values(MONSTER_ICONS).map((p) => p.split('/').pop()));
    for (const monster of avatars) {
      expect(files.has(`${monster}.webp`), `${monster} has no portrait`).toBe(true);
    }
  });
});
