import { describe, it, expect } from 'vitest';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  TAPS_TO_OPEN,
  initialChestState,
  oddsLine,
  pressChest,
  tapWord,
  CHEST_BACKGROUND,
  chestBackground,
} from '../src/lib/ui/chestOpening';

const here = dirname(fileURLToPath(import.meta.url));
const publicDir = join(here, '..', 'public');

describe('chest opening', () => {
  it('opens on the third press, and only the third sends the open', () => {
    let state = initialChestState();
    const sent: boolean[] = [];
    for (let i = 0; i < TAPS_TO_OPEN; i++) {
      const { next, sendOpen, shake } = pressChest(state);
      expect(shake).toBe(true);
      sent.push(sendOpen);
      state = next;
    }
    expect(sent).toEqual([false, false, true]);
    expect(state.phase).toBe('final-shake');
    // A fourth press while it opens does nothing - no second open.
    const extra = pressChest(state);
    expect(extra.sendOpen).toBe(false);
    expect(extra.shake).toBe(false);
  });

  it('says TAP on a touch screen and CLICK with a mouse', () => {
    expect(tapWord(true)).toBe('TAP!');
    expect(tapWord(false)).toBe('CLICK!');
  });

  it('every picture the stage asks for is in public/', () => {
    const files = [
      CHEST_BACKGROUND.landscape,
      CHEST_BACKGROUND.portrait,
      chestBackground('samhain').landscape,
      chestBackground('samhain').portrait,
    ];
    // The event's painting replaces the ordinary one, and only for a known event.
    expect(chestBackground('samhain')).not.toEqual(CHEST_BACKGROUND);
    expect(chestBackground(undefined)).toEqual(CHEST_BACKGROUND);
    expect(chestBackground('nonsense')).toEqual(CHEST_BACKGROUND);
    for (const file of files) expect(existsSync(join(publicDir, file)), file).toBe(true);
    // The deleted opening clips stay deleted.
    expect(existsSync(join(publicDir, 'chest', 'shake.webm'))).toBe(false);
  });

  it('words the odds row, skipping a zero', () => {
    const names = ['', 'Common', 'Rare', 'Epic', 'Legendary'];
    expect(oddsLine([0, 750, 200, 45, 5], names)).toBe('75% Common · 20% Rare · 4.5% Epic · 0.5% Legendary');
    expect(oddsLine([0, 0, 100, 300, 600], names)).toBe('10% Rare · 30% Epic · 60% Legendary');
    expect(oddsLine(undefined, names)).toBe('');
  });
});
