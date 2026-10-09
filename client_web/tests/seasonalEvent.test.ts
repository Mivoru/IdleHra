import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { EVENT_THEME_STORAGE_KEY } from '../src/lib/ui/eventTheme';
import { eventThemeKey } from '../src/lib/net/seasonalEvent';

// The seasonal event's theme is read in two places that cannot share an
// import: eventTheme.ts (the bundle) writes the key, boot.js (a classic script
// that runs before the bundle) reads it. These pin the two together, and the
// event loading art to the files boot.js points at.
const root = resolve(__dirname, '..');
const boot = readFileSync(resolve(root, 'public', 'boot.js'), 'utf8');

describe('seasonal event theme', () => {
  it('boot.js reads the key eventTheme.ts writes', () => {
    expect(boot).toContain(`localStorage.getItem('${EVENT_THEME_STORAGE_KEY}')`);
  });

  it('boot.js accepts the theme every known event wears', () => {
    const key = eventThemeKey(1);
    expect(key).toBe('samhain');
    expect(boot).toContain(`eventKey === '${key}'`);
  });

  it('every AVIF the loading screen names has an event copy', () => {
    const html = readFileSync(resolve(root, 'index.html'), 'utf8');
    const avifs = [...html.matchAll(/\/loading\/((?:portrait|landscape)-\d+)\.avif/g)].map((m) => m[1]);
    expect(avifs.length).toBeGreaterThan(0);
    for (const name of avifs) {
      expect(existsSync(resolve(root, 'public', 'loading', `samhain-${name}.avif`)), name).toBe(true);
    }
  });

  it('app.css themes the event', () => {
    const css = readFileSync(resolve(root, 'src', 'app.css'), 'utf8');
    expect(css).toContain(":root[data-event='samhain']");
  });
});
