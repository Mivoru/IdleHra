// Modul: WHAT A RECIPE CARD SAYS, kept pure (task 101).
//
// A new player met "0 of 30 craftable now" over thirty faded cards, every
// button disabled and nothing saying what to do; level-locked and missing-
// materials cards looked the same; every card printed "Equipment". This file
// sorts recipes into Ready / Missing materials / Locked, names the slot and
// the effect of what a recipe makes, and writes the one line that tells an
// empty-handed player where the first tools come from.

import type { CraftingRecipe } from '../net/rest';
import { TOOL_TIERS } from './wikiData';
import { prettifyBaseId } from '../net/content';

export type RecipeGroup = 'ready' | 'missing' | 'locked';

/** A craft press needs this many units' worth of each material. */
export function affordableUnits(recipe: CraftingRecipe, cap: number): number {
  const one = recipe.Mat1Id === 0 ? Infinity : Math.floor(recipe.Mat1CurrentStock / Math.max(1, recipe.Mat1Count));
  const two = recipe.Mat2Id === 0 ? Infinity : Math.floor(recipe.Mat2CurrentStock / Math.max(1, recipe.Mat2Count));
  const units = Math.min(one, two);
  return Number.isFinite(units) ? units : cap;
}

export function recipeGroup(recipe: CraftingRecipe, playerLevel: number): RecipeGroup {
  if (playerLevel < recipe.RequiredLevel) return 'locked';
  return affordableUnits(recipe, 1) >= 1 ? 'ready' : 'missing';
}

const TOOL_KINDS: readonly { marker: string; slot: string; profession: string }[] = [
  // Order matters: "_pickaxe_" contains "_axe_" (ContentRegistry.GetToolKind
  // tests pickaxe and rod before axe for the same reason).
  { marker: '_pickaxe_', slot: 'Pickaxe', profession: 'mining' },
  { marker: '_fishing_rod_', slot: 'Rod', profession: 'fishing' },
  { marker: '_axe_', slot: 'Axe', profession: 'woodcutting' },
];

/**
 * The slot a crafted tool goes in and what it does there: "Axe - +35%
 * woodcutting speed". The tier is ContentRegistry.GetToolTier - the leading
 * wood token, longest match first ("golden_birch_" before "birch_") - and the
 * speed is GatheringToolEngine's table (TOOL_TIERS). Null for anything that is
 * not a tool.
 */
export function toolEffect(baseItemId: string): { slot: string; effect: string } | null {
  if (!baseItemId.endsWith('_tool')) return null;
  const kind = TOOL_KINDS.find((k) => baseItemId.includes(k.marker));
  if (!kind) return null;
  const tier = [...TOOL_TIERS].sort((a, b) => b.slug.length - a.slug.length).find((t) => baseItemId.startsWith(`${t.slug}_`));
  if (!tier) return { slot: kind.slot, effect: `starter ${kind.profession} tool` };
  return { slot: kind.slot, effect: `+${tier.speedPct}% ${kind.profession} speed` };
}

/**
 * "Nothing craftable yet - the first tools need Birch Log and Copper Ore."
 * Built from the lowest-level recipe the player can already make, so the line
 * names real materials from the real table rather than a guess, and stays
 * true when the recipe list changes.
 */
export function firstStepLine(recipes: readonly CraftingRecipe[], playerLevel: number): string {
  const open = recipes.filter((r) => r.RequiredLevel <= playerLevel).sort((a, b) => a.RequiredLevel - b.RequiredLevel);
  const first = open[0];
  if (!first) return 'Nothing craftable yet - the first recipes open as your level rises.';
  const mats = [
    first.Mat1Id !== 0 ? prettifyBaseId(first.Mat1BaseItemId) : '',
    first.Mat2Id !== 0 ? prettifyBaseId(first.Mat2BaseItemId) : '',
  ].filter((m) => m !== '');
  const unique = [...new Set(mats)];
  if (unique.length === 0) return 'Nothing craftable yet.';
  return `Nothing craftable yet - the first tools need ${unique.join(' and ')}.`;
}
