import { describe, it, expect } from 'vitest';
import {
  aptitudeBonusPercent,
  APTITUDE_MAX,
  APTITUDE_VILLAGE_CEILING,
} from '../src/lib/net/commands';
import { KNOWN_AFFIX_IDS } from '../src/lib/ui/affixes';
import { ATTRIBUTE_MILESTONES, ATTRIBUTE_THRESHOLDS, ATTRIBUTE_CURVES } from '../src/lib/net/commands';
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

/** The source from `marker` onward, so a pattern cannot match a different
 *  switch elsewhere in the same file. */
function after(source: string, marker: string, what: string): string {
  const at = source.indexOf(marker);
  if (at < 0) throw new Error(`could not find ${what} - the pattern needs updating, not deleting`);
  return source.slice(at, at + 1200);
}

/** Every `key -> value` pair `pattern` finds, as numbers. */
function table(source: string, pattern: RegExp): Map<number, number> {
  const out = new Map<number, number>();
  for (const m of source.matchAll(pattern)) out.set(Number(m[1]), Number(m[2]));
  if (out.size === 0) throw new Error('matched no table rows - the pattern needs updating, not deleting');
  return out;
}

describe('the numbers the client mirrors still match the server', () => {
  // Modul: THIS TEST USED TO GUARD A FORMULA NEITHER SIDE HAD ANY MORE.
  //
  // It compared a client `REROLL_BASE_FEE`/`REROLL_FEE_GROWTH` pair against the
  // server's `RerollGoldBase`/`RerollGoldItemTierGrowth`, i.e. the old
  // `100 * 1.35^(itemTier-1)` curve. That curve is gone: the server charges a
  // flat per-REGION table now, and the client had already followed it. So the
  // client constants did not exist, the test threw its own "pattern needs
  // updating" error on every run, and the two server constants sat unreferenced
  // - a guard that fails constantly guards nothing, because the failure stops
  // being information.
  //
  // The repair is to compare what both sides ACTUALLY use: the five-entry
  // region table, entry by entry. Note the two are keyed on regionTier (1-5,
  // via AffixRerollEngine.ResolveRegionTier), NOT on the fourteen-step item
  // rarity - the names in the old constants were the misleading part.
  it('reroll: the per-region gold table', () => {
    const affixes = read(serverRoot, 'Engine', 'AffixRegistry.cs');
    const forge = read(clientRoot, 'routes', 'Forge.svelte');

    // Both tables are sliced out of their own function first. Bare
    // `N => NNNNL` and `case N: return NNNN` shapes appear in other switches in
    // both files, and a mirror test that matches the wrong switch is worse than
    // no mirror test.
    const serverBody = after(affixes, 'public static long CalculateRerollGoldCost', 'server reroll function');
    const clientBody = after(forge, 'function getRerollCost', 'client reroll function');

    const serverTable = table(serverBody, /(\d) => (\d+)L/g);
    const clientTable = table(clientBody, /case (\d): return (\d+);/g);

    expect([...clientTable.keys()].sort()).toEqual([1, 2, 3, 4, 5]);
    for (const tier of [1, 2, 3, 4, 5]) {
      expect(clientTable.get(tier), `region ${tier} reroll price`).toBe(serverTable.get(tier));
    }
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

  // Modul: THE AFFIX ORDER IS A WIRE FORMAT.
  //
  // Auto-reroll's "stop on stat" travels as a 1-based INDEX into
  // AffixRegistry.Definitions, because ClientCommandPacket is fixed-layout and
  // cannot carry a string. So the client's KNOWN_AFFIX_IDS is not a display
  // list - it is the same ordering written down a second time, and it had
  // drifted for ten of its twelve entries. Picking "crit chance" sent the index
  // the server reads as `range_dmg_pct`, which is weapon-only, so on any other
  // slot the run was refused before it rolled once.
  it('affixes: the registry order, which auto-reroll sends as an index', () => {
    const registry = read(serverRoot, 'Engine', 'AffixRegistry.cs');
    const serverOrder = [...registry.matchAll(/new AffixDefinition\("([a-z_]+)"/g)].map((m) => m[1]);

    expect(serverOrder.length).toBeGreaterThan(10);
    expect(KNOWN_AFFIX_IDS.length).toBe(serverOrder.length);
    // Element by element, not as a set: the INDEX is what goes on the wire.
    expect([...KNOWN_AFFIX_IDS]).toEqual(serverOrder);
  });

  // Modul: THE MILESTONE TABLE IS A MIRROR, and a long one.
  //
  // Twenty rows of thresholds, names and magnitudes live in AttributeRegistry
  // and again in commands.ts, because they are a static table the server never
  // changes at runtime and StateUpdatePacket is a fixed-layout struct with a
  // size guard. A mirror is the right call there - but only with this test
  // under it, because a track that promises "Sunder at 60" and delivers
  // something else at 60 is worse than no track at all.
  it('attributes: the milestone table and the curves', () => {
    const registry = read(serverRoot, 'Engine', 'AttributeRegistry.cs');

    const thresholds = JSON.parse(
      '[' + /Thresholds = \{([^}]+)\}/.exec(registry)![1].replace(/,\s*$/, '') + ']',
    ) as number[];
    expect([...ATTRIBUTE_THRESHOLDS]).toEqual(thresholds);

    // `new(attribute, threshold, "Name", MilestoneEffect.Thing, magnitude)`
    const serverRows = [...registry.matchAll(
      /new\((\w+),\s*(\d+),\s*"([^"]+)",\s*MilestoneEffect\.(\w+),\s*([\d.]+)f\)/g,
    )].map((m) => ({ attribute: m[1], threshold: Number(m[2]), name: m[3] }));

    expect(serverRows.length).toBe(ATTRIBUTE_MILESTONES.length);

    const attributeIndex: Record<string, number> = { Might: 0, Finesse: 1, Vigour: 2, Fortune: 3 };
    serverRows.forEach((row, i) => {
      const client = ATTRIBUTE_MILESTONES[i];
      expect(attributeIndex[row.attribute], `row ${i} attribute`).toBe(client.attribute);
      expect(row.threshold, `row ${i} threshold`).toBe(client.threshold);
      expect(row.name, `row ${i} name`).toBe(client.name);
    });

    // And the curves, so a card's preview cannot promise a different number
    // from the one the server grants.
    expect(ATTRIBUTE_CURVES.critChancePerRootPoint).toBe(
      num(registry, /CritChancePerRootPoint = ([\d.]+)f/, 'crit chance per root point'),
    );
    expect(ATTRIBUTE_CURVES.attackSpeedPerRootPoint).toBe(
      num(registry, /AttackSpeedPerRootPoint = ([\d.]+)f/, 'attack speed per root point'),
    );
    expect(ATTRIBUTE_CURVES.lootLuckPerRootPoint).toBe(
      num(registry, /LootLuckPerRootPoint = ([\d.]+)f/, 'loot luck per root point'),
    );
  });

  it('gathering: mastery and village production percentages', () => {
    const tools = read(serverRoot, 'Domain', 'Shared', 'GatheringToolEngine.cs');
    const gathering = read(clientRoot, 'routes', 'Gathering.svelte');

    // Modul: mastery is a CURVE now - 40 * sqrt(level) - because a flat 10%
    // a level with no ceiling reached +1270% and drowned the tool curve beside
    // it. Both sides must agree on the constant and on the square root; a
    // client that mirrored only the constant would draw a straight line and be
    // wrong at every level but the first.
    expect(num(gathering, /MASTERY_SPEED_PCT_AT_LEVEL_ONE = (\d+)/, 'client mastery pct')).toBe(
      num(tools, /MasterySpeedPctAtLevelOne = (\d+)/, 'server mastery pct'),
    );
    expect(gathering).toMatch(/Math\.sqrt\(level\)/);
    expect(tools).toMatch(/Math\.Sqrt\(masteryLevel\)/);
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

  // Modul: the aptitude curve is a mirror the moment the panel shows a
  // percentage. Two diminishing tables drifting apart would have the client
  // promising a bonus the server does not grant - and nobody would notice,
  // because both numbers look plausible.
  it('breeding: the aptitude cap, the village ceiling and the whole curve', () => {
    const apt = read(serverRoot, 'Engine', 'BreedingAptitudes.cs');
    // The CLIENT side is the real exported function, not a re-parse of its
    // source: a test that reimplements the thing it is checking passes for the
    // wrong reason the moment either copy moves.
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    expect(num(commands, /APTITUDE_MAX = (\d+)/, 'client cap')).toBe(
      num(apt, /MaxValue = (\d+)/, 'server cap'),
    );
    expect(num(commands, /APTITUDE_VILLAGE_CEILING = (\d+)/, 'client village ceiling')).toBe(
      num(apt, /VillagerCeiling = (\d+)/, 'server village ceiling'),
    );
    expect(APTITUDE_MAX).toBe(num(apt, /MaxValue = (\d+)/, 'server cap'));
    expect(APTITUDE_VILLAGE_CEILING).toBe(num(apt, /VillagerCeiling = (\d+)/, 'server ceiling'));

    // And the curve itself, band by band, at every boundary that matters.
    const bandOneEnd = num(apt, /BandOneEnd = (\d+)/, 'band one end');
    const bandTwoEnd = num(apt, /BandTwoEnd = (\d+)/, 'band two end');
    const one = Number(apt.match(/BandOnePerPoint = ([\d.]+)f/)![1]);
    const two = Number(apt.match(/BandTwoPerPoint = ([\d.]+)f/)![1]);
    const three = Number(apt.match(/BandThreePerPoint = ([\d.]+)f/)![1]);
    const cap = num(apt, /MaxValue = (\d+)/, 'cap');

    const serverCurve = (points: number) => {
      if (points <= 0) return 0;
      const p = Math.min(points, cap);
      let total = Math.min(p, bandOneEnd) * one;
      if (p > bandOneEnd) total += (Math.min(p, bandTwoEnd) - bandOneEnd) * two;
      if (p > bandTwoEnd) total += (p - bandTwoEnd) * three;
      return total;
    };

    for (const points of [0, 1, 19, 20, 21, 34, 35, 36, 49, 50, 60]) {
      expect(aptitudeBonusPercent(points), `${points} points`).toBeCloseTo(
        serverCurve(points),
        4,
      );
    }
  });

  it('combat: the first-clear boss multiplier', () => {
    const rules = read(serverRoot, 'Domain', 'Combat', 'BossFirstClearRules.cs');
    const combat = read(clientRoot, 'routes', 'Combat.svelte');

    expect(num(combat, /FIRST_CLEAR_HP = (\d+)/, 'client first-clear hp')).toBe(
      num(rules, /FirstClearHpMultiplier = (\d+)/, 'server first-clear hp'),
    );
  });

  // Modul: the world boss rework of 2026-09-05 added SIX hand-mirrored numbers
  // in one commit - the plate count, the weak-point multiplier, the hidden
  // sentinel, the session cap, and it inherited an attempt cap that had never
  // been guarded at all. Every one of them decides something the screen says
  // out loud BEFORE the player commits, which is exactly the case this file
  // exists for: change the rule, and the preview goes on confidently quoting
  // the old answer.
  it('world boss: the armour, its multiplier and the hidden sentinel', () => {
    const engine = read(serverRoot, 'Engine', 'WorldBossEngine.cs');
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    expect(num(commands, /BOSS_PLATE_COUNT = (\d+)/, 'client plate count')).toBe(
      num(engine, /PlateCount = (\d+)/, 'server plate count'),
    );
    expect(num(commands, /BOSS_WEAK_PLATE_MULTIPLIER = (\d+)/, 'client weak multiplier')).toBe(
      num(engine, /WeakPlateDamageMultiplier = ([\d.]+)/, 'server weak multiplier'),
    );
    // The sentinel is not a balance number, but a mismatch would make every
    // client believe the weak point had been found on plate 255.
    expect(num(commands, /BOSS_WEAK_PLATE_HIDDEN = (\d+)/, 'client hidden sentinel')).toBe(
      num(engine, /WeakPlateHidden = (\d+)/, 'server hidden sentinel'),
    );
  });

  it('world boss: the attempt budget and the battle session cap', () => {
    const engine = read(serverRoot, 'Engine', 'WorldBossEngine.cs');
    const commands = read(clientRoot, 'lib', 'net', 'commands.ts');

    expect(num(commands, /MAX_BOSS_ATTEMPTS = (\d+)/, 'client attempt cap')).toBe(
      num(engine, /MaxAttemptsPerEncounter = (\d+)/, 'server attempt cap'),
    );

    // THIS ONE COST THE MOST BY BEING UNSAID. The server gives a player 300
    // seconds from their first strike to spend the other two, and until
    // 2026-09-05 nothing carried that - the button stayed enabled and the
    // attack rolled back in silence for the rest of an encounter that runs for
    // up to seven days. The screen counts down against this number now, so the
    // two halves have to agree or the countdown lies.
    expect(num(commands, /BOSS_SESSION_CAP_SECONDS = (\d+)/, 'client session cap')).toBe(
      num(engine, /BattleSessionCapSeconds = (\d+)L/, 'server session cap'),
    );
  });

  it('wiki: the world boss page quotes the rules the server enforces', () => {
    // Modul: THE WIKI TAUGHT A MECHANIC THE GAME NO LONGER HAD.
    //
    // Before 2026-09-05 the page quoted a 100,000,000 damage CEILING - the
    // clamp on the damage figure the client used to compute about itself. The
    // client stopped sending one, so the ceiling stopped existing, and the page
    // would have gone on explaining it. This project has already shipped a wiki
    // that taught the wrong thing once: its core loop described the old,
    // pre-2026-09-02 order and said the FOURTH monster kills an unfed
    // character.
    const engine = read(serverRoot, 'Engine', 'WorldBossEngine.cs');
    const wiki = read(clientRoot, 'lib', 'ui', 'wikiData.ts');

    expect(num(wiki, /WORLD_BOSS_PLATES = (\d+)/, 'wiki plate count')).toBe(
      num(engine, /PlateCount = (\d+)/, 'server plate count'),
    );
    expect(num(wiki, /WORLD_BOSS_WEAK_MULTIPLIER = (\d+)/, 'wiki weak multiplier')).toBe(
      num(engine, /WeakPlateDamageMultiplier = ([\d.]+)/, 'server weak multiplier'),
    );
    expect(num(wiki, /WORLD_BOSS_ATTEMPTS = (\d+)/, 'wiki attempt cap')).toBe(
      num(engine, /MaxAttemptsPerEncounter = (\d+)/, 'server attempt cap'),
    );
    // The page says the session in MINUTES; the server counts seconds.
    expect(num(wiki, /WORLD_BOSS_SESSION_MINUTES = (\d+)/, 'wiki session minutes') * 60).toBe(
      num(engine, /BattleSessionCapSeconds = (\d+)L/, 'server session cap'),
    );

    // And the retired ceiling must not come back as a number nobody enforces.
    expect(wiki).not.toContain('WORLD_BOSS_DAMAGE_CEILING =');
  });

  it('loot: the equipment drop chance the odds line quotes', () => {
    // The Loot drops panel tells the player "Legendary+ about 1 in N kills",
    // and N is rarityOdds() divided by this. If the server changed the drop
    // rate the panel would go on quoting the old number with total confidence,
    // which is exactly the failure this file exists for.
    const loot = read(serverRoot, 'Engine', 'CombatLootEngine.cs');
    const rarity = read(clientRoot, 'lib', 'ui', 'rarity.ts');

    expect(num(rarity, /EQUIPMENT_DROP_CHANCE = ([\d.]+)/, 'client drop chance')).toBe(
      num(loot, /EquipmentDropChance = ([\d.]+)/, 'server drop chance'),
    );
  });

  it('combat log: the event kinds and flags the server actually sends', () => {
    // The fight log decodes a numeric EventKind and a Flags bitmask into the
    // words a player reads. A mismatch here does not throw - it silently
    // relabels every line, which is worse: "You miss" where the server said
    // "Critical".
    const packet = read(serverRoot, 'Network', 'ResponseCombatEventPacket.cs');
    const store = read(clientRoot, 'lib', 'stores', 'combatLog.ts');

    const kinds: [string, string][] = [
      ['PlayerHit', 'KindPlayerHit'],
      ['PlayerMiss', 'KindPlayerMiss'],
      ['MonsterHit', 'KindMonsterHit'],
      ['MonsterMiss', 'KindMonsterMiss'],
      ['Lifesteal', 'KindLifesteal'],
      ['Kill', 'KindKill'],
    ];
    for (const [clientName, serverName] of kinds) {
      expect(
        num(store, new RegExp(`${clientName}: (\\d+)`), `client ${clientName}`),
      ).toBe(num(packet, new RegExp(`${serverName} = (\\d+)`), `server ${serverName}`));
    }

    // The flags are written as shifts server-side, so they are compared as the
    // shift rather than the value.
    const flags: [string, string, number][] = [
      ['Crit', 'FlagCrit', 0],
      ['Blocked', 'FlagBlocked', 1],
      ['Burn', 'FlagBurn', 2],
      ['Thorns', 'FlagThorns', 3],
    ];
    for (const [clientName, serverName, shift] of flags) {
      expect(num(store, new RegExp(`${clientName}: (\\d+)`), `client ${clientName}`)).toBe(
        1 << num(packet, new RegExp(`${serverName} = 1 << (\\d+)`), `server ${serverName}`),
      );
      expect(num(packet, new RegExp(`${serverName} = 1 << (\\d+)`), `server ${serverName}`)).toBe(shift);
    }
  });
});
