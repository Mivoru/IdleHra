// Modul: the shield wheel's result list, mirrored (task 36). The server
// exports its WorldBossStrikeResult enum to a fixture (regenerated-and-compared
// by WorldBossChallengeRegistryTests); this compares that list to the client's
// sentences, so neither side can grow a result the other does not know.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WORLD_BOSS_RESULT_SENTENCES, SILENT_WORLD_BOSS_RESULTS } from '../src/lib/game/worldBossResults';

const here = dirname(fileURLToPath(import.meta.url));
const serverResults: string[] = JSON.parse(
  readFileSync(join(here, '..', '..', 'server', 'FolkIdle.Server.Tests', 'Fixtures', 'world_boss_results.json'), 'utf8'),
);

describe('world boss strike results', () => {
  it('knows exactly the results the server can answer', () => {
    expect(Object.keys(WORLD_BOSS_RESULT_SENTENCES).sort()).toEqual([...serverResults].sort());
  });

  it('has a sentence for every result a player should be told about', () => {
    for (const result of serverResults) {
      const sentence = WORLD_BOSS_RESULT_SENTENCES[result as keyof typeof WORLD_BOSS_RESULT_SENTENCES];
      if (SILENT_WORLD_BOSS_RESULTS.has(result as never)) continue;
      expect(sentence, `${result} has no sentence`).toMatch(/\w{3,}/);
    }
  });
});
