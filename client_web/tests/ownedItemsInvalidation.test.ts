// Modul: A CALL SITE THAT INVALIDATES ONE OWNED-ITEMS KEY DIRECTLY IS A STALE
// SCREEN WAITING TO HAPPEN.
//
// invalidateOwnedItems (net/queryClient) exists because the chest is served by
// TWO REST routes answering from the same tables (the full equipment snapshot
// and the stacks-only materials route) - anything that changes what the
// player owns has to invalidate BOTH query keys, and a call site that
// remembers only one is a screen showing a count the server stopped agreeing
// with, with no error anywhere. Every mutation call site is already routed
// through the shared helper today; this test is the mechanical guard that a
// future call site cannot bypass it with a direct, partial
// `invalidateQueries({ queryKey: queryKeys.inventory })` (or `.materials`)
// the way `StateUpdatePacketFieldCoverageTests` guards the wire's equivalent
// "remembers one, forgets the other" defect class on the server side.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const helperFile = join(srcRoot, 'lib', 'net', 'queryClient.ts');

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...sourceFiles(full));
    else if (entry.name.endsWith('.svelte') || entry.name.endsWith('.ts')) out.push(full);
  }
  return out.sort();
}

describe('owned-items cache invalidation stays behind the shared helper', () => {
  const files = sourceFiles(srcRoot);

  it('finds source files at all - an empty sweep proves nothing', () => {
    expect(files.length).toBeGreaterThan(20);
  });

  it('invalidateOwnedItems itself still invalidates both keys', () => {
    const helperSource = readFileSync(helperFile, 'utf8');
    expect(helperSource).toContain('queryKeys.inventory');
    expect(helperSource).toContain('queryKeys.materials');
  });

  it('no call site invalidates inventory or materials directly, outside the helper', () => {
    // A direct `invalidateQueries({ queryKey: queryKeys.X })` naming one of
    // the two owned-items keys - matched loosely enough to survive
    // reformatting, since this is a source scan, not a parser.
    const directInvalidation = /invalidateQueries\(\s*\{\s*queryKey:\s*queryKeys\.(inventory|materials)\b/;

    const offenders = files
      .filter((file) => file !== helperFile)
      .filter((file) => directInvalidation.test(readFileSync(file, 'utf8')))
      .map((file) => relative(srcRoot, file));

    expect(
      offenders,
      `these files invalidate one owned-items key directly instead of calling invalidateOwnedItems(), ` +
        `so they risk forgetting the other key: ${offenders.join(', ')}`,
    ).toEqual([]);
  });
});
