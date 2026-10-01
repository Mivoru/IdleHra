// Modul: A CSS CUSTOM PROPERTY NOBODY DEFINES FAILS IN SILENCE.
//
// `color: var(--bad)` with no `--bad` anywhere is not an error in any tool this
// project runs. The declaration is "invalid at computed-value time", so the
// property falls back to inherited/initial and the page renders - just wrong.
// Components had invented eleven such names (--bad, --err, --ok, --success,
// --fg, --dim, --bg-dark, --bg-hover, --panel, --bg-surface, --bg-light):
//   - the Settings email error was not red, so it did not read as an error;
//   - the admin panel's border and the ban button's red were simply absent;
//   - the player profile modal fell back to a hard-coded #1e1e1e box, which
//     the light theme fills with dark-brown text - dark on dark;
//   - and where a fallback WAS given it was a foreign colour, so one screen
//     showed three different reds for "bad".
//
// svelte-check and vite are both blind to it, so the check is a grep: every
// var(--x) in src must be defined in app.css (the theme) or set in the same
// file (a scoped token, an inline style, style:--x, setProperty).
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

// Injected on the root element by Capacitor's SystemBars at runtime, so they
// are defined - just never in a file. Every read of them carries an env()
// fallback for the browser (see the --sa-* note in app.css).
const RUNTIME_INJECTED = new Set([
  '--safe-area-inset-top',
  '--safe-area-inset-right',
  '--safe-area-inset-bottom',
  '--safe-area-inset-left',
]);

// Files whose undefined tokens are being fixed on another branch. Empty now
// that the overlays/nav work has landed; add an entry only with a branch
// that removes it again.
const PENDING_ELSEWHERE = new Set<string>();

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...sourceFiles(full));
    else if (/\.(svelte|css|ts)$/.test(entry.name)) out.push(full);
  }
  return out;
}

// Comments only, so a note ABOUT a dead token ("it read var(--bg-dark)") does
// not count as a use of it. `//` is stripped only after whitespace or at a
// line start, which keeps the `//` inside a url("data:...http://...") intact.
function stripComments(text: string): string {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/(^|\s)\/\/.*$/gm, '$1');
}

function definedIn(text: string): Set<string> {
  const names = new Set<string>();
  for (const m of text.matchAll(/(?<![\w-])(--[\w-]+)\s*:/g)) names.add(m[1]);
  for (const m of text.matchAll(/style:(--[\w-]+)/g)) names.add(m[1]);
  for (const m of text.matchAll(/setProperty\(\s*['"`](--[\w-]+)/g)) names.add(m[1]);
  return names;
}

describe('every CSS custom property that is read is defined', () => {
  const files = sourceFiles(srcRoot);
  const theme = definedIn(stripComments(readFileSync(join(srcRoot, 'app.css'), 'utf8')));

  it('finds files and theme tokens at all - an empty sweep proves nothing', () => {
    expect(files.length).toBeGreaterThan(50);
    expect(theme.has('--danger')).toBe(true);
    expect(theme.has('--bg-sunken')).toBe(true);
  });

  it('has no var(--x) whose --x is defined nowhere', () => {
    const offenders: string[] = [];
    for (const file of files) {
      const rel = relative(srcRoot, file).replace(/\\/g, '/');
      if (PENDING_ELSEWHERE.has(rel)) continue;
      const text = stripComments(readFileSync(file, 'utf8'));
      const local = definedIn(text);
      // `[\w-]+` stops at `${`, so a computed name like `--rarity-${tier}`
      // reads as the prefix `--rarity-` and is matched against the family.
      for (const m of text.matchAll(/var\(\s*(--[\w-]+)/g)) {
        const name = m[1];
        if (theme.has(name) || local.has(name) || RUNTIME_INJECTED.has(name)) continue;
        if (name.endsWith('-') && [...theme].some((t) => t.startsWith(name))) continue;
        offenders.push(`${rel}: ${name}`);
      }
    }
    expect(offenders, `undefined CSS custom properties - use a theme token from app.css:\n${offenders.join('\n')}`).toEqual([]);
  });
});
