// Modul: THE CLIENT HOLDS NO LIST OF TITLES (task 37).
//
// A title is a slug on the server and a display name the server sends. The
// plan's fallback was a client copy checked element by element against
// TitleRegistry, the way serverMirrors.test.ts guards the affix ordering - but
// the better answer is that no copy exists at all, so there is nothing to
// drift. This test holds that line: no display name from the server's registry
// may appear anywhere in the client source, and the slugs are only ever sent,
// never looked up.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = join(here, '..', 'src');
const registryPath = join(here, '..', '..', 'server', 'FolkIdle.Server', 'Domain', 'Progression', 'TitleRegistry.cs');

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...sourceFiles(full));
    else if (/\.(ts|svelte)$/.test(entry.name)) out.push(full);
  }
  return out;
}

const registry = readFileSync(registryPath, 'utf8');
const titles = [...registry.matchAll(/new TitleDefinition\("([a-z0-9_]+)", "([^"]+)", (\d+)\)/g)].map((m) => ({
  slug: m[1],
  name: m[2],
}));

describe('titles', () => {
  it('reads the server registry (the regex still matches it)', () => {
    expect(titles.length).toBeGreaterThanOrEqual(6);
    expect(titles.map((t) => t.slug)).toContain('deep_10');
  });

  it('keeps no copy of any display name in the client', () => {
    const offenders: string[] = [];
    for (const file of sourceFiles(srcRoot)) {
      const text = readFileSync(file, 'utf8');
      for (const t of titles) {
        if (text.includes(`'${t.name}'`) || text.includes(`"${t.name}"`) || text.includes(`>${t.name}<`)) {
          offenders.push(`${file}: ${t.name}`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });

  it('keeps no slug-to-name table in the client', () => {
    const offenders: string[] = [];
    for (const file of sourceFiles(srcRoot)) {
      const text = readFileSync(file, 'utf8');
      for (const t of titles) {
        if (text.includes(`${t.slug}:`) || text.includes(`'${t.slug}'`) || text.includes(`"${t.slug}"`)) {
          offenders.push(`${file}: ${t.slug}`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });
});
