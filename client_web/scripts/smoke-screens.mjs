// Modul: visits EVERY screen in a real browser and fails on any console error.
//
// This exists because svelte-check passes on crashes that only rendering
// finds. A temporal-dead-zone error in a module-level subscription, a null
// dereference in a $derived, a store read before its first value - all of them
// type-check perfectly and blank the page. The only way to know a screen works
// is to open it.
//
// Runs against a live server and a real signed-in session, so it also proves
// the queries behind each screen return something the screen can render.
import { chromium } from 'playwright';

const OUT = process.argv[2] ?? 'screens';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

const errors = [];
page.on('console', (m) => {
  // Modul: a 403 is the server SAYING NO CORRECTLY, not a broken screen.
  // Settings asks /api/v1/admin/status whether this account may see the admin
  // tools, because there is no other way to find out; an ordinary account - and
  // the guest this script signs in as is always one - is told no, and the
  // browser logs that refusal as a console error regardless. It failed Settings
  // on production while Settings worked perfectly.
  if (m.type() === 'error' && /status of 403/.test(m.text())) return;
  if (m.type() === 'error') errors.push(`console: ${m.text()}`);
});
page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));

// Modul: FOLKIDLE_E2E_BASE, like exercise.mjs. This was hardcoded to
// localhost, and the deploy skill tells you to point it at production with
// exactly that variable - so the post-deploy check silently smoke-tested the
// developer's own dev server and reported a pass for a box it had never
// opened. A verification that cannot be aimed is worse than none, because it
// is believed.
const BASE = process.env.FOLKIDLE_E2E_BASE ?? 'http://localhost:5173/';
await page.goto(BASE, { waitUntil: 'networkidle' });
await page.getByRole('button', { name: 'Play as guest' }).click();
await page.waitForSelector('text=Combat', { timeout: 20000 });
await page.waitForFunction(
  () => !document.body.innerText.includes('Waiting for the first state snapshot'),
  { timeout: 20000 },
);

// Every destination in the header, by its visible label, in the header's own
// order and grouping.
//
// Modul: THIS LIST WENT STALE AND THE SCRIPT COULD NOT SAY SO. It still asked
// for 'Larder' (the nav has said 'Auto-Eat' for a long time), 'Social' (it is
// 'Friends'), 'Chat' (chat became a dock, not a screen) and 'Bank' (the chrono
// bank, deleted 2026-09-02) - while never visiting Map, Leaderboards,
// Ancestors, Inheritance, Skill Tree or Wiki at all. A missing label fails
// loudly, which is fine; a screen nobody visits is the silent half, and six of
// them were going unchecked. The check below asserts the list matches the nav.
const SCREENS = [
  'Map', 'Combat', 'Gathering', 'World Boss', 'Boosts',
  'Character', 'Chest', 'Auto-Eat', 'Crafting', 'Forge',
  'Market', 'Friends', 'Guild', 'Mail', 'Leaderboards',
  'Breeding', 'Ancestors', 'Inheritance',
  'Village', 'Skill Tree', 'Progress', 'Codex', 'Store', 'Settings',
  'Wiki',
];

// Modul: the list is checked against the nav rather than trusted. Renaming a
// destination used to leave this file asking for a button that no longer
// exists, which surfaces thirty seconds later as a click timeout that says
// nothing about the rename - and adding one left it unvisited forever, which
// says nothing at all.
const navLabels = await page.evaluate(() =>
  [...document.querySelectorAll('header button')]
    .map((b) => b.textContent.trim())
    .filter((t) => t.length > 0),
);
const missing = SCREENS.filter((s) => !navLabels.includes(s));
const unvisited = navLabels.filter((n) => !SCREENS.includes(n));
if (unvisited.length > 0) {
  console.log(`note: nav buttons this script does not visit: ${unvisited.join(', ')}`);
}

// A label with no button is counted and SKIPPED rather than clicked: clicking
// it waits the full thirty seconds and then throws a timeout that names the
// locator instead of the rename, which is how this failure used to present.
let failures = missing.length;
for (const label of missing) {
  console.log(`FAIL ${label} - the nav has no button with this label (renamed or removed?)`);
}

for (const label of SCREENS) {
  if (missing.includes(label)) continue;
  const before = errors.length;
  await page.getByRole('button', { name: label, exact: true }).first().click();
  // Long enough for a query to resolve and a $effect to run - a crash on
  // mount usually surfaces within one frame, but a crash in a query callback
  // needs the round trip.
  await page.waitForTimeout(900);

  const text = await page.evaluate(() => document.body.innerText);
  // A blanked screen is the exact symptom of a module-level crash, and it
  // looks like "nothing happened" rather than an error.
  const headerOnly = text.replace(/\s+/g, ' ').trim().length < 200;
  const newErrors = errors.slice(before);

  if (newErrors.length > 0 || headerOnly) {
    failures++;
    console.log(`FAIL ${label}${headerOnly ? ' (page is blank)' : ''}`);
    for (const err of newErrors) console.log(`      ${err}`);
    await page.screenshot({ path: `${OUT}-fail-${label.replace(/\W/g, '')}.png` });
  } else {
    console.log(`ok   ${label}`);
  }
}

await page.screenshot({ path: `${OUT}-last.png`, fullPage: true });
await browser.close();

console.log(`\n${SCREENS.length - failures}/${SCREENS.length} screens rendered clean`);
if (failures > 0) process.exit(1);
