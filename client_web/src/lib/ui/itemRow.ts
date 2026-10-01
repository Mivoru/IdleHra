// Modul: the pure half of ItemRow (task 99), kept out of the component so it
// can be tested without a DOM.
//
// A row used to say "Hunter Amulet - Relic" and nothing else, so two Relic
// amulets, one of them worn, were the same row twice. The affix roll is the
// whole difference between them, and this is where it becomes a line of text.

import type { AffixMap, InventoryStack } from '../net/rest';
import { toDisplayAffixes } from './affixes';
import { rarityName } from './rarity';

/**
 * The commodity id gold is stored under (VillageManagementEngine.GoldItemId).
 *
 * Modul: GOLD IS NOT A MATERIAL, but it arrives as one. The inventory snapshot
 * lists every CommodityRecords row, and gold lives there as ItemId "gold", so
 * the Chest showed a guest "Gold 2000" with Sell all and Bin and counted it in
 * Materials. The server does not refuse either: /api/v1/chest/sell and
 * /api/v1/chest/discard both run RemoveMaterialAsync, which takes the gold
 * out of the row, and since "gold" has no items.json entry ValueMaterial
 * prices it at 0 - so "Sell all" on Gold destroyed the player's gold for
 * nothing, exactly like Bin. Gold is shown in the header, not here.
 */
export const GOLD_ITEM_ID = 'gold';

/** A stack that belongs in the Chest's materials list. */
export function isChestMaterial(stack: InventoryStack): boolean {
  return stack.Quantity > 0 && stack.ItemId !== GOLD_ITEM_ID;
}

/**
 * The strongest affixes in a few words: "+1.2% Crit Chance, +40 Hp, 2 more".
 *
 * "Strongest" is the affix's own five-tier rarity, highest first; ties keep
 * the payload's order so a row does not reshuffle between renders. The labels
 * and values are the Forge's (toDisplayAffixes), so a row and the Inspect
 * panel cannot describe the same affix two ways.
 */
export function summarizeAffixes(affixes: AffixMap | null | undefined, shown = 2): string {
  if (!affixes) return '';
  const rows = toDisplayAffixes(affixes)
    .map((row, index) => ({ row, index }))
    .sort((a, b) => b.row.rarity - a.row.rarity || a.index - b.index)
    .map(({ row }) => row);
  if (rows.length === 0) return '';

  const head = rows.slice(0, shown).map((r) => `${r.value} ${r.label}`);
  const rest = rows.length - head.length;
  return rest > 0 ? `${head.join(', ')}, ${rest} more` : head.join(', ');
}

/**
 * The second line of an item row: region tier, rarity, top affixes. Parts that
 * are unknown (tier 0, no rarity, no affixes) are left out rather than shown
 * as zeros.
 */
export function itemMetaLine(parts: {
  regionTier?: number;
  qualityTier?: number;
  affixes?: AffixMap | null;
  extra?: string;
}): string {
  const out: string[] = [];
  if (parts.regionTier && parts.regionTier > 0) out.push(`T${parts.regionTier}`);
  if (parts.qualityTier && parts.qualityTier > 0) out.push(rarityName(parts.qualityTier));
  const head = out.join(' ');
  const tail = [summarizeAffixes(parts.affixes), parts.extra ?? ''].filter((s) => s !== '');
  return [head, ...tail].filter((s) => s !== '').join(' - ');
}

/**
 * Worn pieces first, as their own group. Order inside each half is kept, so
 * a caller that sorted by rarity still has both halves sorted by rarity.
 */
export function splitWorn<T extends { IsEquipped: boolean }>(items: readonly T[]): { worn: T[]; loose: T[] } {
  const worn: T[] = [];
  const loose: T[] = [];
  for (const item of items) (item.IsEquipped ? worn : loose).push(item);
  return { worn, loose };
}
