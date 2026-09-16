// client_web/src/lib/ui/traits.ts
// Modul: HERITABLE TRAITS, as the client shows them. The catalogue comes from
// the server (fetchTraits) and is never copied here - tests/traits.test.ts
// fails if a trait name appears in client source.
import type { TraitDefinition, TraitOddsEntry, TraitRarity } from '../net/rest';

/**
 * The bits set in a mask. Arithmetic, not `&`: JavaScript bitwise operators
 * truncate to 32 bits, and a mask is a C# long.
 */
export function traitBitsOf(mask: number): number[] {
  const bits: number[] = [];
  for (let bit = 0; bit < 53; bit++) {
    if (Math.floor(mask / 2 ** bit) % 2 === 1) bits.push(bit);
  }
  return bits;
}

export function traitsOf(mask: number, catalogue: readonly TraitDefinition[]): TraitDefinition[] {
  const byId = new Map(catalogue.map((t) => [t.Id, t]));
  return traitBitsOf(mask)
    .map((bit) => byId.get(bit))
    .filter((t): t is TraitDefinition => t !== undefined);
}

export function rarityClass(rarity: TraitRarity): string {
  return `trait-${rarity.toLowerCase()}`;
}

export function oddsSourceLabel(source: TraitOddsEntry['Source'], mode: 'village' | 'roster'): string {
  if (source === 'both') return 'both parents';
  if (mode === 'village') return source === 'hero' ? 'you' : 'them';
  return source === 'hero' ? 'the father' : 'the mother';
}
