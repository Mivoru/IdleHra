// Modul: the URL rule for artwork, without the generated tables. sprites.ts
// re-exports backgroundUrl and resolves the generated paths through spriteUrl;
// the login form imports this file directly, so the landing page does not
// download sprites.generated.ts.

import { SPRITE_BASE } from '../net/config';

/**
 * The sprite filenames contain SPACES and AMPERSANDS, because they were
 * authored for a Unity import rather than for a URL - "Tools&Equipment/Melee
 * weapons/Doom Edge.png". Each path segment is encoded separately so the
 * slashes survive; `encodeURI` on the whole path would leave the ampersand
 * intact and `encodeURIComponent` would destroy the slashes.
 */
export function spriteUrl(relativePath: string): string {
  const encoded = relativePath.split('/').map(encodeURIComponent).join('/');
  return `${SPRITE_BASE}/sprites/${encoded}`;
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
