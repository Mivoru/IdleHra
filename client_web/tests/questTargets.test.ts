import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { SCREEN_KEYS } from '../src/lib/ui/screens';

/*
  THE SERVER NAMES THE BUTTON, THE CLIENT HAS TO WEAR THE NAME.

  QuestLineRegistry (C#) lists, per quest step, the `data-guide` values the
  spotlight should light. Those strings are a position-free contract between
  two codebases: rename the attribute in a screen, or add a step on the server
  and forget the screen, and the step still works - the ring just never
  appears, silently. This is the mechanical guard CLAUDE.md asks for on a
  contract that crosses the wire as a name.

  The FIRST target of every step must exist as an attribute somewhere in src/;
  later targets are fallbacks for a screen state where the button is absent.
*/
const here = dirname(fileURLToPath(import.meta.url));
const registry = readFileSync(
  join(here, '..', '..', 'server', 'FolkIdle.Server', 'Domain', 'Progression', 'QuestLineRegistry.cs'),
  'utf8',
);

function sources(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) sources(full, out);
    else if (full.endsWith('.svelte')) out.push(full);
  }
  return out;
}
const svelte = sources(join(here, '..', 'src')).map((p) => readFileSync(p, 'utf8')).join('\n');

describe('the quest line targets', () => {
  const steps = [...registry.matchAll(/new\(\s*(\w+),\s*\d+,\s*"[^"]*",[\s\S]*?new\[\] \{([^}]*)\}/g)].map((m) => ({
    constant: m[1],
    screen: m[0].match(/"(\w+)", new\[\] \{/)?.[1] ?? '',
    targets: [...m[2].matchAll(/"([^"]+)"/g)].map((t) => t[1]),
  }));

  it('finds the ten steps in the registry', () => {
    expect(steps).toHaveLength(10);
    for (const step of steps) expect(step.targets.length).toBeGreaterThan(0);
  });

  it('has the first target of every step on a real control', () => {
    for (const step of steps) {
      const target = step.targets[0];
      // data-guide="x" on an element, or guide="x" on ConfirmButton.
      const present = new RegExp(`(data-guide|guide)=(\{[^}]*)?["']${target}["']`).test(svelte);
      expect(present, `${step.constant}: no control wears data-guide="${target}"`).toBe(true);
    }
  });

  it('points every step at a screen the app can open', () => {
    // 'inheritance' and 'ancestors' are tab-only keys App.svelte opens through
    // requestScreen (TAB_ONLY_KEYS), so they are not in SCREEN_KEYS.
    const known = new Set<string>([...SCREEN_KEYS, 'inheritance', 'ancestors']);
    for (const step of steps) {
      expect(known.has(step.screen), `${step.constant} -> ${step.screen}`).toBe(true);
    }
  });
});
