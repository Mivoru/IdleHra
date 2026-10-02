// Modul: TASK 106 / 110 - THE SHARED RULES IN app.css, HELD BY A GREP.
//
// Each of these was a decision that a later edit could quietly undo without
// any checker noticing at the default 390px: a touch floor that only fires on
// a narrow viewport, a rarity glow that drifts back to the tier's own red, a
// thirteenth copy of .tiny-btn. The geometry checks need a running stack;
// these need nothing.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const appCss = readFileSync(join(srcRoot, 'app.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');

describe('app.css shared rules', () => {
  it('applies the 44px touch floor to a coarse pointer as well as a phone width (110a)', () => {
    const floor = /@media ([^{]+)\{\s*button:not\(\.touch-exempt\),/.exec(appCss);
    expect(floor, 'the global touch floor block').not.toBeNull();
    expect(floor![1]).toContain('(max-width: 40rem)');
    expect(floor![1]).toContain('(pointer: coarse)');
  });
});
