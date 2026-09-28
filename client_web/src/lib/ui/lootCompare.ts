import type { ContentRegistry } from '../net/content';
import type { WornPiece } from '../net/rest';
import { ATTRIBUTES, equipRequirement } from '../net/commands';
import { powerMultiplier, rarityName } from './rarity';
import { EQUIPMENT_SLOTS, resolveSlotIndex } from './slots';

/*
 * Modul: TASK 49 - "is this drop better than what I wear?", answered on the
 * loot row itself. Pure, so the arithmetic is tested without a browser.
 *
 * Compares BASE power only, scaled by quality exactly as EquipmentSlotEngine
 * scales it. Affixes are deliberately left out: they are a vector of unlike
 * stats (crit, lifesteal, gathering speed) with no honest single sum, and a
 * line that folded them in would have to invent the exchange rate. The line
 * says what it measures - ATK, DEF and the rarity step - and nothing more.
 */

export interface PieceTotals {
  attack: number;
  defense: number;
}

export interface DropComparison {
  slotIndex: number;
  slotLabel: string;
  attackDelta: number;
  defenseDelta: number;
  /** Drop tier minus the worn piece's tier; the drop's own tier on an empty slot. */
  tierStep: number;
  worn: WornPiece | null;
}

/** C#'s Math.Round, which rounds a midpoint to EVEN - JS rounds it up. */
function roundHalfEven(value: number): number {
  const floor = Math.floor(value);
  const diff = value - floor;
  if (Math.abs(diff - 0.5) > 1e-9) return Math.round(value);
  return floor % 2 === 0 ? floor : floor + 1;
}

export function pieceTotals(registry: ContentRegistry | null, baseItemId: string, qualityTier: number): PieceTotals {
  const item = registry?.itemsByBaseId.get(baseItemId);
  if (!item) return { attack: 0, defense: 0 };
  const scale = powerMultiplier(qualityTier);
  return {
    attack: roundHalfEven(Number(item.FlatAttackPower ?? 0) * scale),
    defense: roundHalfEven(Number(item.FlatDefenseRating ?? 0) * scale),
  };
}

export function compareDrop(
  registry: ContentRegistry | null,
  baseItemId: string,
  qualityTier: number,
  worn: readonly WornPiece[],
): DropComparison | null {
  const slotIndex = resolveSlotIndex(baseItemId);
  if (slotIndex < 0) return null;

  const current = worn.find((p) => p.SlotIndex === slotIndex) ?? null;
  const next = pieceTotals(registry, baseItemId, qualityTier);
  const now = current ? pieceTotals(registry, current.BaseItemId, current.QualityTier) : { attack: 0, defense: 0 };

  return {
    slotIndex,
    slotLabel: EQUIPMENT_SLOTS.find((s) => s.index === slotIndex)?.label ?? 'Slot',
    attackDelta: next.attack - now.attack,
    defenseDelta: next.defense - now.defense,
    tierStep: qualityTier - (current?.QualityTier ?? 0),
    worn: current,
  };
}

function signed(n: number): string {
  return n > 0 ? `+${n}` : n < 0 ? `−${-n}` : '±0';
}

/** One line: "+12 ATK · −3 DEF · 2 tiers above your Rare". */
export function comparisonLine(c: DropComparison): string {
  const parts: string[] = [];
  if (c.attackDelta !== 0) parts.push(`${signed(c.attackDelta)} ATK`);
  if (c.defenseDelta !== 0) parts.push(`${signed(c.defenseDelta)} DEF`);
  if (!c.worn) {
    parts.push(`${c.slotLabel.toLowerCase()} slot is empty`);
  } else if (c.tierStep === 0) {
    parts.push(`same rarity as yours`);
  } else {
    const n = Math.abs(c.tierStep);
    parts.push(`${n} tier${n === 1 ? '' : 's'} ${c.tierStep > 0 ? 'above' : 'below'} your ${rarityName(c.worn.QualityTier)}`);
  }
  return parts.join(' · ');
}

/** Worth a green line: more power, or equal power and a rarer piece (more affixes). */
export function isUpgrade(c: DropComparison): boolean {
  const power = c.attackDelta + c.defenseDelta;
  if (!c.worn) return true;
  return power > 0 || (power === 0 && c.tierStep > 0);
}

/**
 * The attribute minimum a piece asks for, exactly as Character's picker shows
 * it (equipRequirement; region 1 asks for nothing). `attributes` is the
 * snapshot's STR/DEX/CON/LCK - account-wide, so any character's will do.
 */
export function dropRequirement(
  registry: ContentRegistry | null,
  baseItemId: string,
  attributes: Partial<Record<'STR' | 'DEX' | 'CON' | 'LCK', number>>,
): { text: string; met: boolean } | null {
  const def = registry?.itemsByBaseId.get(baseItemId);
  if (!def) return null;
  const need = equipRequirement(resolveSlotIndex(baseItemId), Number(def.RegionTier ?? 0));
  if (!need) return null;
  const attribute = ATTRIBUTES.find((a) => a.id === need.attribute);
  if (!attribute) return null;
  return {
    text: `needs ${need.minimum} ${attribute.label}`,
    met: Number(attributes[attribute.key] ?? 0) >= need.minimum,
  };
}
