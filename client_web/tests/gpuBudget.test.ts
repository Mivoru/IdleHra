// Modul: TASK 108 - NO ENDLESS PAINT ANIMATION.
//
// `.rarity-glow` animated text-shadow and CosmeticFrame animated
// filter: drop-shadow, both infinite, both on repeated list items (every
// tier-10+ row, every avatar in chat and on the boards). Neither property runs
// on the compositor, so every one of those elements repainted every frame for
// as long as it was on screen. Nothing in the toolchain notices: the page looks
// right and a desktop GPU shrugs it off.
//
// The rule this file holds: an animation that runs for ever may animate only
// what the compositor can do alone (opacity, transform, ...). A glow pulses by
// fading a pre-drawn layer, never by redrawing the shadow. Same file only -
// every keyframe in this client is declared beside the rule that runs it.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

const REPAINTING = /\b(text-shadow|filter|box-shadow|backdrop-filter)\s*:/;

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...sourceFiles(full));
    else if (/\.(svelte|css)$/.test(entry.name)) out.push(full);
  }
  return out;
}

/** The body of `@keyframes name { ... }`, braces matched. */
function keyframesBody(source: string, name: string): string | null {
  const at = source.search(new RegExp(`@keyframes\\s+${name}\\s*\\{`));
  if (at < 0) return null;
  let depth = 0;
  for (let i = source.indexOf('{', at); i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(at, i + 1);
  }
  return null;
}

describe('the phone GPU budget', () => {
  const files = sourceFiles(srcRoot);

  it('finds the sources at all - an empty sweep proves nothing', () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it('has no infinite animation of a property that repaints', () => {
    const offenders: string[] = [];
    let infinite = 0;
    for (const file of files) {
      const source = readFileSync(file, 'utf8');
      for (const m of source.matchAll(/animation\s*:\s*([^;]*\binfinite\b[^;]*);/g)) {
        infinite++;
        const name = m[1].trim().split(/\s+/).find((token) => keyframesBody(source, token) !== null);
        const body = name ? keyframesBody(source, name) : null;
        if (body && REPAINTING.test(body)) offenders.push(`${relative(srcRoot, file)}: ${name}`);
      }
    }
    expect(infinite, 'the scan found no infinite animation at all - the pattern needs updating').toBeGreaterThan(0);
    expect(offenders).toEqual([]);
  });

  it('keeps .rarity-glow itself still - lists get it, and lists must not repaint', () => {
    const css = readFileSync(join(srcRoot, 'app.css'), 'utf8');
    const rule = css.match(/\n\.rarity-glow\s*\{([^}]*)\}/);
    expect(rule, '.rarity-glow rule').not.toBeNull();
    expect(rule![1]).not.toMatch(/animation/);
  });
});
