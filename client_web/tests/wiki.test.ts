import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  SCREEN_COVERAGE,
  WIKI_SEARCH_INDEX,
  VILLAGE_TIER_MATERIALS,
  GUILD_BUFF_TIERS,
  REROLL_GOLD_BY_REGION,
  GUILD_BUFF_COST_PER_MATERIAL,
  HALL_BASE_SLOTS,
  HALL_MAX_SLOTS,
  SKILL_POINTS_PER_SEAL,
  DEED_CHAPTERS,
  TOOL_TIERS,
  MASTERY_SPEED_PCT_PER_LEVEL,
  VILLAGE_SPEED_PCT_PER_LEVEL,
  DAILY_LOGIN_DAY7_DIAMONDS,
  WORLD_BOSS_HP,
  WORLD_BOSS_ATTEMPTS,
  GUILD_TAX_MIN_PCT,
  GUILD_TAX_MAX_PCT,
  townHallCeiling,
  villageTierIndex,
} from '../src/lib/ui/wikiData';

// Modul: the wiki's two failure modes, both silent.
//
// The FIRST is drift: a page quoting a price or a material the server stopped
// charging. That is not hypothetical here - `commands.ts`'s `villageCostLabel`
// carries a display copy of the village's ore column and it names the RARE ore
// for tiers 1, 2 and 4, where VillageManagementEngine charges the common one.
// Its own comment says it was corrected; a second pass the same day moved the
// server and the label did not follow. Nothing compared the two, so nothing
// said so. Every table the wiki restates is compared here, entry by entry,
// against the C# that owns it.
//
// The SECOND is a screen nobody documented. A wiki is only "complete" against
// a list of what exists, so the coverage ledger is asserted to be exactly the
// set of `src/routes/*.svelte`: a new screen fails this suite until somebody
// decides whether it needs a page.
//
// Regexes over source, deliberately, for the same reason serverMirrors.test.ts
// gives: the constants are one-liners, and a test needing a build step is a
// test that gets skipped.

const here = dirname(fileURLToPath(import.meta.url));
const serverRoot = join(here, '..', '..', 'server', 'FolkIdle.Server');
const routesDir = join(here, '..', 'src', 'routes');

const read = (...parts: string[]) => readFileSync(join(...parts), 'utf8');

function num(source: string, pattern: RegExp, what: string): number {
  const match = source.match(pattern);
  if (!match) throw new Error(`could not find ${what} - the pattern needs updating, not deleting`);
  return Number(match[1]);
}

/** The source from `marker` onward, so a pattern cannot match a different
 *  table elsewhere in the same file. */
function after(source: string, marker: string, what: string, span = 2000): string {
  const at = source.indexOf(marker);
  if (at < 0) throw new Error(`could not find ${what} - the pattern needs updating, not deleting`);
  return source.slice(at, at + span);
}

describe('the wiki documents every screen the game has', () => {
  const screensOnDisk = readdirSync(routesDir)
    .filter((file) => file.endsWith('.svelte'))
    .map((file) => file.replace(/\.svelte$/, ''))
    .sort();

  it('the ledger and src/routes are the same set', () => {
    const ledger = SCREEN_COVERAGE.map((row) => row.screen).sort();

    // Named rather than a bare toEqual, because the two ways this fails need
    // different fixes: a new screen needs a decision, a deleted one needs a row
    // removed.
    const undocumented = screensOnDisk.filter((s) => !ledger.includes(s));
    const stale = ledger.filter((s) => !screensOnDisk.includes(s));

    expect(undocumented, 'screens with no row in SCREEN_COVERAGE').toEqual([]);
    expect(stale, 'SCREEN_COVERAGE rows for screens that no longer exist').toEqual([]);
  });

  it('every documented screen names a tab, and every other row gives a reason', () => {
    for (const row of SCREEN_COVERAGE) {
      if (row.status === 'documented') {
        expect(row.tab, `${row.screen} is documented but names no tab`).toBeTruthy();
      } else {
        expect(row.tab, `${row.screen} is not documented but names a tab`).toBeUndefined();
      }
      expect(row.note.length, `${row.screen} has no note`).toBeGreaterThan(20);
    }
  });

  it('every tab a ledger row points at is a tab the wiki actually renders', () => {
    const wiki = read(here, '..', 'src', 'routes', 'Wiki.svelte');
    const tabs = new Set(
      [...wiki.matchAll(/\{ id: '([a-z]+)', label:/g)].map((m) => m[1]),
    );
    expect(tabs.size).toBeGreaterThan(10);

    for (const row of SCREEN_COVERAGE) {
      if (row.status !== 'documented' || !row.tab) continue;
      expect(tabs.has(row.tab), `${row.screen} points at a tab "${row.tab}" that does not exist`).toBe(true);
    }
    for (const entry of WIKI_SEARCH_INDEX) {
      expect(tabs.has(entry.tab), `search entry "${entry.title}" points at tab "${entry.tab}"`).toBe(true);
    }
  });

  it('search reaches every tab', () => {
    const wiki = read(here, '..', 'src', 'routes', 'Wiki.svelte');
    const tabs = [...wiki.matchAll(/\{ id: '([a-z]+)', label:/g)].map((m) => m[1]);
    const indexed = new Set(WIKI_SEARCH_INDEX.map((e) => e.tab));

    // A page with no search entry is a page only reachable by knowing it is
    // there, which is the failure the search box exists to prevent.
    expect(tabs.filter((t) => !indexed.has(t)), 'tabs with no search entries').toEqual([]);
  });
  it('every search result lands on a heading that exists', () => {
    // Modul: a search hit that jumps nowhere is worse than no hit - the tab
    // changes, the page does not move, and the reader concludes the section
    // does not exist. The anchors live across five files, so they are collected
    // from all of them.
    const uiDir = join(here, '..', 'src', 'lib', 'ui');
    const sources = [
      read(here, '..', 'src', 'routes', 'Wiki.svelte'),
      ...readdirSync(uiDir)
        .filter((f) => f.startsWith('Wiki') && f.endsWith('.svelte'))
        .map((f) => read(uiDir, f)),
    ].join('\n');

    const ids = new Set([...sources.matchAll(/ id="([a-z-]+)"/g)].map((m) => m[1]));

    for (const entry of WIKI_SEARCH_INDEX) {
      expect(ids.has(entry.anchor), `"${entry.title}" points at #${entry.anchor}, which nothing renders`).toBe(true);
    }
  });
});

describe('the tables the wiki restates still match the server', () => {
  it('village: the tier materials, log and ore, common and rare', () => {
    const village = read(serverRoot, 'Domain', 'Progression', 'VillageManagementEngine.cs');
    const body = after(village, 'TierMaterials = new[]', 'the server tier table');

    const rows = [...body.matchAll(/\(\s*"([a-z_]+)",\s*"([a-z_]+)",\s*"([a-z_]+)",\s*"([a-z_]+)"\s*\)/g)];
    expect(rows.length, 'five tiers, one per region').toBe(5);

    for (let tier = 0; tier < 5; tier++) {
      const [, log, ore, rareLog, rareOre] = rows[tier];
      expect(VILLAGE_TIER_MATERIALS[tier].log, `tier ${tier + 1} log`).toBe(log);
      expect(VILLAGE_TIER_MATERIALS[tier].ore, `tier ${tier + 1} ore`).toBe(ore);
      expect(VILLAGE_TIER_MATERIALS[tier].rareLog, `tier ${tier + 1} rare log`).toBe(rareLog);
      expect(VILLAGE_TIER_MATERIALS[tier].rareOre, `tier ${tier + 1} rare ore`).toBe(rareOre);
    }
  });

  // Modul: this is the assertion that would have caught the live defect. The
  // village and the guild depot are priced in the SAME five pairs on purpose -
  // if they ever diverge, one of them is charging for something the other says
  // is a different tier, and a player following one page would stock the wrong
  // material.
  it('the guild buff tiers use the same pairs the village does', () => {
    const guild = read(serverRoot, 'Engine', 'GuildContributionEngine.cs');
    const body = after(guild, 'BuffTierMaterials = new[]', 'the server buff table');

    const rows = [...body.matchAll(/\(\s*"([a-z_]+)",\s*"([a-z_]+)",\s*"([a-z_]+)",\s*"([a-z_]+)"\s*\)/g)];
    expect(rows.length).toBe(5);

    for (let tier = 0; tier < 5; tier++) {
      const [, commonWood, rareWood, commonOre, rareOre] = rows[tier];
      expect(GUILD_BUFF_TIERS[tier].commonWood).toBe(commonWood);
      expect(GUILD_BUFF_TIERS[tier].rareWood).toBe(rareWood);
      expect(GUILD_BUFF_TIERS[tier].commonOre).toBe(commonOre);
      expect(GUILD_BUFF_TIERS[tier].rareOre).toBe(rareOre);

      // And the two tables agree with each other.
      expect(GUILD_BUFF_TIERS[tier].commonWood, `tier ${tier + 1} wood`).toBe(VILLAGE_TIER_MATERIALS[tier].log);
      expect(GUILD_BUFF_TIERS[tier].commonOre, `tier ${tier + 1} ore`).toBe(VILLAGE_TIER_MATERIALS[tier].ore);
      expect(GUILD_BUFF_TIERS[tier].rareOre, `tier ${tier + 1} rare ore`).toBe(VILLAGE_TIER_MATERIALS[tier].rareOre);
    }
  });

  it('guild: the buff material price and the tax range', () => {
    const guild = read(serverRoot, 'Engine', 'GuildContributionEngine.cs');
    const record = read(serverRoot, 'Models', 'GuildRecord.cs');

    // The C# writes it as 25_000, which Number() reads as NaN - the digit
    // separator has to come out before the comparison.
    const buffPrice = guild.match(/BuffMaterialCostPerType = ([\d_]+)/);
    if (!buffPrice) throw new Error('could not find the buff material price');
    expect(GUILD_BUFF_COST_PER_MATERIAL).toBe(Number(buffPrice[1].replace(/_/g, '')));
    expect(GUILD_TAX_MIN_PCT).toBe(num(record, /MinTaxRatePct = (\d+)/, 'min tax'));
    expect(GUILD_TAX_MAX_PCT).toBe(num(record, /MaxTaxRatePct = (\d+)/, 'max tax'));
  });

  it('forge: the per-region reroll price', () => {
    const affixes = read(serverRoot, 'Engine', 'AffixRegistry.cs');
    const body = after(affixes, 'public static long CalculateRerollGoldCost', 'the reroll function');

    const rows = [...body.matchAll(/(\d) => (\d+)L/g)];
    // The trailing `_ => 10000L` default is not a region and is not matched.
    expect(rows.length).toBe(5);

    for (const [, region, cost] of rows) {
      expect(REROLL_GOLD_BY_REGION[Number(region)], `region ${region} reroll`).toBe(Number(cost));
    }
  });

  it('village: the Town Hall ceiling formula', () => {
    const village = read(serverRoot, 'Domain', 'Progression', 'VillageManagementEngine.cs');
    // The method name also appears in a comment further up the file, so the
    // marker is the signature rather than the bare name.
    const body = after(village, 'public static int GetMaxBuildingLevelCeiling', 'the ceiling function', 400);
    const match = body.match(/return (\d+) \+ townHallLevel \* (\d+);/);
    if (!match) throw new Error('could not find the ceiling formula - the pattern needs updating');

    for (let level = 0; level <= 5; level++) {
      expect(townHallCeiling(level), `ceiling at Town Hall ${level}`).toBe(
        Number(match[1]) + level * Number(match[2]),
      );
    }
  });

  it('village: the tier bands are five levels wide, like GetTierMaterials', () => {
    // `Math.Clamp(currentLevel / 5, 0, 4)` - the whole of the server's rule.
    const village = read(serverRoot, 'Domain', 'Progression', 'VillageManagementEngine.cs');
    expect(village).toMatch(/int tier = Math\.Clamp\(currentLevel \/ 5, 0, 4\);/);

    expect(villageTierIndex(0)).toBe(0);
    expect(villageTierIndex(4)).toBe(0);
    expect(villageTierIndex(5)).toBe(1);
    expect(villageTierIndex(19)).toBe(3);
    expect(villageTierIndex(20)).toBe(4);
    expect(villageTierIndex(100)).toBe(4);
  });

  it('gathering: the tool speed curve and the two percentages', () => {
    const tools = read(serverRoot, 'Domain', 'Shared', 'GatheringToolEngine.cs');

    const serverTable = [...tools.matchAll(/^\s*(\d+) => (\d+),/gm)].map((m) => [
      Number(m[1]),
      Number(m[2]),
    ]);
    expect(serverTable.length).toBe(10);

    for (const [tier, pct] of serverTable) {
      const row = TOOL_TIERS.find((t) => t.tier === tier);
      expect(row, `tool tier ${tier} is missing from the wiki table`).toBeTruthy();
      expect(row!.speedPct, `tool tier ${tier} speed`).toBe(pct);
    }

    expect(MASTERY_SPEED_PCT_PER_LEVEL).toBe(
      num(tools, /MasterySpeedPctPerLevel = (\d+)/, 'mastery pct'),
    );
    expect(VILLAGE_SPEED_PCT_PER_LEVEL).toBe(
      num(tools, /VillageYieldBonusPctPerLevel = (\d+)/, 'village pct'),
    );
  });

  it('gathering: the ten tool woods, in tier order', () => {
    const content = read(serverRoot, 'Engine', 'ContentRegistry.cs');
    const body = after(content, 'ToolWoodsByTier', 'the tool wood table', 400);
    const woods = [...body.matchAll(/"([a-z_]+)_"/g)].map((m) => m[1]);
    expect(woods.length).toBe(10);

    // Compared on TOOL_TIERS' own slug rather than on a slug() of the display
    // name: "Voidbark" is one word in the catalogue, and deriving the id from
    // the readable name is exactly how the tool art was mis-keyed once already.
    for (let i = 0; i < 10; i++) {
      expect(TOOL_TIERS[i].slug, `tool tier ${i + 1} wood`).toBe(woods[i]);
    }
  });

  it('the long game: seals, hall slots and the deed chapters', () => {
    const deeds = read(serverRoot, 'Engine', 'DeedRegistry.cs');
    const hall = read(serverRoot, 'Engine', 'HallOfAncestorsRules.cs');

    expect(SKILL_POINTS_PER_SEAL).toBe(num(deeds, /SkillPointsPerSeal = (\d+)/, 'points per seal'));
    expect(DEED_CHAPTERS.length).toBe(num(deeds, /ChapterCount = (\d+)/, 'chapter count'));

    // The titles are what a player searches for, so they have to be the real
    // ones rather than a paraphrase.
    const titles = [...deeds.matchAll(/new DeedChapter\(\d+, "([^"]+)"/g)].map((m) => m[1]);
    expect(titles).toEqual(DEED_CHAPTERS.map((c) => c.title));

    expect(HALL_BASE_SLOTS).toBe(num(hall, /BaseSlots = (\d+)/, 'hall base slots'));
    expect(HALL_MAX_SLOTS).toBe(num(hall, /MaxSlots = (\d+)/, 'hall max slots'));
  });

  it('events: the world boss and the day-7 diamond bonus', () => {
    const boss = read(serverRoot, 'Engine', 'WorldBossEngine.cs');
    const daily = read(serverRoot, 'Domain', 'Progression', 'DailyLoginRewardEngine.cs');

    expect(WORLD_BOSS_HP).toBe(num(boss, /BaseHp = (\d+)L/, 'boss hp'));
    expect(WORLD_BOSS_ATTEMPTS).toBe(
      num(boss, /MaxAttemptsPerEncounter = (\d+)/, 'boss attempts'),
    );
    expect(DAILY_LOGIN_DAY7_DIAMONDS).toBe(
      num(daily, /PremiumDiamondsOnDay7Completion = (\d+)/, 'day 7 diamonds'),
    );
  });
});
