import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isRareMaterial } from '../src/lib/ui/wikiData';

// The loot-chest reveal colours a rare material in the Rare colour
// (owner, 2026-10-31). The client's answer mirrors
// VillageManagementEngine.IsRareMaterial, so the server's lists are read and
// every name in them must come back rare.
const here = dirname(fileURLToPath(import.meta.url));
const engine = readFileSync(
  join(here, '..', '..', 'server', 'FolkIdle.Server', 'Domain', 'Progression', 'VillageManagementEngine.cs'),
  'utf8',
);

describe('rare materials match the server', () => {
  it('every rare log, ore and fish the server names is rare here', () => {
    const tiers = [...engine.matchAll(/\("(\w+)",\s*"(\w+)",\s*"(\w+)",\s*"(\w+)"\)/g)];
    expect(tiers.length).toBe(5);
    for (const [, log, ore, rareLog, rareOre] of tiers) {
      expect(isRareMaterial(rareLog), rareLog).toBe(true);
      expect(isRareMaterial(rareOre), rareOre).toBe(true);
      expect(isRareMaterial(log), log).toBe(false);
      expect(isRareMaterial(ore), ore).toBe(false);
    }
    const fishBlock = /RareFish = new\(StringComparer\.Ordinal\)\s*\{([^}]*)\}/.exec(engine)?.[1] ?? '';
    const fish = [...fishBlock.matchAll(/"(\w+)"/g)].map((m) => m[1]);
    expect(fish.length).toBe(5);
    for (const f of fish) expect(isRareMaterial(f), f).toBe(true);
    expect(isRareMaterial('sunlit_perch')).toBe(false);
  });
});
