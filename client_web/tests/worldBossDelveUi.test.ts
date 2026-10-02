// Task 105: the World Boss timer reads in days, and the Delve's colours are
// tokens - its dark "underground" palette lives in app.css, once.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { bossTimeLeft } from '../src/lib/game/worldBossTime';

const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

describe('bossTimeLeft', () => {
  it('counts a week in days, not 97 hours', () => {
    expect(bossTimeLeft(97 * 3600 + 33 * 60)).toBe('4d 1h');
  });
  it('drops to hours and minutes under a day', () => {
    expect(bossTimeLeft(3 * 3600 + 12 * 60 + 5)).toBe('3h 12m');
  });
  it('drops to minutes and seconds under an hour', () => {
    expect(bossTimeLeft(12 * 60 + 30)).toBe('12m 30s');
  });
  it('never goes negative', () => {
    expect(bossTimeLeft(-5)).toBe('0m 0s');
  });
});

describe('the Delve has no private palette', () => {
  const delve = readFileSync(join(src, 'routes', 'Delve.svelte'), 'utf8');

  it('writes no colour literal - every colour is an --ug-* token', () => {
    const literals = delve.match(/#[0-9a-fA-F]{3,8}\b|rgba?\(/g) ?? [];
    expect(literals).toEqual([]);
  });

  it('reads only underground tokens that app.css defines', () => {
    const css = readFileSync(join(src, 'app.css'), 'utf8');
    const used = new Set([...delve.matchAll(/var\((--ug-[\w-]+)\)/g)].map((m) => m[1]));
    expect(used.size).toBeGreaterThan(5);
    for (const name of used) expect(css).toContain(`${name}:`);
  });

  it('puts the page into the underground mood and takes it out again', () => {
    expect(delve).toContain("root.dataset.mood = 'underground'");
    expect(delve).toContain('delete root.dataset.mood');
  });
});

describe('the shield wheel overlay (task 105)', () => {
  const wheel = readFileSync(join(src, 'lib', 'ui', 'ShieldWheel.svelte'), 'utf8');

  it('is portalled above the shell header', () => {
    expect(wheel).toMatch(/use:portal\s+class="overlay"/);
  });

  it('can be left while a run is live', () => {
    expect(wheel).toContain("'Leave practice'");
    expect(wheel).toContain("'Leave - finish later'");
  });

  it('draws the plate numbers outside the rotating ring, so they stay upright', () => {
    const ring = wheel.slice(wheel.indexOf('class="ring"'), wheel.indexOf('</g>', wheel.indexOf('class="ring"')));
    expect(ring).not.toContain('class="label"');
  });

  it('throws from a solid button that counts the spears, not a dashed drop zone', () => {
    expect(wheel).toContain('class="throw-zone primary"');
    expect(wheel).toContain('`Throw - ${spearsLeft} left`');
  });
});
