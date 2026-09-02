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
//
// Signs in as a GUEST, deliberately: this is the post-deploy check the deploy
// skill points at production, and a guest owns nothing that a navigation pass
// could spend. Use exercise.mjs when the question is whether a feature works.
import { SCREENS, assertMatchesNav, go, open, signInAsGuest } from './screens.mjs';

const OUT = process.argv[2] ?? 'screens';
const { browser, page, errors } = await open({ width: 1440, height: 900 });

await signInAsGuest(page);

// Modul: the list is checked against the nav rather than trusted. Renaming a
// destination used to leave this file asking for a button that no longer
// exists, which surfaces thirty seconds later as a click timeout naming a
// locator and saying nothing about the rename - and adding one left it
// unvisited forever, which says nothing at all. Six screens were unvisited when
// this check was written.
const { missing, unvisited } = await assertMatchesNav(page);
if (unvisited.length > 0) {
  console.log(`note: nav buttons this script does not visit: ${unvisited.join(', ')}`);
}

// A label with no button is counted and SKIPPED rather than clicked: clicking
// it waits the full thirty seconds and then throws.
let failures = missing.length;
for (const label of missing) {
  console.log(`FAIL ${label} - the nav has no button with this label (renamed or removed?)`);
}

for (const label of SCREENS) {
  if (missing.includes(label)) continue;
  const before = errors.length;
  await go(page, label);
  // Long enough for a query to resolve and a $effect to run - a crash on
  // mount usually surfaces within one frame, but a crash in a query callback
  // needs the round trip.
  await page.waitForTimeout(600);

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
