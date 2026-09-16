// client_web/tests/traits.test.ts
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { TraitDefinition } from '../src/lib/net/rest';
import { traitBitsOf, traitsOf, rarityClass, oddsSourceLabel } from '../src/lib/ui/traits';

const catalogue: TraitDefinition[] = [
  { Id: 4, Key: 'iron_blood', Name: 'Iron Blood', Description: '+8% max HP', Rarity: 'Rare', Effect: 'MaxHpPct', Value: 8 },
  { Id: 16, Key: 'thin_blood', Name: 'Thin Blood', Description: '-6% max HP', Rarity: 'Flaw', Effect: 'MaxHpPct', Value: -6 },
];

describe('traits', () => {
  it('reads bits without 32-bit bitwise overflow', () => {
    expect(traitBitsOf(0)).toEqual([]);
    expect(traitBitsOf(2 ** 4 + 2 ** 16)).toEqual([4, 16]);
    expect(traitBitsOf(2 ** 40)).toEqual([40]);
  });

  it('maps a mask to catalogue entries and skips unknown bits', () => {
    expect(traitsOf(2 ** 4 + 2 ** 16 + 2 ** 30, catalogue).map((t) => t.Name)).toEqual(['Iron Blood', 'Thin Blood']);
  });

  it('names rarity classes and odds sources for both pairings', () => {
    expect(rarityClass('Legendary')).toBe('trait-legendary');
    expect(oddsSourceLabel('both', 'roster')).toBe('both parents');
    expect(oddsSourceLabel('hero', 'village')).toBe('you');
    expect(oddsSourceLabel('partner', 'village')).toBe('them');
    expect(oddsSourceLabel('hero', 'roster')).toBe('the father');
    expect(oddsSourceLabel('partner', 'roster')).toBe('the mother');
  });

  // Modul: THE CATALOGUE IS SERVED, NEVER COPIED. A second copy of the table
  // is the two-sources-of-truth bug class this codebase keeps paying for.
  it('keeps no copy of the catalogue in client source', () => {
    const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
    const offenders: string[] = [];
    const walk = (dir: string) => {
      for (const name of readdirSync(dir).sort()) {
        const full = join(dir, name);
        if (statSync(full).isDirectory()) walk(full);
        else if (/\.(ts|svelte)$/.test(name) && /blood_of_kings|Blood of Kings|stout_heart/.test(readFileSync(full, 'utf8'))) {
          offenders.push(full);
        }
      }
    };
    walk(src);
    expect(offenders).toEqual([]);
  });
});
