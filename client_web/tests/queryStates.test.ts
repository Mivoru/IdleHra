// Modul: A QUERY THAT CAN FAIL HAS TO SAY SO, OR IT SAYS SOMETHING FALSE.
//
// A UI audit (2026-10, F4) counted 13 of the 21 route files that create a
// TanStack query and never read `isError`. With `retry: 1` a dead endpoint
// surfaces in about a second, and those screens then fell through to their
// empty branch or their skeleton: the chest said "Nothing here." (to players
// who had lived through losing 17,836 rows), the guild list said "No guilds
// exist yet. Create the first." and re-enabled Create for guild members, and
// the statistics panel shimmered for ever. None of it is a type error and
// `svelte-check` is silent; the page renders perfectly.
//
// So the check is a grep, like runesMode.test.ts: a component that calls
// createQuery must either read `.isError` itself or hand the query to
// <QueryState> (lib/ui/QueryState.svelte), which renders the error branch for
// it. It is a per-FILE floor, not a per-query proof - a file with one handled
// query and one unhandled one passes - but it is the floor that was missing.
//
// The allow-list is for queries whose failure honestly renders as ABSENCE:
// an optional enrichment (an estimate, a badge, a comparison) where nothing on
// screen claims "you have none". Each entry says why. Adding a file here
// should need the same sentence.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

function svelteFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...svelteFiles(full));
    else if (entry.name.endsWith('.svelte')) out.push(full);
  }
  return out;
}

const ALLOWED: Record<string, string> = {
  // Every query here decorates the fight list - hunting estimates (deliberately
  // `retry: false`, "a 409 shows nothing"), challenge badges, the ascension
  // ladder, boss-gear progress. Absent means "no badge", not "none exist".
  'routes/Combat.svelte': 'optional enrichments only',
  // Monuments drawn on the map; a failed fetch draws the bare map.
  'routes/Hub.svelte': 'decoration only',
  // The admin probe is EXPECTED to fail (403) for every non-admin, and the
  // season query only runs for admins once that probe succeeded.
  'routes/Settings.svelte': 'admin probe fails by design for players',
  // Home cards fall back to their next-best line when a source is missing.
  'lib/ui/HomeCards.svelte': 'cards degrade to other lines',
  // A mail count badge: no badge is the right rendering of "unknown".
  'lib/ui/MailBadge.svelte': 'badge only',
  // Overlays that enrich a moment (a loot card, a coach hint) and must not
  // interrupt it with an error. Not edited on the F4 branch.
  'lib/ui/LootReveal.svelte': 'overlay enrichment',
  'lib/ui/OnboardingCoach.svelte': 'overlay hint',
  'lib/ui/ChatDock.svelte': 'overlay, unread count only',
  // Task 95: the online count on the chat entry is printed only once it has
  // loaded; a failed fetch shows the plain "Chat" button.
  'lib/ui/ChatButton.svelte': 'online count only, absent until loaded',
  // The worn list only adds the "better than worn" comparison to loot rows.
  'lib/ui/SessionLoot.svelte': 'comparison enrichment',
  // Task 101: the only query is the roster of NAMES for the worker picker;
  // without it a person is called by their race, which is still true.
  'routes/Gathering.svelte': 'worker names only, falls back to the race',
};

describe('every query has an error state', () => {
  const files = svelteFiles(srcRoot);
  const withQueries = files.filter((file) => /\bcreateQuery\s*\(/.test(readFileSync(file, 'utf8')));

  it('finds query-using components at all - an empty sweep proves nothing', () => {
    expect(withQueries.length).toBeGreaterThan(20);
  });

  it('reads .isError or renders <QueryState> wherever createQuery is called', () => {
    const offenders = withQueries
      .map((file) => relative(srcRoot, file).replace(/\\/g, '/'))
      .filter((rel) => !(rel in ALLOWED))
      .filter((rel) => {
        const source = readFileSync(join(srcRoot, rel), 'utf8');
        return !/\.isError\b/.test(source) && !/<QueryState[\s>]/.test(source);
      });

    expect(
      offenders,
      `a failed request reads as "empty" here - add an error branch (QueryState or .isError): ${offenders.join(', ')}`,
    ).toEqual([]);
  });

  it('keeps the allow-list honest - every entry still exists and still needs it', () => {
    const stale = Object.keys(ALLOWED).filter((rel) => {
      const file = join(srcRoot, rel);
      let source: string;
      try {
        source = readFileSync(file, 'utf8');
      } catch {
        return true;
      }
      return /\.isError\b/.test(source) || /<QueryState[\s>]/.test(source) || !/\bcreateQuery\s*\(/.test(source);
    });
    expect(stale, `allow-list entries no longer needed - remove them: ${stale.join(', ')}`).toEqual([]);
  });
});
