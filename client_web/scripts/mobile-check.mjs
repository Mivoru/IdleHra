// Modul: measures whether anything is wider than the phone it is drawn on.
//
// Reported from a phone as "the right side is cut off" - which is what a
// horizontal overflow looks like once `overflow-x: hidden` stops it scrolling.
// A screenshot cannot be asserted on; a width can.
//
// 360px is the common Android logical width; 320 is the narrowest phone still
// worth supporting.
import { chromium } from 'playwright';

const BASE = process.env.FOLKIDLE_E2E_BASE ?? 'http://localhost:5173/';
const WIDTHS = [320, 360, 414];
const SCREENS = [
  'Character', 'Chest', 'Combat', 'Gathering', 'Forge', 'Market',
  'Village', 'Progress', 'Skill Tree', 'Auto-Eat', 'Crafting', 'Store',
  'Inheritance', 'Codex', 'Breeding', 'Settings', 'Social', 'Guild',
  'World Boss', 'Boosts', 'Map', 'Mail',
];

let failures = 0;
const browser = await chromium.launch();

for (const width of WIDTHS) {
  const page = await browser.newPage({ viewport: { width, height: 800 }, deviceScaleFactor: 2 });
  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.locator('input[type="email"]').fill('dev@folkidle.local');
  await page.locator('input[type="password"]').fill('FolkIdleDev123!');
  await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
  await page.waitForSelector('text=Combat', { timeout: 20000 });
  await page.waitForTimeout(2500);
  for (let i = await page.locator('.toast button').count(); i > 0; i--) {
    await page.locator('.toast button').first().click().catch(() => {});
  }

  for (const screen of SCREENS) {
    const nav = page.locator('header').getByRole('button', { name: screen, exact: true }).first();
    if ((await nav.count()) === 0) continue;
    await nav.click().catch(() => {});
    await page.waitForTimeout(350);

    // The widest element on the page, and by how much it exceeds the viewport.
    const worst = await page.evaluate((vw) => {
      let worstOverflow = 0;
      let worstDescription = '';
      for (const element of document.querySelectorAll('body *')) {
        const box = element.getBoundingClientRect();
        if (box.width === 0) continue;
        const overflow = Math.round(box.right - vw);
        if (overflow > worstOverflow) {
          worstOverflow = overflow;
          worstDescription = `${element.tagName.toLowerCase()}.${(element.className || '').toString().split(' ')[0]}`;
        }
      }
      return { overflow: worstOverflow, what: worstDescription, scrollWidth: document.documentElement.scrollWidth };
    }, width);

    // A couple of pixels is rounding; anything more is a layout that does not
    // fit the screen it is on.
    if (worst.overflow > 2) {
      console.log(`FAIL ${width}px ${screen}: ${worst.what} overflows by ${worst.overflow}px`);
      failures++;
    }
  }
  await page.close();
}

await browser.close();
console.log(failures === 0 ? 'ok   nothing overflows at 320, 360 or 414px' : `${failures} overflowing screens`);
process.exit(failures === 0 ? 0 : 1);
