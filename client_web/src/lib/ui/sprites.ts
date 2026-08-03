// Modul: turning a generated sprite path into a URL, and answering "is there
// art for this?" honestly.
//
// The tables are generated (see scripts/generate-sprites.mjs); this file is
// only the resolution rules, which are the part with judgement in them.

import { HTTP_BASE } from '../net/config';
import {
  MONSTER_ICONS,
  ITEM_ICONS,
  RACE_ICONS,
  TOOL_ICONS,
  CURRENCY_ICONS,
} from './sprites.generated';

/**
 * The sprite filenames contain SPACES and AMPERSANDS, because they were
 * authored for a Unity import rather than for a URL - "Tools&Equipment/Melee
 * weapons/Doom Edge.png". Each path segment is encoded separately so the
 * slashes survive; `encodeURI` on the whole path would leave the ampersand
 * intact and `encodeURIComponent` would destroy the slashes.
 */
function spriteUrl(relativePath: string): string {
  const encoded = relativePath.split('/').map(encodeURIComponent).join('/');
  return `${HTTP_BASE}/sprites/${encoded}`;
}

/**
 * Backgrounds and UI plates, by file name.
 *
 * Not generated: these are a fixed handful authored for specific screens
 * rather than a table keyed on content ids, so a generated map would only
 * restate the file names. See tools/prepare_backgrounds.py.
 */
export function backgroundUrl(name: string): string {
  return spriteUrl(`Backgrounds/${name}.webp`);
}

/** The five locations, in canon order, keyed the way locations.ts names them. */
export function locationBackground(locationIndex: number): string | null {
  const files = [
    'whispering_woods',
    'the_murky_swamps',
    'craggy_highlands',
    'ancient_ruins',
    'abyssal_breach',
  ];
  const file = files[locationIndex - 1];
  return file ? backgroundUrl(file) : null;
}

export function monsterIcon(monsterId: number): string | null {
  const path = MONSTER_ICONS[monsterId];
  return path ? spriteUrl(path) : null;
}

/**
 * Art for an item, by its BaseId.
 *
 * Returns null rather than a placeholder URL, so the caller decides how a
 * missing icon looks. About a fifth of the catalogue has no art - rings,
 * amulets and several gloves and helmets were never drawn - and pretending
 * otherwise with a generic box would make a real gap look like a load failure.
 */
export function itemIcon(baseItemId: string): string | null {
  const path = ITEM_ICONS[baseItemId];
  return path ? spriteUrl(path) : null;
}

export function raceIcon(raceId: number, female: boolean): string | null {
  const entry = RACE_ICONS[raceId];
  if (!entry) return null;
  const path = female ? entry.female : entry.male;
  return path ? spriteUrl(path) : null;
}

/**
 * Art for a gathering tool at a given tier.
 *
 * The tier is CLAMPED rather than trusted: `CachedCurrentToolTier` is written
 * from ForgeLevel server-side and nothing documents its range, so a tier past
 * the end of the ladder shows the last tool instead of vanishing.
 */
export function toolIcon(kind: 'axe' | 'pickaxe' | 'rod', tier: number): string | null {
  const ladder = TOOL_ICONS[kind];
  if (!ladder || ladder.length === 0) return null;
  const index = Math.max(0, Math.min(ladder.length - 1, Math.trunc(tier)));
  const path = ladder[index];
  return path ? spriteUrl(path) : null;
}

export function currencyIcon(kind: 'gold' | 'diamond'): string | null {
  const path = CURRENCY_ICONS[kind];
  return path ? spriteUrl(path) : null;
}

/**
 * Two letters for the placeholder tile, taken from the item's own words.
 *
 * Deliberately derived from the display name rather than the BaseId: "Copper
 * Bar" gives "CB", which a player can tie back to what they are looking at,
 * whereas the BaseId's leading token is often a material shared by a dozen
 * items.
 */
export function initialsFor(displayName: string): string {
  const words = displayName.split(/\s+/).filter(Boolean);
  if (words.length === 0) return '?';
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[1][0]).toUpperCase();
}
