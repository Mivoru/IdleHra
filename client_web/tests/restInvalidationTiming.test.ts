import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

/*
  Modul: a ratchet nobody watches fails open. Market/Larder/Character/Mailbox
  each deleted their own guessed-delay setTimeout invalidations because the
  server's own command-acknowledgment signal (game.ts's processCommandResults,
  driven by the CommandResult ring buffer) already covers every command these
  screens send. Nothing stops a future edit from reintroducing "just add a
  setTimeout, it's how the other screens do it" - this reads each file as text
  and refuses that pattern by name, the same approach tests/runesMode.test.ts
  and tests/serverMirrors.test.ts already use for a Svelte file this repo's
  vitest config cannot mount (environment: 'node', no @testing-library).
*/

const here = dirname(fileURLToPath(import.meta.url));

function readRoute(name: string): string {
  return readFileSync(join(here, '..', 'src', 'routes', name), 'utf8');
}

describe('Market.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout paired with invalidateOwnedItems, refetch, or invalidateQueries', () => {
    const source = readRoute('Market.svelte');
    const timedInvalidation =
      /setTimeout\([^)]*(invalidateOwnedItems|\.refetch\(\)|invalidateQueries)/s;
    expect(source).not.toMatch(timedInvalidation);
  });
});

describe('Larder.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout calling refetch', () => {
    const source = readRoute('Larder.svelte');
    expect(source).not.toMatch(/setTimeout\([^)]*\.refetch\(\)/s);
  });
});

describe('Character.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout calling refetch', () => {
    const source = readRoute('Character.svelte');
    expect(source).not.toMatch(/setTimeout\([^)]*\.refetch\(\)/s);
  });
});

describe('Mailbox.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout calling invalidateOwnedItems or a mailbox invalidateQueries', () => {
    const source = readRoute('Mailbox.svelte');
    const timedInvalidation =
      /setTimeout\([^)]*(invalidateOwnedItems|invalidateQueries)/s;
    expect(source).not.toMatch(timedInvalidation);
  });

  it('keeps claimAll\'s own command-staggering setTimeout, which is not the invalidation timer being guarded against', () => {
    const source = readRoute('Mailbox.svelte');
    expect(source).toMatch(/setTimeout\(\(\) => claimMailItem\(entry\.Id\), index \* 250\)/);
  });
});
