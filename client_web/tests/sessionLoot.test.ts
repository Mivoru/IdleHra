// Modul: task 27. The loot <ul> is a max-height flex column and rare rows
// are scroll containers (.folk-sweep sets overflow: hidden), so without
// flex: none on the row the top - best - rows are shrunk to their padding.
// check:clipping measures this on a dev box ("flex-item-squashed-vertically");
// this pins it where no browser is available.
//
// Local-only: CI (deploy.yml) does not run vitest. Run `npm test`.
import { it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const file = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'lib', 'ui', 'SessionLoot.svelte'),
  'utf8',
);

it('loot rows cannot be shrunk by their flex-column list', () => {
  const li = file.match(/\n\s{2}li \{([^}]*)\}/);
  expect(li, 'the li rule moved - update this test, do not delete it').not.toBeNull();
  expect(li![1]).toMatch(/flex:\s*none|flex-shrink:\s*0/);
});
