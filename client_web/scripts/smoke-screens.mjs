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
  if (m.type() === 'error') errors.push(`console: ${m.text()}`);
});
page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));

await page.goto('http://localhost:5173/', { waitUntil: 'networkidle' });
await page.getByRole('button', { name: 'Play as guest' }).click();
await page.waitForSelector('text=Combat', { timeout: 20000 });
await page.waitForFunction(
  () => !document.body.innerText.includes('Waiting for the first state snapshot'),
  { timeout: 20000 },
);

// Every destination in the header, by its visible label.
const SCREENS = [
  'Combat', 'Gathering', 'World Boss', 'Boosts',
  'Character', 'Inventory', 'Larder', 'Crafting', 'Forge', 'Bank', 'Mail',
  'Market', 'Chat', 'Social', 'Guild',
  'Village', 'Progress', 'Codex', 'Breeding', 'Store', 'Settings',
];

let failures = 0;

for (const label of SCREENS) {
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
