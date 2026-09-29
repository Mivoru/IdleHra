import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Modul: TASK 68 - THREE SCREENS STATED RULES THE GAME DOES NOT HAVE.
//
// The Wiki is held to the server by wiki.test.ts; the screens' own sentences
// were held by nothing, and three had drifted: Gathering printed the retired
// "two ticks a level" mastery rule ("-112 mining" beside rates that used
// 40 * sqrt(level)), the Diamond Star banner promised "+5 percentage points
// forge success" to a fusion that has had no roll since 2026-09-06, and the
// Welcome back card said a harder monster raises the hourly rate when XP and
// gold per kill are proportional to its health. These read the source the way
// serverMirrors.test.ts does, so the next drift fails here.

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const read = (...parts: string[]) => readFileSync(join(repo, ...parts), 'utf8');

describe('screens say what the server does', () => {
  it('the Diamond Star banner names the fee discount ForgeSplicingEngine applies', () => {
    const forge = read('server', 'FolkIdle.Server', 'Domain', 'Economy', 'ForgeSplicingEngine.cs');
    const match = forge.match(/ActiveGlobalEventId == 4[\s\S]{0,600}?feeDiscount \+ ([0-9.]+)\)/);
    expect(match, 'the DiamondStar branch in ForgeSplicingEngine moved - update this test, do not delete it').not.toBeNull();
    const pct = Math.round(Number(match![1]) * 100);

    const banner = read('client_web', 'src', 'lib', 'ui', 'EventBanner.svelte');
    expect(banner).toContain(`effect: '${pct}% off fusion fees'`);
    expect(banner).not.toMatch(/effect: '[^']*forge success/);
  });

  it('the Gathering mastery line reads the same curve as the rates', () => {
    const gathering = read('client_web', 'src', 'routes', 'Gathering.svelte');
    expect(gathering).not.toMatch(/masteryLevelOf\(\d\) \* 2/);
    expect(gathering).not.toMatch(/cuts two ticks/);
    expect(gathering).toMatch(/\+\{masterySpeedPct\(masteryLevelOf\(1\)\)\}% mining/);
  });

  it('the Welcome back card does not promise a harder monster pays more per hour', () => {
    const summary = read('client_web', 'src', 'lib', 'ui', 'OfflineSummary.svelte');
    const visible = summary.replace(/<!--[\s\S]*?-->/g, '');
    expect(visible).not.toMatch(/harder monster running and this goes up/);
  });
});
