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

  // Modul: found while drawing the skill tree - the node table is a TENTH
  // mirror nobody had noticed. The client carries a per-level figure for each
  // node purely so the panel can say "+3.0% for 2 points" before the point is
  // spent, and the server carries the same numbers as tenths of a percent.
  // Nothing held them together.
  //
  // Twenty of them now, not five: the tree grew boughs and crowns, and each
  // one is another chance for the two tables to drift.
  it('skill tree: the per-level figure of every node', () => {
    const registry = read(serverRoot, 'Engine', 'SkillTreeRegistry.cs');
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    // The C# table is a multi-line array with a trailing comment per entry, so
    // the numbers are pulled line by line rather than by splitting one string.
    const block = registry
      .slice(registry.indexOf('TenthsOfPercentPerLevel'))
      .split('};')[0];
    const tenths = [...block.matchAll(/^\s*(\d+),/gm)].map((m) => Number(m[1]));

    expect(tenths).toHaveLength(20);

    const clientPerLevel = [...commands.matchAll(/perLevel: ([\d.]+)/g)].map((m) => Number(m[1]));
    expect(clientPerLevel).toHaveLength(20);

    // Crowns are qualitative - the server's number is a magnitude the client
    // never renders as a per-level rate, so it carries 0 on purpose. Only the
    // fifteen scaling nodes have to agree.
    for (let node = 0; node < 15; node++) {
      expect(clientPerLevel[node], `node ${node}`).toBeCloseTo(tenths[node] / 10, 5);
    }
    for (let node = 15; node < 20; node++) {
      expect(clientPerLevel[node], `crown ${node} must not claim a per-level rate`).toBe(0);
    }
  });

  it('skill tree: the three caps and the two prices', () => {
    const registry = read(serverRoot, 'Engine', 'SkillTreeRegistry.cs');
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    const pairs: [RegExp, RegExp, string][] = [
      [/SKILL_TREE_ROOT_MAX = (\d+)/, /RootMaxLevel = (\d+)/, 'root cap'],
      [/SKILL_TREE_BOUGH_MAX = (\d+)/, /BoughMaxLevel = (\d+)/, 'bough cap'],
      [/SKILL_TREE_CROWN_MAX = (\d+)/, /CrownMaxLevel = (\d+)/, 'crown cap'],
      [/SKILL_TREE_BOUGH_COST = (\d+)/, /BoughCostPerLevel = (\d+)/, 'bough price'],
      [/SKILL_TREE_CROWN_COST = (\d+)/, /CrownCost = (\d+)/, 'crown price'],
      [/SKILL_TREE_BOUGH_NEEDS_ROOT = (\d+)/, /BoughRequiresRootLevel = (\d+)/, 'bough gate'],
      [/SKILL_TREE_CROWN_NEEDS_BOUGH = (\d+)/, /CrownRequiresBoughLevel = (\d+)/, 'crown gate'],
    ];

    for (const [clientPattern, serverPattern, what] of pairs) {
      expect(num(commands, clientPattern, `client ${what}`), what).toBe(
        num(registry, serverPattern, `server ${what}`),
      );
    }
  });

  // The exclusion rule is the one thing in the tree a player can permanently
  // get wrong, so the two id layouts must agree on which nodes are a pair.
  it('skill tree: both sides agree where the boughs and crowns start', () => {
    const registry = read(serverRoot, 'Engine', 'SkillTreeRegistry.cs');
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    expect(num(registry, /FirstBoughId = (\d+)/, 'server first bough')).toBe(5);
    expect(num(registry, /FirstCrownId = (\d+)/, 'server first crown')).toBe(15);

    // The client hard-codes the same boundaries inside skillRingOf.
    expect(commands).toMatch(/if \(nodeId >= 15\) return 'crown';/);
    expect(commands).toMatch(/if \(nodeId >= 5\) return 'bough';/);
  });

  it('combat: the first-clear boss multiplier', () => {
    const rules = read(serverRoot, 'Domain', 'Combat', 'BossFirstClearRules.cs');
    const combat = read(clientRoot, 'routes', 'Combat.svelte');

    expect(num(combat, /FIRST_CLEAR_HP = (\d+)/, 'client first-clear hp')).toBe(
      num(rules, /FirstClearHpMultiplier = (\d+)/, 'server first-clear hp'),
    );
  });
});
