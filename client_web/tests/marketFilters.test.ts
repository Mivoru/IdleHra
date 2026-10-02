// Modul: task 102 replaced the Market's filter checkboxes. They were
// misaligned twice (task 29: a bare `.filters input` stretched every nested
// checkbox; then a 44px box forced one column of sixteen rows on a phone),
// and even fixed they were ~650 px of controls above the first listing. The
// filters are chip BUTTONS in a sheet now - the Cosmetics tab's idiom - so the
// touch floor is app.css's button rule and nothing here has to fight it. This
// pins the pieces that keep it that way; check:touch measures the result on a
// dev box (overlap-check skips the Market, so measure by hand too).
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

describe('market filters (task 102)', () => {
  it('has no filter checkboxes left - the filters are chip buttons', () => {
    expect(market).not.toMatch(/type="checkbox"/);
    expect(market).toMatch(/<button\s+class="chip"/);
  });

  it('keeps the filters out of the DOM until asked for, with {#if} rather than <details>', () => {
    // client_web/CLAUDE.md: a closed <details> kept live buttons over the
    // Chest's list. The sheet is genuinely absent until opened.
    expect(market).toMatch(/\{#if filtersOpen\}\s*[\s\S]*?<DetailSheet/);
    expect(market).not.toMatch(/<details/);
  });

  it('lets no chip shrink below its own width in a wrapping row', () => {
    const chip = market.match(/\n\s*\.chip \{([^}]*)\}/);
    expect(chip, 'the .chip rule moved - update this test').not.toBeNull();
    expect(chip![1]).toMatch(/flex-shrink:\s*0/);
  });

  it('says "Sort:" beside the sort, so it does not read as a second rarity filter', () => {
    expect(market).toMatch(/<span>Sort:<\/span>/);
  });
});
