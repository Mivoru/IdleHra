// Modul: THE WATCH SPUN WHOLE, TWICE. First because the animation was on the
// <svg>; then (task 28) on a phone whose over-the-air bundle predated the fix.
// This pins the shape that makes the pivot engine-independent: hands drawn
// about the origin inside translate(12 13), the animation on the hand only,
// and no second hand-rolled copy in Village.
//
// Local-only: CI (deploy.yml) does not run vitest. Run `npm test`.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const watch = readFileSync(join(src, 'lib/ui/Stopwatch.svelte'), 'utf8');
const village = readFileSync(join(src, 'routes/Village.svelte'), 'utf8');

describe('stopwatch', () => {
  it('draws the hand about the origin inside translate(12 13)', () => {
    expect(watch).toMatch(/<g transform="translate\(12 13\)">\s*<path class="hand" d="M0 /);
  });

  it('animates the hand, never the svg or the group', () => {
    expect(watch).toMatch(/\.hand\s*\{[^}]*animation:/);
    expect(watch).not.toMatch(/(svg|\bg|\.stopwatch)\s*\{[^}]*animation:/);
  });

  it('Village has no hand-rolled copy left', () => {
    expect(village).not.toMatch(/class="hand"/);
    expect(village).toMatch(/<Stopwatch\b/);
  });
});
