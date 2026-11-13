import { open, signIn, go } from './screens.mjs';
const out = process.env.SHOTS;
const { browser, page } = await open({ width: 390, height: 844 });
await signIn(page);
for (const s of (process.env.WHICH ?? 'Dungeon').split(',')) {
  try {
    await go(page, s);
    await page.waitForTimeout(2500);
    await page.screenshot({ path: `${out}/${s.replace(/[^A-Za-z]+/g, '_')}.png`, fullPage: true });
    console.log('shot', s);
  } catch (e) { console.log('fail', s, e.message.split('\n')[0]); }
}
await browser.close();
