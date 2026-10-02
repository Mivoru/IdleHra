// Modul: measures whether anything is wider than the phone it is drawn on.
//
// Reported from a phone as "the right side is cut off" - which is what a
// horizontal overflow looks like once `overflow-x: hidden` stops it scrolling.
// A screenshot cannot be asserted on; a width can.
//
// 360px is the common Android logical width; 320 is the narrowest phone still
// worth supporting.
import { chromium } from 'playwright';
import { navButton } from './screens.mjs';

const BASE = process.env.FOLKIDLE_E2E_BASE ?? 'http://localhost:5173/';
const WIDTHS = [320, 360, 414];
const SCREENS = [
  'Character', 'Chest', 'Combat', 'Gathering', 'Forge', 'Market',
  'Village', 'Progress', 'Skill Tree', 'Auto-Eat', 'Crafting',
  'Codex', 'Bloodline', 'Settings', 'Friends', 'Guild',
  'World Boss', 'Home', 'Mail',
];

let failures = 0;
const browser = await chromium.launch();

for (const width of WIDTHS) {
  const page = await browser.newPage({ viewport: { width, height: 800 }, deviceScaleFactor: 2 });
  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  // The once-per-browser Android popup covers the login buttons on a fresh page.
  await page.getByRole('button', { name: 'Play as guest' }).waitFor();
  const notNow = page.getByRole('button', { name: 'Not now', exact: true });
  if ((await notNow.count()) > 0) await notNow.click();
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.locator('input[type="email"]').fill('dev@folkidle.local');
  await page.locator('input[type="password"]').fill('FolkIdleDev123!');
  await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
  // Modul: NOT 'text=Combat' - that is a nav entry, and on these widths the
  // nav is folded into the tab bar and the More sheet, so it is not visible.
  // Signed-in is signed-in regardless of what the nav is doing.
  await page.waitForSelector('header button.navtoggle', { timeout: 20000 });
  await page.waitForTimeout(2500);

  // Modul: the offline summary is a modal whose backdrop swallows every click,
  // and it ARRIVES LATE - built from the first state packet. Sampled once
  // instead of polled, it is missed, and the miss surfaces thirty seconds later
  // as an unrelated button "not receiving pointer events". exercise.mjs learned
  // this the same way.
  const deadline = Date.now() + 6000;
  while (Date.now() < deadline) {
    const cont = page.getByRole('button', { name: 'Continue', exact: true });
    if ((await cont.count()) > 0) {
      await cont.first().click();
      break;
    }
    await page.waitForTimeout(250);
  }

  for (let i = await page.locator('.toast button').count(); i > 0; i--) {
    await page.locator('.toast button').first().click().catch(() => {});
  }

  for (const screen of SCREENS) {
    // Modul: navButton reaches the destination the way a thumb does - the tab
    // bar, or the More sheet behind the header's ≡ (task 95). Opening ≡ here
    // as well would toggle the sheet shut again before navButton looked.
    const nav = await navButton(page, screen);
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
    // Modul: THE MAP'S PLATE LABELS, specifically.
    //
    // They sit inside a fixed circular disc, so a label too big for it does
    // not overflow the PAGE - it wraps inside the wood and reads as "COMBA /
    // T". Nothing above would ever catch that, because nothing crosses the
    // viewport. Reported from a phone as the last letter jumping to its own
    // line.
    if (screen === 'Home') {
      const wrapped = await page.evaluate(() =>
        [...document.querySelectorAll('.place span')]
          .filter((el) => {
            // Labels that are authored as two lines carry a newline; anything
            // taller than the lines it was written with is wrapping by
            // accident.
            const authoredLines = (el.textContent ?? '').trim().split(String.fromCharCode(10)).length;
            const lineHeight = parseFloat(getComputedStyle(el).lineHeight) || 1;
            return el.getBoundingClientRect().height > lineHeight * authoredLines + 2;
          })
          .map((el) => (el.textContent ?? '').trim()),
      );
      if (wrapped.length > 0) {
        console.log(`FAIL ${width}px Home: plate labels wrap - ${wrapped.join(', ')}`);
        failures++;
      }
    }

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
