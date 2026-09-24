// Modul: task 29. The Market's filter checkboxes were misaligned twice: first
// because a bare `.filters input` stretched every nested checkbox to fill its
// row (fixed in 9474630 by `.filters > input`), then because a 44px checkbox
// forced a track so wide a phone got one column of sixteen rows. The design
// now is: a normal-sized box opted out of app.css's 44x44 rule, inside a
// label that is itself the 44px target. This pins the three load-bearing
// pieces; check:touch (which credits a wrapping label) measures the result on
// a dev box.
//
// Local-only: CI (deploy.yml) does not run vitest. Run `npm test`.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const market = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'routes', 'Market.svelte'),
  'utf8',
);

describe('market filters', () => {
  it('keeps the `>` on .filters > input (the search field only)', () => {
    expect(market).toMatch(/\n\s*\.filters > input\s*\{/);
    expect(market).not.toMatch(/\n\s*\.filters input\s*\{/);
  });

  it('opts every checkbox out of the 44x44 box rule - the label is the target', () => {
    const boxes = [...market.matchAll(/<input\b[^>]*type="checkbox"[^>]*>/g)].map((m) => m[0]);
    expect(boxes.length).toBeGreaterThan(0);
    for (const box of boxes) expect(box).toMatch(/class="touch-exempt"/);
  });

  it('gives the label the 44px floor on a phone', () => {
    const phone = market.match(/@media \(max-width: 40rem\) \{\s*\.checks label \{([^}]*)\}/);
    expect(phone, 'the phone .checks label rule moved - update this test').not.toBeNull();
    expect(phone![1]).toMatch(/min-height:\s*44px/);
  });
});
