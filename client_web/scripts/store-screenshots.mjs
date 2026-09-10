// Modul: STORE SCREENSHOTS, at the sizes the two stores actually demand.
//
// TASK_BOARD C3 wants "screenshots at required sizes", and a store listing is
// the one part of shipping where a wrong number is a rejected submission
// rather than a bug. Both stores are picky in different ways:
//
//   Play  - at least two phone screenshots, 16:9 or 9:16, each side between
//           320px and 3840px, and the long side at most twice the short one.
//           A 7-inch and a 10-inch tablet set are required for tablet listing.
//   Apple - 6.7" (1290x2796) and 6.5" (1242x2688) are the two iPhone sets a
//           new submission needs; 12.9" iPad (2048x2732) if the app is offered
//           on iPad.
//
// So the numbers below are the contract, not a preference, and they are here
// rather than in somebody's notes because a resubmission a week later is the
// usual cost of getting one wrong.
//
// SAME SIGN-IN AS THE GEOMETRY CHECKERS, which means the dev fixture and
// therefore a DEV BOX ONLY. It is also the right account to shoot: a fresh
// account has an empty inventory and nothing to show, and a screenshot of an
// empty game sells nothing.
//
// Run: npm run screenshots:store
import { mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { signIn, go } from './screens.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(here, '../resources/store-screenshots');

/**
 * The frames, and what each one is FOR.
 *
 * A store listing is read in about four seconds, so the order is the argument:
 * what the game is, then that it plays itself, then that there is depth behind
 * it. Screens nobody would understand out of context are deliberately absent.
 */
const SHOTS = [
  { screen: 'Combat', label: '1-combat', caption: 'It fights while you are away' },
  { screen: 'Character', label: '2-character', caption: 'Eleven slots, and gear that matters' },
  { screen: 'Gathering', label: '3-gathering', caption: 'Every trade advances on its own' },
  { screen: 'Village', label: '4-village', caption: 'A village that grows with you' },
  { screen: 'Ancestors', label: '5-ancestors', caption: 'A bloodline that outlives a season' },
  { screen: 'Codex', label: '6-codex', caption: 'Learn a monster by killing enough of them' },
];

/** device name -> viewport. The store cares about the FILE's pixel size. */
const DEVICES = [
  // Play: 9:16 phone. 1080x1920 is the safest possible answer to its rules.
  { id: 'android-phone', width: 1080, height: 1920, scale: 3 },
  // Apple 6.7" - the set a new submission is rejected without.
  { id: 'ios-6.7', width: 1290, height: 2796, scale: 3 },
  // Apple 6.5".
  { id: 'ios-6.5', width: 1242, height: 2688, scale: 3 },
];

const browser = await chromium.launch();
let written = 0;

for (const device of DEVICES) {
  // Modul: the PAGE is laid out at CSS pixels and the FILE comes out at
  // width*scale. Rendering a 1290px-wide page directly would be a tablet
  // layout in a phone-shaped file - every screenshot would show the desktop
  // nav, which is not what anybody installing this will see.
  const cssWidth = Math.round(device.width / device.scale);
  const cssHeight = Math.round(device.height / device.scale);

  const context = await browser.newContext({
    viewport: { width: cssWidth, height: cssHeight },
    deviceScaleFactor: device.scale,
    isMobile: true,
    hasTouch: true,

    // Modul: BOTH OF THESE ARE PINNED BECAUSE THE FIRST RUN GOT THEM WRONG.
    //
    // colorScheme: the client follows the system, and Playwright's default is
    // light - so every shot came out in the parchment palette while the app
    // icon, the splash and everything anybody associates with this game is the
    // dark one. A listing whose screenshots do not match its icon looks like
    // somebody else's app.
    //
    // locale: `initLanguage` reads navigator.language, and on a Czech dev box
    // that produced an English UI with three Czech strings in the header -
    // because only a handful of strings are translated at all. Faithful to
    // what that machine shows and useless as a store asset.
    colorScheme: 'dark',
    locale: 'en-GB',
  });
  const page = await context.newPage();

  // Modul: the shared helper, not a second copy of the sign-in. It also
  // dismisses the offline summary - a modal with a click-swallowing backdrop
  // that ARRIVES LATE, and which would otherwise be in the middle of every
  // screenshot. Three checkers learned that the expensive way.
  await signIn(page);

  const dir = resolve(OUT, device.id);
  mkdirSync(dir, { recursive: true });

  for (const shot of SHOTS) {
    // `go` opens the hamburger first when the nav is collapsed, which it is at
    // every viewport in this file.
    await go(page, shot.screen);
    await page.waitForTimeout(1200);

    const file = resolve(dir, `${shot.label}.png`);
    await page.screenshot({ path: file, fullPage: false });
    written += 1;
    console.log(`  ${device.id}/${shot.label}.png  ${device.width}x${device.height}  "${shot.caption}"`);
  }

  await context.close();
}

await browser.close();

console.log(`\n${written} screenshots in resources/store-screenshots/.`);
console.log('Play wants at least 2 phone shots; Apple wants the full 6.7" and 6.5" sets.\n');
