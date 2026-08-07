import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Modul: THE CLIENT COPIES NINE OF THE SERVER'S NUMBERS BY HAND, and nothing
// held them together.
//
// Screens have to show a price before the player commits to it, and a rate
// before they choose a node - so the reroll fee, the fusion fee, the village
// cost, the gathering speed curve and the first-clear multiplier all exist
// twice: once as the rule, once as the preview. That is a legitimate design
// (the wire carries state, not formulas) with one failure mode, and it is
// silent: change the rule, and the preview keeps confidently quoting the old
// answer.
//
// It has already happened twice in one week. Mastery became a percentage and
// the gathering screen went on subtracting ticks, so every rate it printed was
// wrong. The fusion fee dropped fivefold and the forge kept quoting the old
// one. Neither broke a test, because every test checked each side against
// itself.
//
// This reads the C# and compares. It is deliberately a REGEX over source
// rather than anything cleverer: the constants are one-liners, the failure it
// prevents is a number changing in one place, and a test that needs a build
// step to run is a test that gets skipped.
const here = dirname(fileURLToPath(import.meta.url));
const serverRoot = join(here, '..', '..', 'server', 'FolkIdle.Server');
const clientRoot = join(here, '..', 'src');

const read = (...parts: string[]) => readFileSync(join(...parts), 'utf8');

/** The first capture of `pattern`, as a number. Fails loudly if absent. */
function num(source: string, pattern: RegExp, what: string): number {
  const match = source.match(pattern);
  if (!match) throw new Error(`could not find ${what} - the pattern needs updating, not deleting`);
  return Number(match[1]);
}

describe('the numbers the client mirrors still match the server', () => {
  it('reroll: base fee and growth per item tier', () => {
    const affixes = read(serverRoot, 'Engine', 'AffixRegistry.cs');
    const forge = read(clientRoot, 'routes', 'Forge.svelte');

    expect(num(forge, /REROLL_BASE_FEE = (\d+)/, 'client reroll base')).toBe(
      num(affixes, /RerollGoldBase = (\d+)L/, 'server reroll base'),
    );
    expect(num(forge, /REROLL_FEE_GROWTH = ([\d.]+)/, 'client reroll growth')).toBe(
      num(affixes, /RerollGoldItemTierGrowth = ([\d.]+)/, 'server reroll growth'),
    );
  });

  it('fusion: base fee and growth per quality tier', () => {
    const splicing = read(serverRoot, 'Domain', 'Economy', 'ForgeSplicingEngine.cs');
    const forge = read(clientRoot, 'routes', 'Forge.svelte');

    expect(num(forge, /FORGE_BASE_FEE = (\d+)/, 'client fusion base')).toBe(
      num(splicing, /BaseGoldCost = (\d+)/, 'server fusion base'),
    );
    expect(num(forge, /FORGE_FEE_GROWTH = ([\d.]+)/, 'client fusion growth')).toBe(
      num(splicing, /Math\.Ceiling\(BaseGoldCost \* Math\.Pow\(([\d.]+),/, 'server fusion growth'),
    );
  });

  it('fusion: the rarity ceiling', () => {
    const splicing = read(serverRoot, 'Domain', 'Economy', 'ForgeSplicingEngine.cs');
    const rarity = read(clientRoot, 'lib', 'ui', 'rarity.ts');

    expect(num(rarity, /MAX_QUALITY_TIER = (\d+)/, 'client max tier')).toBe(
      num(splicing, /MaxQualityTier = (\d+)/, 'server max tier'),
    );
  });

  it('village: gold cost base and growth', () => {
    const village = read(serverRoot, 'Domain', 'Progression', 'VillageManagementEngine.cs');
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    expect(num(commands, /Math\.ceil\((\d+) \* Math\.pow/, 'client village base')).toBe(
      num(village, /BaseUpgradeCost = (\d+)L/, 'server village base'),
    );
    expect(num(commands, /Math\.ceil\(\d+ \* Math\.pow\(([\d.]+),/, 'client village growth')).toBe(
      num(village, /BaseUpgradeCost \* Math\.Pow\(([\d.]+),/, 'server village growth'),
    );
  });

  it('gathering: the tool speed curve, tier for tier', () => {
    const tools = read(serverRoot, 'Domain', 'Shared', 'GatheringToolEngine.cs');
    const gathering = read(clientRoot, 'routes', 'Gathering.svelte');

    const serverTable = [...tools.matchAll(/^\s*(\d+) => (\d+),/gm)].map((m) => [
      Number(m[1]),
      Number(m[2]),
    ]);
    expect(serverTable.length).toBe(10);

    const clientTable = JSON.parse(
      gathering.match(/TOOL_SPEED_PCT = (\[[^\]]+\])/)![1],
    ) as number[];

    for (const [tier, pct] of serverTable) {
      expect(clientTable[tier], `tool tier ${tier}`).toBe(pct);
    }
    // Index 0 is "no tool", which the switch answers with its default.
    expect(clientTable[0]).toBe(0);
  });

  it('gathering: mastery and village production percentages', () => {
    const tools = read(serverRoot, 'Domain', 'Shared', 'GatheringToolEngine.cs');
    const gathering = read(clientRoot, 'routes', 'Gathering.svelte');

    expect(num(gathering, /MASTERY_SPEED_PCT_PER_LEVEL = (\d+)/, 'client mastery pct')).toBe(
      num(tools, /MasterySpeedPctPerLevel = (\d+)/, 'server mastery pct'),
    );
    expect(num(gathering, /VILLAGE_SPEED_PCT_PER_LEVEL = (\d+)/, 'client village pct')).toBe(
      num(tools, /VillageYieldBonusPctPerLevel = (\d+)/, 'server village pct'),
    );
  });

  it('combat: the first-clear boss multiplier', () => {
    const rules = read(serverRoot, 'Domain', 'Combat', 'BossFirstClearRules.cs');
    const combat = read(clientRoot, 'routes', 'Combat.svelte');

    expect(num(combat, /FIRST_CLEAR_HP = (\d+)/, 'client first-clear hp')).toBe(
      num(rules, /FirstClearHpMultiplier = (\d+)/, 'server first-clear hp'),
    );
  });
});
