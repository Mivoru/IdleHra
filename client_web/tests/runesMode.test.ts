// Modul: A LEGACY COMPONENT IN A RUNES PROJECT IS A SILENT REACTIVITY BUG.
//
// Svelte 5 decides per FILE: a component is in runes mode only if it uses a
// rune. `export let` is the Svelte 4 form, compiles without a warning, and puts
// that one file back on the compiler's old invalidation system.
//
// Which is fine on its own, and not fine the moment the file touches anything
// rune-based. `PlayerProfileModal.svelte` did both: `export let playerId` made
// it legacy, and `createQuery` from @tanstack/svelte-query builds its result
// out of runes. So the template read `profile.isPending` once at mount and
// never heard the request finish - reported from a phone as "I click show
// profile, it says fetching data, and I have to close it and click again". The
// second click worked because the query cache was warm by then and the first
// render already had the data, which is what made it look intermittent instead
// of broken.
//
// Nothing catches this. It is not a type error, it is not a deprecation
// warning, and `svelte-check` is silent: it is two reactivity systems
// disagreeing at runtime. So the check is "is any component still legacy",
// which is a grep, and it is worth being a grep.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

function svelteFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...svelteFiles(full));
    else if (entry.name.endsWith('.svelte')) out.push(full);
  }
  return out;
}

describe('every component is in runes mode', () => {
  const files = svelteFiles(srcRoot);

  it('finds components at all - an empty sweep proves nothing', () => {
    expect(files.length).toBeGreaterThan(20);
  });

  it('declares props with $props(), never with `export let`', () => {
    // `export let` inside the <script> of a .svelte file is the legacy prop
    // form. Matched at a line start (allowing indentation) so an `export let`
    // inside a string or a comment about this very rule does not trip it.
    const offenders = files
      .filter((file) => /^\s*export\s+let\s/m.test(readFileSync(file, 'utf8')))
      .map((file) => relative(srcRoot, file));

    expect(
      offenders,
      `legacy Svelte 4 props - convert to $props(): ${offenders.join(', ')}`,
    ).toEqual([]);
  });
});
