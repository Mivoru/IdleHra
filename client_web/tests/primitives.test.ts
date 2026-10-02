// Modul: TASK 106 / 110 - THE SHARED RULES IN app.css, HELD BY A GREP.
//
// Each of these was a decision that a later edit could quietly undo without
// any checker noticing at the default 390px: a touch floor that only fires on
// a narrow viewport, a rarity glow that drifts back to the tier's own red, a
// thirteenth copy of .tiny-btn. The geometry checks need a running stack;
// these need nothing.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const appCss = readFileSync(join(srcRoot, 'app.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');

function svelteFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...svelteFiles(full));
    else if (entry.name.endsWith('.svelte')) out.push(full);
  }
  return out;
}
const svelte = svelteFiles(srcRoot).map((f) => ({ f, text: readFileSync(f, 'utf8') }));

describe('app.css shared rules', () => {
  it('applies the 44px touch floor to a coarse pointer as well as a phone width (110a)', () => {
    const floor = /@media ([^{]+)\{\s*button:not\(\.touch-exempt\),/.exec(appCss);
    expect(floor, 'the global touch floor block').not.toBeNull();
    expect(floor![1]).toContain('(max-width: 40rem)');
    expect(floor![1]).toContain('(pointer: coarse)');
  });

  it('draws the top-tier glow in gold, not in the tier colour (110f)', () => {
    const glow = /\.rarity-glow\s*\{([^}]*)\}/.exec(appCss);
    expect(glow![1]).toContain('var(--rarity-sheen)');
    expect(glow![1]).not.toContain('currentColor');
    const halo = /\.rarity-glow-live::after\s*\{([^}]*)\}/.exec(appCss);
    expect(halo![1]).toContain('var(--rarity-sheen)');
  });

  it('defines the .panel surface once, in app.css (106)', () => {
    expect(appCss).toMatch(/\.panel \{\s*background: var\(--bg-panel\);/);
    const copies = svelte
      .filter(({ text }) => /\.panel \{[^}]*background: var\(--bg-panel\)/.test(text))
      .map(({ f }) => f);
    expect(copies).toEqual([]);
  });

  it('defines .tiny-btn / .btn-sm once, in app.css (106)', () => {
    const copies = svelte.filter(({ text }) => /^\s*\.(tiny-btn|btn-sm)\s*[,{]/m.test(text)).map(({ f }) => f);
    expect(copies).toEqual([]);
    expect(appCss).toMatch(/\.tiny-btn,\s*\.btn-sm\s*\{/);
  });

  it('lets a panel fade in without staying a stacking context (106)', () => {
    expect(appCss).toMatch(/\.panel \{\s*animation: folk-rise [^;]* backwards;/);
  });

  it('rings every keyboard-reachable thing on focus-visible (106)', () => {
    for (const sel of ['a:focus-visible', 'summary:focus-visible', "[role='button']:focus-visible", '[tabindex]']) {
      expect(appCss).toContain(sel);
    }
  });
});
