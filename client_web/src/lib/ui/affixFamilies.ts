import { createQuery } from '@tanstack/svelte-query';
import { fetchAffixFamilies, queryKeys, type AffixFamilyInfo } from '../net/rest';
import { formatAffixValue } from './affixes';

// Modul: THE TREE'S MARK ON THE GEAR (phase 5.1, 2026-11-03). Every affix
// belongs to a family, and a family node in the tree makes it stronger. Which
// family and by how much both come from the server
// (/api/v1/skilltree/families) - this file only colours and formats.

/** The tree column colours (SkillsPanel's LIMB_HUE), so a chip matches its column. */
export const FAMILY_COLOR: Record<string, string> = {
  power: '#e2674a',
  precision: '#57c7e3',
  guard: '#7fb069',
  fortune: '#e8c15a',
  craft: '#d79a4e',
};

export interface FamilyIndex {
  /** affix id -> its family. */
  byAffix: Map<string, AffixFamilyInfo>;
}

export function indexFamilies(families: readonly AffixFamilyInfo[] | undefined): FamilyIndex {
  const byAffix = new Map<string, AffixFamilyInfo>();
  for (const f of families ?? []) for (const id of f.AffixIds) byAffix.set(id, f);
  return { byAffix };
}

/** The affix's value with its family boost, formatted like the raw value; null when there is no boost. */
export function boostedValue(affixId: string, magnitude: number, family: AffixFamilyInfo | undefined): string | null {
  if (!family || family.BoostTenths <= 0) return null;
  return formatAffixValue(affixId, Math.floor((magnitude * (1000 + family.BoostTenths)) / 1000));
}

/** The viewer's families, cached; refetched after a tree commit (see invalidateAffixFamilies). */
export function familiesQuery() {
  return createQuery(() => ({ queryKey: queryKeys.affixFamilies, queryFn: fetchAffixFamilies, staleTime: 60_000 }));
}
