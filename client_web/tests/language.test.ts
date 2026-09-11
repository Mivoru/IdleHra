// Modul: A PARTIAL TRANSLATION READS AS A BUG, BECAUSE IT IS INDISTINGUISHABLE
// FROM ONE.
//
// Reported from a phone: "why is everything in English and then it says
// Aktivní event: Zlatá sklizeň". The client was reading `navigator.language`
// and switching to Czech for any Czech device, which sounds hospitable until
// you count what is actually translated - 25 strings, against more than 500
// English ones the components carry inline. The result was not a Czech game.
// It was an English game with a handful of Czech words in it.
//
// These tests pin the two halves of the decision: the device is not asked, and
// an explicit choice still wins.
import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

const store = new Map<string, string>();

beforeEach(() => {
  vi.resetModules();
  store.clear();
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  });
  // A Czech device, which is what produced the report.
  vi.stubGlobal('navigator', { language: 'cs-CZ' });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('which language a fresh install starts in', () => {
  it('is English on a Czech device, because the table cannot dress the UI', async () => {
    const { initLanguage, language } = await import('../src/lib/ui/i18n');
    const { get } = await import('svelte/store');

    initLanguage();
    expect(get(language)).toBe('En');
  });

  it('still honours a choice the player actually made', async () => {
    const { initLanguage, setLanguage, language } = await import('../src/lib/ui/i18n');
    const { get } = await import('svelte/store');

    setLanguage('Cs');
    initLanguage();
    expect(get(language)).toBe('Cs');
  });
});

describe('the translation table itself', () => {
  it('is still too small to dress the interface - the reason the default is English', () => {
    // Modul: THIS IS THE TEST FOR THE DECISION ABOVE, not decoration. If
    // somebody translates the client properly this fails, and the failure is
    // the prompt to put `navigator.language` back. Assert on a measurement or
    // it is decoration - this repository's own rule.
    const rows = JSON.parse(
      readFileSync(join(repoRoot, 'server', 'GameData', 'localizations.json'), 'utf8').replace(/^﻿/, ''),
    ) as { Key: string; En: string; Cs: string }[];

    expect(rows.length).toBeLessThan(200);
  });
});
