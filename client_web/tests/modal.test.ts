// Modul: TASK 106 - EVERY MODAL IS A `Modal`, and this list only shrinks.
//
// Nine overlays each drew their own backdrop, with no focus management and
// four different scrims. Modal.svelte is the one shell now (portal, inert app
// root, focus trap and return, the closer stack, --scrim, the dvh cap). The
// files below still hand-roll a dialog and are migrated by the task named
// beside them; a NEW hand-rolled dialog fails here instead of becoming the
// tenth copy.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

const NOT_YET_MODAL = new Set([
  // A side sheet with its own swipe/scrim; the Modal sheet variant can absorb it.
  'lib/ui/DetailSheet.svelte',
  // A tutorial spotlight, not a dialog card - it must let the target show through.
  'lib/ui/GuidedOverlay.svelte',
  // Task 105 owns the shield wheel.
  'lib/ui/ShieldWheel.svelte',
]);

function svelteFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...svelteFiles(full));
    else if (entry.name.endsWith('.svelte')) out.push(full);
  }
  return out;
}

describe('modal dialogs', () => {
  const rel = (f: string) => relative(srcRoot, f).replace(/\\/g, '/');
  const declaring = svelteFiles(srcRoot)
    .filter((f) => /aria-modal|role="dialog"/.test(readFileSync(f, 'utf8').replace(/<!--[\s\S]*?-->/g, '')))
    .map(rel)
    .filter((f) => f !== 'lib/ui/Modal.svelte');

  it('declares a dialog only in Modal.svelte or a file still waiting to migrate', () => {
    expect(declaring.filter((f) => !NOT_YET_MODAL.has(f))).toEqual([]);
  });

  it('keeps the waiting list honest - a migrated file comes off it', () => {
    expect([...NOT_YET_MODAL].filter((f) => !declaring.includes(f))).toEqual([]);
  });

  it('migrated the six task 106 named', () => {
    for (const f of [
      'lib/ui/DeathCard.svelte',
      'lib/ui/VictoryCard.svelte',
      'lib/ui/WhatsNew.svelte',
      'lib/ui/OfflineSummary.svelte',
      'lib/ui/PlayerProfileModal.svelte',
      'App.svelte',
    ]) {
      expect(readFileSync(join(srcRoot, f), 'utf8'), f).toMatch(/<Modal\b/);
    }
  });
});
