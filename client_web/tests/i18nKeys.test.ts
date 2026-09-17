// Modul: A KEY WRITTEN ON ONE SIDE AND READ ON THE OTHER, again - the shape
// serverMirrors.test.ts exists for, applied to the translation table instead
// of a balance constant. `i18n.ts`'s own `t()`/`translate()` return the KEY
// ITSELF for an unknown lookup ("visibly wrong rather than invisibly
// missing") precisely because a call site can reference a key that was
// renamed or never added - this test exists so that is caught here, once,
// rather than read live in a language nobody proofreads by eye.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const srcRoot = join(repoRoot, 'client_web', 'src');
const i18nModulePath = join('lib', 'ui', 'i18n.ts');

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) walk(full, out);
    else if (extname(entry) === '.svelte' || extname(entry) === '.ts') out.push(full);
  }
  return out;
}

/** Every key passed to `$t(...)` or `translate(...)` anywhere in the client,
 *  found by a plain regex over source - i18n.ts itself is excluded, since it
 *  DEFINES those functions rather than calling them. */
function referencedKeys(): Set<string> {
  const keys = new Set<string>();
  const pattern = /(?:\$t|translate)\(\s*'([A-Za-z0-9_]+)'\s*\)/g;
  for (const file of walk(srcRoot)) {
    if (file.endsWith(i18nModulePath)) continue;
    const text = readFileSync(file, 'utf8');
    for (const m of text.matchAll(pattern)) keys.add(m[1]);
  }
  return keys;
}

describe('every translation key the client asks for exists in the table', () => {
  it('has a row in localizations.json for each $t()/translate() call', () => {
    const rows = JSON.parse(
      readFileSync(join(repoRoot, 'server', 'GameData', 'localizations.json'), 'utf8').replace(/^﻿/, ''),
    ) as { Key: string }[];
    const known = new Set(rows.map((r) => r.Key));

    const used = referencedKeys();
    expect(
      used.size,
      'no $t()/translate() call sites found at all - the scan pattern needs updating, not deleting',
    ).toBeGreaterThan(0);

    const missing = [...used].filter((k) => !known.has(k)).sort();
    expect(missing, `key(s) referenced but not in localizations.json: ${missing.join(', ')}`).toEqual([]);
  });
});
