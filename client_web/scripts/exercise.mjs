// Modul: drives every INTERACTIVE feature and asserts what happened.
//
// smoke-screens.mjs proves a screen renders. That is a much weaker claim than
// it sounds: a screen full of buttons that all silently do nothing renders
// perfectly. This script clicks them and checks the world changed - the
// inventory shrank, the affix value moved, the message appeared.
//
// Signs in as the DEV FIXTURE rather than a guest, because a guest owns
// nothing and every "does forge fusion work" question answers itself with
// "there is nothing to fuse".
import { chromium } from 'playwright';
import { navButton } from './screens.mjs';

const results = [];
function record(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` - ${detail}` : ''}`);
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });

const consoleErrors = [];
page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
page.on('pageerror', (e) => consoleErrors.push(`pageerror: ${e.message}`));

// Modul: the console message for a failed fetch does NOT carry the URL - it is
// the same "Failed to load resource" string whatever was asked for. So misses
// are counted off the response stream, where the URL is, which is the only way
// to tell an optional audio clip apart from a real problem.
const missedUrls = [];
page.on('response', (r) => { if (r.status() === 404) missedUrls.push(r.url()); });

// Waits for the screen to have actually LOADED, not for a fixed delay. Most
// screens open on a query, and a cold server answers the first one slowly
// enough that a fixed wait passes locally and fails on a fresh boot - which is
// a flaky test pretending to be a bug report.
// Task 59: these screens are tabs under one menu entry now - Supplies holds
// Auto-Eat and Boosts, Bloodline holds Breeding, Ancestors and Inheritance.
// A step still names the screen it means; go() takes the menu entry and then
// the tab, the way a player would.
const SUB_TABS = {
  'Auto-Eat': ['Supplies', 'larder'],
  Boosts: ['Supplies', 'boosts'],
  Breeding: ['Bloodline', 'breeding'],
  Ancestors: ['Bloodline', 'ancestors'],
  Inheritance: ['Bloodline', 'inheritance'],
  // Task 76: one Community entry, four tabs.
  Friends: ['Community', 'social'],
  Market: ['Community', 'market'],
  Guild: ['Community', 'guildops'],
  Leaderboards: ['Community', 'leaderboards'],
};

const go = async (label) => {
  const [menuLabel, subTab] = SUB_TABS[label] ?? [label, null];
  // Modul: scoped to the NAV. The hub map's plates are buttons named "Combat",
  // "Market", "Guild" and so on too, and an unscoped lookup resolved to
  // whichever came first in the DOM - which is the map, and only while the map
  // is the screen being shown. Navigation has to mean the nav.
  await (await navButton(page, menuLabel)).click();
  if (subTab) await page.locator(`[data-subtab="${subTab}"]`).first().click();
  await page.waitForFunction(
    () => !/\bLoading\.\.\./.test(document.body.innerText),
    { timeout: 15000 },
  ).catch(() => {});
  await page.waitForTimeout(600);
};

// Modul: TASK 97 - Character is a person switcher plus three tabs (Gear,
// Attributes, Work & orders), and only the open tab is in the DOM. A step that
// reads or presses something on that screen opens its tab first rather than
// trusting whichever tab the screen chose as its default.
const characterTab = async (name) => {
  await page.locator(`[data-character-tab="${name}"]`).first().click({ timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(300);
};
// Picks a person on the switcher by character id. The chips exist only when
// more than one person can be chosen; with one, that person is already shown.
const characterPerson = async (characterId) => {
  const chip = page.locator(`[data-testid="person-switcher"] [data-character-id="${characterId}"].person`);
  if ((await chip.count()) > 0) {
    await chip.first().click().catch(() => {});
    await page.waitForTimeout(300);
  }
};
// What the switcher says about EVERY person - name, race, age and job - by
// stepping through the chips. The screen shows one person at a time now.
const everyPersonText = async () => {
  const chips = page.locator('[data-testid="person-switcher"] .person');
  const count = await chips.count();
  if (count === 0) return (await page.locator('[data-testid="person-switcher"]').innerText().catch(() => '')) ?? '';
  const parts = [];
  for (let i = 0; i < count; i++) {
    await chips.nth(i).click().catch(() => {});
    await page.waitForTimeout(250);
    parts.push(await page.locator('[data-testid="person-current"]').innerText().catch(() => ''));
  }
  await chips.first().click().catch(() => {});
  return parts.join(' | ');
};

// A toast is how this client reports both server results and its own refusals,
// so reading them is how a click's outcome becomes observable at all.
const toasts = async () => page.locator('.toast').allInnerTexts();
// Modul: DISMISS, NEVER RELOAD. This clicked the FIRST button of every `.toast`,
// and the "FolkIdle has been updated" prompt (WhatsNew.svelte) is also a
// `.toast` whose first button is Reload. Any client edit during a dev session
// makes Vite serve a new build, the prompt appears, and this reloaded the page
// back to the map mid-step - the Friends step then waited thirty seconds for an
// "Add" button on a screen it was no longer on. Three runs red for that alone.
// Only the command toasts' × (aria-label Dismiss) and the prompt's "Later".
const dismissToasts = async () => {
  const buttons = page.locator('.toast button[aria-label="Dismiss"], .toast button.ghost');
  for (let i = await buttons.count(); i > 0; i--) {
    await buttons.first().click().catch(() => {});
  }
};

// Modul: the dev server by default, but the deployment when asked. The
// point of this script is to assert what CHANGED, and once the game is live
// the thing worth asserting against is the box actually serving players -
// a balance pass that makes a monster lethal can break the combat step in
// production while every local test still passes.
const BASE = process.env.FOLKIDLE_E2E_BASE ?? 'http://localhost:5173/';

// Modul: the API is a DIFFERENT ORIGIN from the page in development - Vite
// serves the client on 5173 and the server answers on 8080 - so a relative
// fetch from inside the page hits Vite and comes back as index.html, which
// surfaces as "Unexpected token '<'" rather than as a 404. In production both
// halves sit behind one Caddy origin and this collapses to the same host,
// which is why the client itself never needs it (see lib/net/config.ts, the
// one place the client's address is written down - this is the harness, not
// the client).
const API_BASE = process.env.FOLKIDLE_E2E_API ?? 'http://localhost:8080';

/** The app's own bearer token, so checks can read the API as the signed-in player. */
const authToken = () =>
  page.evaluate(() => sessionStorage.getItem('folkidle.token') ?? localStorage.getItem('folkidle.token'));

async function apiGet(path) {
  const res = await fetch(`${API_BASE}${path}`, {
    headers: { Authorization: `Bearer ${await authToken()}` },
  });
  return res.ok ? res.json() : null;
}

async function apiPost(path, body) {
  const res = await fetch(`${API_BASE}${path}`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${await authToken()}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  });
  return res.ok ? res.json() : null;
}

/** The status alone, for the checks whose whole point is that a call is REFUSED. */
async function apiPostStatus(path, body) {
  const res = await fetch(`${API_BASE}${path}`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${await authToken()}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  });
  return res.status;
}
await page.goto(BASE, { waitUntil: 'networkidle' });

// --- the Android app offer ----------------------------------------------------
// Once per browser: this is a fresh one, so the popup must be up, must close,
// must stay closed after a reload, and the permanent link must remain.
{
  const promo = page.getByRole('dialog', { name: 'FolkIdle for Android' });
  record('app popup shows on a first visit', (await promo.count()) === 1);
  if ((await promo.count()) > 0) {
    await page.getByRole('button', { name: 'Not now', exact: true }).click();
    record('app popup closes on Not now', (await promo.count()) === 0);
  }
  await page.reload({ waitUntil: 'networkidle' });
  record('app popup does not come back', (await promo.count()) === 0);
  const link = page.getByRole('link', { name: 'Get the Android app' });
  record(
    'login screen keeps the app link',
    (await link.count()) === 1 && (await link.getAttribute('href')) === '/download/folkidle.apk',
  );
}

// --- sign in as the stocked fixture -----------------------------------------
await page.getByRole('button', { name: 'Sign in' }).click();
await page.locator('input[type="email"]').fill('dev@folkidle.local');
await page.locator('input[type="password"]').fill('FolkIdleDev123!');
await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
// Modul: not 'text=Combat' - since task 82 the desktop header folds Combat
// into the Play dropdown, so the label exists but is never visible. The
// header plus the first state packet (below) is what signed in means.
await page.waitForSelector('header', { timeout: 20000 });
await page.waitForFunction(
  () => !document.body.innerText.includes('Waiting for the first state snapshot'),
  { timeout: 20000 },
);
record('sign in with the dev fixture', true);

// The offline summary is a modal with a backdrop that swallows every click, so
// it has to go before anything else can be driven. A real player dismisses it
// the same way; this is not a workaround, it is the first interaction.
// Modul: the modal ARRIVES LATE. It is built from the first state packet, so
// a single count() the instant after sign-in can run before it exists - and
// then its full-screen backdrop swallows every click that follows, which
// surfaces thirty seconds later as an unrelated button "not receiving pointer
// events". Polled for a few seconds instead of sampled once.
async function dismissOfflineSummary(waitMs = 6000) {
  const deadline = Date.now() + waitMs;
  let dismissed = false;
  while (Date.now() < deadline) {
    const cont = page.getByRole('button', { name: 'Continue', exact: true });
    if ((await cont.count()) > 0) {
      await cont.first().click();
      dismissed = true;
      break;
    }
    await page.waitForTimeout(250);
  }
  await page.waitForTimeout(300);
  return dismissed;
}

{
  const shown = await dismissOfflineSummary();
  const stillBlocked = await page.locator('.backdrop').count();
  record('offline summary can be dismissed', stillBlocked === 0, shown ? 'was shown' : 'not shown');
}

// --- combat ------------------------------------------------------------------
//
// Modul: fights the STRONGEST monster the fixture has unlocked.
//
// The bar-animation check below asserts that the monster's health moves between
// snapshots, which needs a target that survives more than one tick. Against
// Field Mouse - 80 HP, and the fixture is level 40 - the character one-shots
// it, so every sample catches a brand new monster at full health and the bar
// reads 100% forever. That has twice looked like an interpolation bug and twice
// been the test picking a target it cannot observe.
//
// Naming a monster does not survive either: region unlocks decide which rows
// are enabled, and the fixture's progress is not fixed. "The last enabled one"
// is the same statement in a form that keeps holding.
await go('Combat');
// The monster list is content-driven and arrives after the screen does, so
// `go`'s "no Loading..." check can return while the list is still empty. Wait
// for the twenty-five rows themselves before indexing into them.
await page
  .getByRole('button', { name: 'Fight', exact: true })
  .nth(0)
  .waitFor({ state: 'visible', timeout: 15000 });
// Modul: EXACT. `name: 'Fight'` is a substring match, so "Stop fighting"
// matched it too - and now that deploying actually persists, the fixture
// arrives already in combat, which put that button in the list and shifted
// every index by one. The click then resolved to a monster row's own button
// and waited thirty seconds for something that was never going to move.
//
// Modul: ENABLED, not nth(5). Region progression gates a region behind the
// previous region's boss, so a fresh fixture can only fight the five monsters
// of region 1 - and index 5 is the first monster of region 2, whose button is
// correctly disabled. The script sat clicking it for thirty seconds and failed
// on a rule working exactly as designed, which is the worst kind of red: it
// says "combat is broken" about a locked door.
//
// Asking for an enabled button says what the step actually needs, and keeps
// saying it when the fixture's unlocked regions change.
//
// Modul: the strongest unlocked REGULAR monster - not the weakest, and not a
// boss.
//
// The weakest is Field Mouse, 80 HP against a level 40 character: dead inside
// one tick, so every sample of its health bar catches a brand new monster at
// full width and the animation check reads "1 distinct width" on a bar that
// works perfectly.
//
// The strongest is a region boss, and a boss can kill the fixture. Shadow Lynx
// is 14,000 HP at roughly 2.5x its region's regular attack power; one run of
// this script won the fight and the next one came back to "Died and respawned"
// and no combat at all. A check that passes or fails on a coin toss is worse
// than no check.
//
// Every region is four regulars and one boss - content canon, not an inference
// from this screen - so the boss is every fifth row.
//
// Modul: the FOURTH regular is now excluded too, for the reason the boss
// always was. A region's regulars scale 8/15/25/40% of its health pool, and
// that last one is deliberately a wall - the monster a player cannot simply
// walk up to without the gear the three before it drop. It kills this fixture
// the same way Shadow Lynx did, and a fight that ends in "Died and respawned"
// inside four seconds reads here as "combat is broken".
//
// So: the strongest regular a geared character reliably SURVIVES, which is the
// third of the four - the last enabled row that is neither the wall nor the
// boss.
const fightButtons = page.getByRole('button', { name: 'Fight', exact: true });
const fightCount = await fightButtons.count();
let strongestUnlocked = -1;
for (let i = 0; i < fightCount; i++) {
  if (await fightButtons.nth(i).isEnabled()) strongestUnlocked = i;
}
if (strongestUnlocked < 0) throw new Error('no unlocked monster to fight - every Fight button is disabled');
// Modul: the FIRST regular of the strongest unlocked region.
//
// "Strongest regular" was a coin toss and had to stop being one. A region's
// four regulars scale 8/15/25/40% of its health pool, and the top of that
// range now kills an unfed character - which the fixture becomes, because
// every run of this script eats the larder it was seeded with. The same code
// scored 49/51 and then 51/51 with nothing changed between them.
//
// The first monster of a region is the one sized for a player ARRIVING there,
// so it cannot kill the fixture whether or not it has food. It is also not the
// global weakest - Field Mouse dies inside a tick and freezes the health bar
// at full width - so the bar stays observable, which is the other thing this
// step exists to check.
const fightTarget = fightButtons.nth(strongestUnlocked - (strongestUnlocked % 5));
await fightTarget.click();
await page.waitForTimeout(4000);
{
  const text = await page.evaluate(() => document.body.innerText);

  record('combat starts', text.includes('Fighting'), text.match(/Fighting [^\n]*/)?.[0]);

  // The bar must MOVE, not merely exist - a filled Image that ignores its own
  // fill was the exact Unity bug this port was built to be free of.
  // Modul: scoped to the MONSTER's bar, not to ".bar-fill" index 1.
  //
  // Index 1 assumed the player bar is always first and always present. The
  // fight block only renders while a monster is alive, so a run where the
  // target died between the click and the read shifted every index and this
  // waited thirty seconds for an element that had never existed - reported as
  // a timeout crash rather than as a failed check.
  const monsterBar = page.locator('.fighting .bar-fill').first();
  const widths = [];
  for (let i = 0; i < 10; i++) {
    widths.push(
      await monsterBar.evaluate((el) => el.style.width).catch(() => 'gone'),
    );
    await page.waitForTimeout(180);
  }
  record('monster health bar animates', new Set(widths).size > 2, `${new Set(widths).size} distinct widths`);

  // Modul: a guard against re-introducing a render loop.
  //
  // A hit-reaction effect keyed on "the damage array is non-empty" re-created
  // its node about sixty times a second, because the render loop rewrites that
  // array whenever it prunes an expired number. It starved the main thread
  // badly enough that every OTHER screen stopped loading - a symptom that
  // looks nothing like an animation bug and cost a while to trace.
  //
  // Counting renders is not possible from here, so this measures the effect:
  // during live combat the page must still be able to do work promptly.
  const started = Date.now();
  await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r(null))));
  const frameMs = Date.now() - started;
  record('the page stays responsive during combat', frameMs < 400, `${frameMs}ms to the next frame`);

  // Modul: THE FIGHT LOG HAS TO FILL, not merely render.
  //
  // A panel that draws perfectly and never receives anything is this project's
  // worst-shipped defect shape, and this one is more exposed to it than most:
  // it is fed by a dedicated server packet (ResponseCombatEventPacket) rather
  // than by the snapshot every other screen reads, so the whole feed can be
  // dead while the screen looks finished.
  //
  // It exists because the snapshot stream CANNOT describe a fast fight -
  // measured 2026-09-04, a geared character killed an early monster every
  // ~1400ms against snapshots every ~1090ms, so CurrentMonsterHp took one
  // single value across 27 of them and there was nothing to animate or infer.
  const logLines = await page.evaluate(() => {
    const list = document.querySelector('.fightlog');
    return list ? [...list.querySelectorAll('li')].map((li) => li.textContent.trim()) : null;
  });
  record('the fight log renders', logLines !== null);
  record(
    'the fight log fills from the server feed',
    (logLines?.length ?? 0) > 0,
    `${logLines?.length ?? 0} lines`,
  );
  // Both directions of the fight, so a feed that only reports one half is
  // still a failure. The player's own swing and the monster's reply are
  // resolved in different branches of the tick and published separately.
  // Modul: AND THE LOOT IS DELIBERATELY NOT IN IT.
  //
  // Drops lived in this log for one build and were moved out: "under the
  // monster there should be only the course of the fight, and on the right a
  // loot drops window split into materials and equipment". The reason is not
  // taste - this is a 50-line ring, and with two characters gathering the
  // material volume evicted every piece of equipment from it within minutes,
  // which is how a player farming for hours concluded nothing was dropping.
  record(
    'the fight log carries no loot lines',
    !(logLines ?? []).some((l) => /^(Dropped|Salvaged into):?/.test(l)),
    (logLines ?? []).find((l) => /^(Dropped|Salvaged)/.test(l)) ?? 'none, as intended',
  );

  // Modul: THE LOOT PANEL IS WHERE DROPS HAVE TO SHOW UP.
  //
  // Its own packet (ResponseLootDropPacket), its own two stores, so it can be
  // dead while everything around it works. Polled rather than sampled once:
  // gear drops on 15% of kills, so a single read a few seconds into a fight
  // proves nothing either way. 60 s, not 25: two passing runs on 2026-09-28
  // held exactly ONE row at 25 s, and the third held none - on a working feed.
  const lootPanel = await (async () => {
    const deadline = Date.now() + 60000;
    let seen = { sections: [], rows: 0 };
    while (Date.now() < deadline) {
      seen = await page.evaluate(() => {
        const panel = document.querySelector('.loot');
        const sections = panel ? [...panel.querySelectorAll('.lootsection')] : [];
        return {
          sections: sections.map((sec) => sec.querySelector('h3')?.textContent.trim() ?? ''),
          rows: sections.reduce((n, sec) => n + sec.querySelectorAll('li').length, 0),
        };
      });
      if (seen.rows > 0) break;
      await page.waitForTimeout(1000);
    }
    return seen;
  })();

  record(
    'the loot panel is split into equipment and materials',
    lootPanel.sections.length === 2,
    lootPanel.sections.join(' / '),
  );
  record(
    'the loot panel fills from the server feed',
    lootPanel.rows > 0,
    `${lootPanel.rows} rows`,
  );

  // The layout half of the same request: the fight goes under the monster, the
  // drops go beside it.
  const lootIsBeside = await page.evaluate(() => {
    const log = document.querySelector('.fightlog')?.getBoundingClientRect();
    const loot = document.querySelector('.loot')?.getBoundingClientRect();
    return log && loot ? { log: Math.round(log.left), loot: Math.round(loot.left) } : null;
  });
  record(
    'the loot panel sits to the right of the fight log',
    !!lootIsBeside && lootIsBeside.loot > lootIsBeside.log,
    JSON.stringify(lootIsBeside),
  );

  // Modul: TASK 49 - WEAR A DROP FROM THE LOOT LIST. The row names the exact
  // instance that dropped (ResponseLootDropPacket.InstanceId); pressing Wear
  // must change what /player/worn reports. Then the fixture gets its own piece
  // back - a check that spends fixture state passes once and fails forever.
  {
    const deadline = Date.now() + 90000;
    let target = null;
    while (Date.now() < deadline) {
      target = await page.evaluate(() => {
        const button = [...document.querySelectorAll('[data-loot-wear]')].find((b) => !b.disabled);
        return button ? Number(button.getAttribute('data-loot-wear')) : null;
      });
      if (target) break;
      await page.waitForTimeout(1500);
    }

    if (!target) {
      record('a drop can be worn from the loot list', false, 'no wearable equipment drop arrived in 90 s');
    } else {
      const line = await page.evaluate(
        (id) => document.querySelector(`[data-loot-wear="${id}"]`)?.closest('li')?.querySelector('.cmp')?.textContent.trim() ?? null,
        target,
      );
      record('a loot row compares the drop with what is worn', !!line, line ?? 'no comparison line');

      const before = (await apiGet('/api/v1/player/worn'))?.Pieces ?? [];
      await dismissToasts();
      await page.locator(`[data-loot-wear="${target}"]`).click();

      let worn = null;
      for (let i = 0; i < 20 && !worn; i++) {
        await page.waitForTimeout(500);
        worn = ((await apiGet('/api/v1/player/worn'))?.Pieces ?? []).find((p) => p.InstanceId === target) ?? null;
      }
      record(
        'wearing a drop from the loot list changes the worn item',
        !!worn,
        worn ? `instance ${target} now in slot ${worn.SlotIndex}` : (await toasts()).join(' | ') || 'the worn list never changed',
      );

      const previous = worn ? before.find((p) => p.SlotIndex === worn.SlotIndex) : null;
      if (previous) {
        await page.evaluate((id) => globalThis.__folkidleEquip?.(id), previous.InstanceId);
        let restored = false;
        for (let i = 0; i < 20 && !restored; i++) {
          await page.waitForTimeout(500);
          restored = ((await apiGet('/api/v1/player/worn'))?.Pieces ?? []).some((p) => p.InstanceId === previous.InstanceId);
        }
        record('the fixture gets its own piece back after the loot-row Wear', restored, `instance ${previous.InstanceId}`);
      }
      // Modul: THE SLOT WAS EMPTY BEFORE. The round-trip above only ran when the
      // main character already wore something there, so on a main that was
      // unarmed (a fixture whose gear sat on another roster slot) the drop
      // stayed worn for good and the state was never restored. Restoring what
      // was touched means taking the drop off again, on the same character
      // (the Wear and this both use no TargetGuid = the main character).
      if (worn && !previous) {
        await page.evaluate((slot) => globalThis.__folkidleUnequip?.(slot), worn.SlotIndex);
        let cleared = false;
        for (let i = 0; i < 20 && !cleared; i++) {
          await page.waitForTimeout(500);
          cleared = !((await apiGet('/api/v1/player/worn'))?.Pieces ?? []).some((p) => p.SlotIndex === worn.SlotIndex);
        }
        record('an empty slot is emptied again after the loot-row Wear', cleared, `slot ${worn.SlotIndex}`);
      }
    }
  }

  // Modul: TASK 50 - a Legendary+ drop is shown over the screen for a moment and
  // then leaves on its own. Forced through the REAL drop handler
  // (__folkidleDemoDrop -> acceptLootDrop): waiting for a live Legendary is one
  // kill in about 1,300.
  {
    await page.evaluate(() => globalThis.__folkidleDemoDrop?.(8));
    const shown = await page
      .waitForSelector('[data-loot-reveal]', { timeout: 3000 })
      .then(() => true)
      .catch(() => false);
    const text = shown ? (await page.locator('[data-loot-reveal]').innerText()).replace(/\s+/g, ' ') : '';
    record('a Legendary+ drop shows the reveal card', shown && /mythic/i.test(text), text || 'no card');
    const gone = await page
      .waitForSelector('[data-loot-reveal]', { state: 'detached', timeout: 4000 })
      .then(() => true)
      .catch(() => false);
    record('the reveal card leaves on its own', gone);
  }

  record(
    'the log reports both sides of the fight',
    (logLines ?? []).some((l) => /^(Critical! )?You (hit|miss)/.test(l))
      && (logLines ?? []).some((l) => /(hits|misses) you/.test(l)),
    (logLines ?? [])[0] ?? '',
  );

  // Modul: the bar's maximum comes from the SERVER now. The client used to
  // compute it as `MaxHp * 5` for an unbeaten boss, which ignores First Blood
  // softening the penalty, and scaled the player's own bar against a session
  // high-water mark of the largest PlayerHp ever seen - caught reading
  // "2320 / 2320" while PlayerHp was 3701.
  const barsHonest = await page.evaluate(() => {
    const bars = [...document.querySelectorAll('.hpblock [role="progressbar"]')];
    return bars.map((b) => ({
      now: Number(b.getAttribute('aria-valuenow')),
      max: Number(b.getAttribute('aria-valuemax')),
    }));
  });
  record(
    'no health bar reports more health than its maximum',
    barsHonest.length > 0 && barsHonest.every((b) => b.max > 0 && b.now <= b.max),
    JSON.stringify(barsHonest),
  );
}

// --- combat: the way back, and what opens the next region (task 72) ----------
// "Not in combat" offers the last monster; pressing it must put the character
// back in a fight. Round trip: if the fixture was fighting, it is fighting again
// afterwards; if it was idle, it is stood down again.
await go('Combat');
{
  const stopButton = page.getByRole('button', { name: 'Stand down', exact: true });
  const wasFighting = (await stopButton.count()) > 0;
  if (wasFighting) {
    await stopButton.first().click();
    await page.getByTestId('combat-continue').waitFor({ timeout: 10000 }).catch(() => {});
  }

  const cont = page.getByTestId('combat-continue');
  const offered = (await cont.count()) > 0;
  let resumed = false;
  if (offered) {
    await cont.first().click();
    resumed = await stopButton
      .first()
      .waitFor({ timeout: 10000 })
      .then(() => true)
      .catch(() => false);
  }
  record(
    '"Not in combat" offers the last monster, and it resumes the fight',
    offered && resumed,
    offered ? (resumed ? 'fighting again' : 'pressed, no fight started') : 'no Continue button',
  );
  if (resumed && !wasFighting) await stopButton.first().click();

  const wall = page.getByTestId('region-wall');
  const wallText = (await wall.count()) > 0 ? (await wall.first().innerText()).replace(/\s+/g, ' ') : '';
  record(
    'the next region says what its boss asks for and what you wear',
    /full set of region-\d gear/.test(wallText) && /You wear \d of 8/.test(wallText),
    wallText.slice(0, 140) || 'no requirement shown (fixture may have every region open)',
  );

  // Task 78: every open monster carries the server's estimate, and the line
  // on screen is the API's numbers, not a client guess.
  const projection = await apiGet('/api/v1/combat/projection');
  const estimateRows = page.getByTestId('hunting-estimate');
  await estimateRows.first().waitFor({ timeout: 10000 }).catch(() => {});
  const shown = await estimateRows.count();
  const firstLine = shown > 0 ? (await estimateRows.first().innerText()).replace(/\s+/g, ' ') : '';
  record(
    'each open monster shows a hunting estimate from the server',
    // Task 98: the card's second line carries the estimate without the
    // "Estimate:" prefix (the full sentence is the element's title).
    projection !== null && (projection.Monsters ?? []).length === 25 && shown > 0 && /XP\/h|cannot hurt/.test(firstLine),
    `${shown} rows; first: ${firstLine.slice(0, 110)}`,
  );
}

// --- forge: fusion and reroll ------------------------------------------------
await go('Forge');
{
  const text = await page.evaluate(() => document.body.innerText);
  // Modul: NO MORE "Forge stock". That panel listed the equipment recipes,
  // and equipment is monster loot now - the Forge fuses and rerolls what you
  // looted, and recipes live on the Crafting screen with the tool tree.
  // Asserted on the panel headings rather than on item names, because which
  // items exist is content that legitimately changes.
  record('forge shows fusion and reroll', /Fusion/.test(text) && /Affix reroll/.test(text));
  record(
    'the forge no longer offers to craft equipment',
    !/Forge stock/.test(text),
    'equipment is a drop',
  );

  // Reroll needs an item and an affix picked. The screen's selects are the
  // only way to know which affix index the command should carry.
  const selects = page.locator('select');
  const count = await selects.count();
  record('forge exposes selects for fusion and reroll', count >= 3, `${count} selects`);

  // Task 100: one row per item, each with its own Fuse. The fixture holds
  // thousands of identical pieces, so an empty list here is the old chip
  // panel's failure (or a fixture that lost its stock - re-seed).
  const rows = await page.locator('[data-testid="fusion-row"]').count();
  record('the forge lists one fusion row per item', rows > 0, `${rows} rows`);
}

// --- forge: a whole stack in one press (task 69) --------------------------------
// The dev route hands the fixture nine Normal Doom Gorgets - a region-5 piece
// it holds none of - so the stack is exactly 9 -> 3 Common -> 1 Uncommon. The
// Uncommon is binned afterwards: a round trip, never a bite out of the
// fixture's own piles (CLAUDE.md: a check that spends fixture state passes
// once and fails forever).
{
  const stack = await apiPost('/api/v1/dev/forge/stack', {});
  const base = stack?.BaseItemId;
  const held = async () =>
    ((await apiGet('/api/v1/forge/inventory'))?.OwnedEquipment ?? []).filter((i) => i.BaseItemId === base);

  if (!base) {
    record('a stack fuses in one press', false, 'the dev route gave no stack');
  } else {
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForTimeout(1200);
    await dismissOfflineSummary(3000);
    await go('Forge');
    await page.waitForTimeout(1200);

    // Task 100: the chips are gone. The row for the item carries a Stack
    // button that points the whole-stack section at its lowest rarity; the
    // search narrows the list so the row is on screen whatever else is owned.
    const search = page.getByPlaceholder('Find an item...');
    if ((await search.count()) > 0) await search.fill('Doom Gorget');
    const chip = page
      .locator('[data-testid="fusion-row"]', { hasText: 'Doom Gorget' })
      .getByTestId('fusion-row-stack')
      .first();
    let planText = '';
    if ((await chip.count()) > 0) {
      await chip.click();
      await page.getByTestId('fuse-stack-to').selectOption({ label: 'Uncommon' }).catch(() => {});
      planText = await page
        .getByTestId('fuse-stack-plan')
        .innerText({ timeout: 10000 })
        .catch(() => '');
    }
    record(
      'the stack preview quotes the plan before anything is spent',
      /^4 fusions/.test(planText.trim()) && /1× Uncommon/.test(planText),
      planText.trim() || 'no plan shown',
    );

    let after = [];
    if (planText) {
      await page.getByTestId('fuse-stack-go').click();
      const deadline = Date.now() + 15000;
      while (Date.now() < deadline) {
        after = await held();
        if (after.length === 1) break;
        await page.waitForTimeout(500);
      }
    }
    record(
      'a stack fuses in one press',
      after.length === 1 && after[0].QualityTier === 3,
      after.map((i) => `T${i.QualityTier}`).join(', ') || 'nothing left',
    );

    // Restore: bin whatever of the dev stack remains.
    for (const piece of await held()) {
      await apiPost('/api/v1/chest/discard', { equipmentId: piece.Id });
    }

    // Modul: FUSING QUICKLY (owner: "the forge breaks for a second"). Three
    // row Fuses pressed as fast as the button allows, on a fresh dev stack of
    // nine. Each fusion turns three pieces into one, so the honest end state
    // is three Common pieces. A tap that re-sent a piece the previous fusion
    // ate used to be answered with TargetNotFound AND a disconnect, so the
    // count came out wrong and the header said "reconnecting". Round-trips:
    // the three Commons are binned afterwards.
    const again = await apiPost('/api/v1/dev/forge/stack', {});
    if (again?.BaseItemId === base) {
      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(1200);
      await dismissOfflineSummary(3000);
      await go('Forge');
      await page.waitForTimeout(1200);
      const find = page.getByPlaceholder('Find an item...');
      if ((await find.count()) > 0) await find.fill('Doom Gorget');
      const fuseBtn = page
        .locator('[data-testid="fusion-row"]', { hasText: 'Doom Gorget' })
        .getByTestId('fusion-row-fuse')
        .first();
      let pressed = 0;
      let sawReconnect = false;
      for (let i = 0; i < 3; i++) {
        const ready = await fuseBtn
          .and(page.locator('button:not([disabled])'))
          .waitFor({ timeout: 8000 })
          .then(() => true)
          .catch(() => false);
        if (!ready) break;
        await fuseBtn.click();
        pressed++;
        sawReconnect ||= /reconnecting/i.test(await page.evaluate(() => document.body.innerText));
      }
      let quick = [];
      const until = Date.now() + 15000;
      while (Date.now() < until) {
        quick = await held();
        if (quick.length === 3) break;
        await page.waitForTimeout(500);
      }
      record(
        'fusing quickly from the row fuses every press and keeps the session',
        pressed === 3 && quick.length === 3 && quick.every((i) => i.QualityTier === 2) && !sawReconnect,
        `${pressed} presses -> ${quick.map((i) => `T${i.QualityTier}`).join(', ') || 'nothing'}${sawReconnect ? ', reconnected' : ''}`,
      );
      for (const piece of await held()) {
        await apiPost('/api/v1/chest/discard', { equipmentId: piece.Id });
      }
    }
  }
}

// --- market ------------------------------------------------------------------
await go('Market');
{
  const before = await page.evaluate(() => document.body.innerText);
  record('market shows a sell list', before.includes('Sell'));

  // Modul: the market used to REQUIRE an exact BaseItemId and an exact rarity
  // and returned nothing without both - a lookup, not a shop. These assert the
  // shop front: it loads on its own, and it can be narrowed.
  record(
    'the market lists on arrival, with no search typed',
    /\d+ listings?|Nothing matches|market is empty|Loading the market/i.test(before),
    'browse is the default',
  );

  const filterCount = await page.locator('.filters select').count();
  record(
    'the market filters by type and rarity',
    filterCount >= 3,
    `${filterCount} filter dropdowns`,
  );

  record(
    'the market pages rather than dumping the book',
    /Page \d+ of \d+/.test(before) || /Nothing matches|market is empty/i.test(before),
  );

  // Narrowing to a slot must actually change the request, not just the UI.
  //
  // Modul: A CHECKBOX, NOT A DROPDOWN. The type filter became checkboxes when
  // the market gained multi-select, and this step kept calling selectOption on
  // `.filters select` - which now resolves to the RARITY dropdown, where no
  // option is named "Helmet". It threw rather than failed, so the whole script
  // died here and every check below the market - crafting, guild, the paper
  // doll, the chest - silently stopped running for as long as that shipped.
  // A crash in a test suite is worse than a red line: a red line is reported.
  const helmet = page.locator('.filters label').filter({ hasText: 'Helmet' }).locator('input[type="checkbox"]').first();
  await helmet.check();
  await page.waitForTimeout(1200);
  const narrowed = await page.evaluate(() => document.body.innerText);
  record(
    'narrowing by slot re-queries the market',
    narrowed !== before,
    'the listing panel changed',
  );

  const listButton = page.getByRole('button', { name: /^List for/ });
  const hasList = (await listButton.count()) > 0;
  record('market has a list-for-price button', hasList);

  if (hasList) {
    const disabled = await listButton.first().isDisabled();
    record(
      'market list button reflects the guild trade licence',
      true,
      disabled ? 'disabled (no guild licence or no item picked)' : 'enabled',
    );
  }
}

// --- social: friends ---------------------------------------------------------
// Modul: this nav item is 'Friends'. It was 'Social' until the menu was
// reorganised on 2026-08-10 and this script was not updated with it, so every
// run since then died here - which is how the one verification that proves
// gameplay works went three weeks without being run. If a go() target ever
// times out, check App.svelte's labels before suspecting the screen.
await go('Friends');
{
  const text = await page.evaluate(() => document.body.innerText);
  record('social screen shows a friend list section', /Friend/i.test(text));

  const input = page.getByPlaceholder('Username').first();
  if ((await input.count()) > 0) {
    await input.fill('definitely_not_a_real_player_9999');
    const addBtn = page.getByRole('button', { name: /^Add/ }).first();
    if ((await addBtn.count()) > 0) {
      await dismissToasts();
      await addBtn.click();
      await page.waitForTimeout(1800);
      const msgs = await toasts();
      // The point is that it SAYS something. Silence here is the failure.
      record('adding an unknown player reports back', msgs.length > 0, msgs.join(' | ') || 'no toast');
      await dismissToasts();
    }
  }
}

// --- chat --------------------------------------------------------------------
// Chat is no longer a nav tab - it is a floating dock that slides out, with a
// red unread dot on its handle. Opening it is now a click on that handle.
await page.getByRole('button', { name: /Show chat/i }).first().click();
await page.waitForTimeout(600);
{
  // Located by placeholder, not by `input[type=text]` - the element has no
  // explicit type attribute, which is valid HTML and exactly the kind of
  // difference a selector chosen from the source rather than the rendered page
  // gets wrong.
  const box = page.getByPlaceholder(/Say something|Message your guild/).first();
  const marker = `probe-${Date.now()}`;
  await box.fill(marker);
  await box.press('Enter');
  await page.waitForTimeout(2500);
  const text = await page.evaluate(() => document.body.innerText);
  // A message the server echoed back is proof the whole round trip works:
  // RequestChatMessage out, ResponseChatMessage in, decoded, rendered.
  record('chat message round-trips through the server', text.includes(marker));

  // Shut the dock and confirm the handle is back, so a failure to close is not
  // mistaken for "no unread" later.
  await page.getByRole('button', { name: /Hide chat/i }).first().click();
  await page.waitForTimeout(400);
  record(
    'the chat dock closes back to its handle',
    (await page.getByRole('button', { name: /Show chat/i }).count()) > 0,
  );
}

// --- the hub map -------------------------------------------------------------
// Signing in used to land on Combat behind a wall of nav words. The painted
// valley is the menu now: five places, each a plate on its own landmark.
// The suite has walked through several screens by now, so it has to come back
// to the map before asking what is on it.
await go('Map');
{
  const plates = await page.locator('.place').count();
  record('the hub map shows its five places', plates === 5, `${plates} plates`);

  const hubImage = await page.evaluate(() => {
    const scene = document.querySelector('.scene');
    return scene ? getComputedStyle(scene).backgroundImage : '';
  });
  record('the hub background is loaded art, not a colour', /main_hub\.webp/.test(hubImage));

  // Clicking a PLATE - scoped to the map, not the nav button of the same name.
  await page.locator('.place').filter({ hasText: 'Market' }).first().click();
  await page.waitForTimeout(900);
  const leftTheMap = (await page.locator('.scene').count()) === 0;
  const text = await page.evaluate(() => document.body.innerText);
  record('a plate navigates to its screen', leftTheMap && /Sell|Market/i.test(text));
}

// --- gathering ---------------------------------------------------------------
// Reported from a live session: "when I go fishing, XP is added to mining and
// fishing is not there at all", plus a "Backpack full - EVERYTHING IS STOPPED"
// banner about a minute in. Both were real. Every XP router read
// `professionType == 0 ? Woodcutting : Mining`, and the loot census had started
// measuring an UNLIMITED chest against a 20 slot ceiling.
await go('Gathering');
{
  // Parsed by walking lines rather than by building a RegExp from a template
  // literal: `\s` inside backticks is just "s", so a constructed pattern
  // silently matches nothing and the check then fails for a reason that has
  // nothing to do with the game. That happened on the first run of this very
  // check.
  // Modul: READ THE PUBLISHED VALUE, DO NOT PARSE THE DISPLAY.
  //
  // This used to pull the XP back out of the rendered line, and it needed two
  // rounds of comment to survive doing so: thousands are grouped with a
  // NON-BREAKING SPACE in this locale, so a [\d,]+ pattern stopped at the space
  // and reported the whole track as missing - intermittently, because it only
  // bit once a number passed a thousand.
  //
  // Compaction is the same trap one order of magnitude up. Mastery XP is now
  // written as "1.2M" past a million, which the old pattern would have read as
  // 12 - a number that is not wrong in any way a test could notice, on a check
  // that compares before against after. So the screen publishes `data-exact`
  // and this reads that instead. A display format that a checker parses is a
  // display format nobody can change afterwards.
  const readMastery = async (name) => {
    const found = await page.evaluate((trackName) => {
      const term = [...document.querySelectorAll('dt')].find(
        (el) => el.textContent.trim() === trackName,
      );
      const detail = term?.nextElementSibling;
      if (!detail) return null;

      const exact = detail.querySelector('[data-exact]')?.getAttribute('data-exact');
      const level = /level\s+(\d+)/i.exec(detail.textContent ?? '');
      if (exact === null || exact === undefined || !level) return null;

      return { level: Number(level[1]), xp: Number(exact) };
    }, name);

    return found && Number.isFinite(found.xp) && Number.isFinite(found.level) ? found : null;
  };

  const fishingBefore = await readMastery('Fishing');
  const miningBefore = await readMastery('Mining');
  record(
    'every profession has its own mastery track',
    fishingBefore !== null && miningBefore !== null && (await readMastery('Woodcutting')) !== null,
    'Woodcutting, Mining and Fishing all shown',
  );

  // Scoped by the profession heading, not by button order. Every node button
  // on this screen is labelled "Gather", so `.first()` or a fixed index is one
  // content change away from silently testing woodcutting instead. (It used to
  // key off "Activity ids 3000-3999" - that line is gone now the nodes are
  // named after the five locations rather than numbered.)
  const fishingSection = page
    .locator('section')
    .filter({ has: page.getByRole('heading', { name: 'Fishing', exact: true }) })
    .last();
  // The first location is the only one a fresh account has reached, so it is
  // the only one with a Gather button - the rest read "Fight here first".
  const fishBtn = fishingSection.getByRole('button', { name: 'Gather' }).first();
  const deployed = (await fishBtn.count()) > 0;
  if (deployed) await fishBtn.click();

  record(
    'gathering is locked to locations the player has reached',
    (await page.getByText('Fight here first').count()) > 0,
    'later locations are gated',
  );
  await page.waitForTimeout(9000);

  const fishingAfter = await readMastery('Fishing');
  const miningAfter = await readMastery('Mining');

  const fishingMoved =
    fishingAfter !== null &&
    fishingBefore !== null &&
    (fishingAfter.xp > fishingBefore.xp || fishingAfter.level > fishingBefore.level);
  const miningMoved =
    miningAfter !== null &&
    miningBefore !== null &&
    (miningAfter.xp > miningBefore.xp || miningAfter.level > miningBefore.level);

  if (deployed) {
    record('fishing raises fishing mastery', fishingMoved, `fishing xp ${fishingBefore?.xp} -> ${fishingAfter?.xp}`);
    record('fishing does not raise mining mastery', !miningMoved, `mining xp ${miningBefore?.xp} -> ${miningAfter?.xp}`);
  }

  // Modul: GATHERING USED TO GRANT NOTHING. The tick rolled the node's loot
  // table, picked a winner, spent a backpack slot and broke - there was no
  // write to CommodityRecords anywhere on the gathering path. Mastery XP went
  // up (which is what the checks above measure), so the professions looked
  // alive while producing not one log. This asserts the OUTPUT.
  const hauled = await page.evaluate(() => {
    const heading = [...document.querySelectorAll('h2')].find(
      (h) => /Hauled this session/i.test(h.textContent ?? ''),
    );
    return heading?.closest('section')?.innerText ?? '';
  });
  // "Nothing yet." is SessionLoot's empty state. Matched exactly rather than
  // as the word "nothing", which also appears in this panel's own description.
  record(
    'gathering actually yields materials',
    hauled.length > 0 && !/Nothing yet\./.test(hauled),
    hauled.split(String.fromCharCode(10)).filter(Boolean).slice(-1)[0] ?? 'empty',
  );

  const text = await page.evaluate(() => document.body.innerText);
  record(
    'no backpack-full halt anywhere',
    !/Backpack full|EVERYTHING IS STOPPED/i.test(text),
    'storage is the unlimited village chest',
  );
}

// --- auto-eat accepts what you caught ----------------------------------------
// Modul: food was "anything with _food in its BaseId", which no raw fish
// carries - so a player could fish all day, watch the catch land in the chest,
// and be told by the larder that they had no food. Cooking is not in the
// design list; a fish IS the meal.
await go('Auto-Eat');
// Task 59/60: the fixture is level 40 with every building, so nothing in its
// menu may be greyed - a greyed entry here is a rule nobody can reach.
{
  const greyed = await page.evaluate(() =>
    [...document.querySelectorAll('header button[data-locked]')].map((b) => b.dataset.label),
  );
  record('the dev fixture sees no greyed menu entry', greyed.length === 0, greyed.join(', ') || 'none');
  // Task 76: and no greyed tab either - the fixture is past level 10.
  const greyedTabs = await page.evaluate(() =>
    [...document.querySelectorAll('[data-subtab][data-locked]')].map((b) => b.getAttribute('data-subtab')),
  );
  record('the dev fixture sees no greyed tab', greyedTabs.length === 0, greyedTabs.join(', ') || 'none');
  const tabs = await page.evaluate(() =>
    [...document.querySelectorAll('[data-subtab]')].map((b) => b.getAttribute('data-subtab')),
  );
  record('Supplies opens on Auto-Eat with a Boosts tab beside it', tabs.join(',') === 'larder,boosts', tabs.join(','));
}
{
  const text = await page.evaluate(() => document.body.innerText);
  record(
    'auto-eat can be stocked with the fish you caught',
    !/No food in the chest/i.test(text),
    text.match(/Choose food\.\.\./) ? 'food list offered' : 'panel shown',
  );
}

// --- Workshop commissions (task 83) ------------------------------------------
// Modul: THIS RUNS BEFORE "crafting as a job". That step puts a character to
// work on a recipe, and the worker then eats birch log and copper ore every
// few seconds - the same stock this step proves it gave back, so the round
// trip came out 20 log and 10 ore short on a Workshop that refunds exactly.
// The material sink: one region piece at the Workshop's rarity floor, with an
// affix the player picks, for hours and tens of thousands of materials. Proved
// by what CHANGED - the quoted materials leave the stock, a clock starts, and
// collecting puts a piece in the chest carrying the chosen affix at Common.
//
// ROUND-TRIPS the fixture: the dev route finishes the order at once AND gives
// its price back, and the collected piece is binned - so the stock and the
// chest end where they started and this passes on every run, not once.
await go('Crafting');
{
  const panel = page.locator('[data-testid="workshop-commissions"]');
  const shown = await panel.waitFor({ timeout: 10000 }).then(() => true).catch(() => false);
  record('the Crafting screen offers Workshop commissions', shown);

  let view = await apiGet('/api/v1/workshop');
  // A run interrupted mid-step leaves an order standing; clear it first so the
  // check below starts from an empty Workshop (refund, collect, bin).
  if (view?.Commission) {
    await apiPost('/api/v1/dev/workshop/finish', { Refund: true });
    const leftover = await apiPost('/api/v1/workshop/collect', {});
    if (leftover?.Collected) await apiPost('/api/v1/chest/discard', { equipmentId: leftover.Collected.InstanceId });
    view = await apiGet('/api/v1/workshop');
  }

  // Region 1, always: it is open to every account, and its Common floor costs
  // a few thousand of what the fixture is seeded with - a deeper region the
  // fixture happens to have opened would ask for ten times its stock.
  const region = (view?.Regions ?? []).find((r) => r.Region === 1 && r.Unlocked);
  if (!view || !region) {
    record('a commission spends its quoted materials and starts the clock', false, '/api/v1/workshop did not answer');
  } else if (view.WorkshopLevel === 0 || region.FloorTier === 0) {
    record('a commission spends its quoted materials and starts the clock', false, 'the fixture has no Workshop - re-seed');
  } else if (!region.Affordable) {
    record(
      'a commission spends its quoted materials and starts the clock',
      false,
      `the fixture cannot pay ${region.Cost.map((l) => `${l.Quantity} ${l.ItemId} (has ${l.Held})`).join(', ')} - re-seed`,
    );
  } else {
    const piece = region.Pieces[0];
    const affix = piece.Affixes[0];
    const heldBefore = Object.fromEntries(region.Cost.map((l) => [l.ItemId, l.Held]));

    const regionChip = panel.locator('button.chip', { hasText: /^\s*Region 1\s*$/ });
    if ((await regionChip.count()) > 0) await regionChip.first().click();
    await panel.locator('[data-testid="workshop-pieces"] button').first().click();
    await panel.locator('[data-testid="workshop-affixes"] button').first().click();
    await panel.locator('[data-testid="workshop-commission"]').click();
    const started = await panel
      .locator('[data-testid="workshop-running"]')
      .waitFor({ timeout: 10000 })
      .then(() => true)
      .catch(() => false);

    const placed = await apiGet('/api/v1/workshop');
    const placedRegion = placed?.Regions.find((r) => r.Region === region.Region);
    const charged = (placedRegion?.Cost ?? []).every((l) => l.Held === heldBefore[l.ItemId] - l.Quantity);
    const countdown = await panel.locator('[data-testid="workshop-countdown"]').innerText().catch(() => '');
    record(
      'a commission spends its quoted materials and starts the clock',
      started && charged && placed?.Commission?.ItemId === piece.ItemId && placed.Commission.ChosenAffixId === affix
        && !placed.Commission.Ready && /Ready in/.test(countdown),
      `${piece.BaseItemId} + ${affix}, floor ${placed?.Commission?.FloorName}, "${countdown}"`,
    );

    // Finish it now and give the price back, then collect it in the UI.
    await apiPost('/api/v1/dev/workshop/finish', { Refund: true });
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForTimeout(1200);
    await dismissOfflineSummary(3000);
    await go('Crafting');

    const inventoryBefore = ((await apiGet('/api/v1/player/inventory'))?.Equipment ?? []).map((e) => e.Id);
    const collectBtn = panel.locator('[data-testid="workshop-collect"]');
    const enabled = await collectBtn
      .and(page.locator('button:not([disabled])'))
      .waitFor({ timeout: 10000 })
      .then(() => true)
      .catch(() => false);
    if (enabled) await collectBtn.click();
    const collectedLine = await panel
      .locator('[data-testid="workshop-collected"]')
      .waitFor({ timeout: 10000 })
      .then(() => true)
      .catch(() => false);

    const inventoryAfter = (await apiGet('/api/v1/player/inventory'))?.Equipment ?? [];
    const fresh = inventoryAfter.filter((e) => !inventoryBefore.includes(e.Id));
    const made = fresh.find((e) => e.BaseItemId === piece.BaseItemId);
    record(
      'collecting a commission puts the piece in the chest with the chosen affix at Common',
      enabled && collectedLine && !!made && made.QualityTier >= region.FloorTier && `${affix}@1` in (made.Affixes ?? {}),
      made ? `T${made.QualityTier} ${Object.keys(made.Affixes ?? {}).join(', ')}` : `nothing new of ${piece.BaseItemId}`,
    );

    // Restore: bin the piece; the refund already put the stock back.
    if (made) await apiPost('/api/v1/chest/discard', { equipmentId: made.Id });
    const after = await apiGet('/api/v1/workshop');
    const afterRegion = after?.Regions.find((r) => r.Region === region.Region);
    record(
      'the commission check leaves the fixture as it found it',
      !after?.Commission && (afterRegion?.Cost ?? []).every((l) => l.Held === heldBefore[l.ItemId]),
      (afterRegion?.Cost ?? []).map((l) => `${l.ItemId} ${heldBefore[l.ItemId]} -> ${l.Held}`).join(', '),
    );
  }
}

// --- crafting as a job -------------------------------------------------------
// Crafting used to be instant and needed no character: every recipe carried a
// CraftingTimeMs that nothing read. It is now an activity in its own band, so
// the proof is that a character ends up REPORTING it as their job.
await go('Crafting');
{
  const text = await page.evaluate(() => document.body.innerText);
  record(
    'crafting is presented as a job, not a button',
    /Crafting takes time and needs a character/i.test(text),
  );

  // Modul: CRAFT NOW is the other half, added 2026-09-01. Assigning a
  // character crafts one unit per interval forever while materials last, which
  // is right for idling and wrong for "I need a pickaxe" - so making one tool
  // meant assigning a worker and then remembering to stop them. The batch box
  // multiplies both the cost and the output.
  //
  // Counted off the inventory rather than read off a toast: a batch of ten has
  // to produce ten EquipmentInstances, and only counting them proves the
  // multiplier reached the engine rather than just the label.
  const countEquipment = async () => {
    const body = await apiGet('/api/v1/player/inventory');
    return body ? (body.Equipment ?? []).length : -1;
  };

  const batchBox = page.getByRole('checkbox').filter({ hasNot: page.locator('nothing') }).last();
  const craftBtn = page.getByRole('button', { name: /^Craft(\s|$|\sx)/ }).first();
  const canCraft = (await craftBtn.count()) > 0;
  record('the crafting screen offers a direct Craft button', canCraft);

  if (canCraft) {
    // Tick "Craft x10" by its label so this does not depend on checkbox order.
    const tenLabel = page.locator('label.check', { hasText: /Craft x10/i }).locator('input');
    if ((await tenLabel.count()) > 0) await tenLabel.check().catch(() => {});
    await page.waitForTimeout(300);

    const enabled = page.getByRole('button', { name: /^Craft x10$/ }).and(page.locator('button:not([disabled])')).first();
    if ((await enabled.count()) > 0) {
      const before = await countEquipment();
      await enabled.click();
      await page.waitForTimeout(2500);
      const after = await countEquipment();
      // Modul: THE MASTER ARTISAN WEEK. That event (EventBanner, id 3) gives
      // every craft a 25% chance of one extra item, so ten crafts produce ten
      // to twenty - and "exactly ten" failed about 94% of runs for a whole week
      // in four, on a crafting path that was working. Ten is still the floor;
      // the ceiling moves only while the event is on.
      const artisan = await page.evaluate(() => /Master Artisan/.test(document.body.innerText));
      const made = after - before;
      record(
        'a x10 craft produces ten items in one press',
        before >= 0 && (artisan ? made >= 10 && made <= 20 : made === 10),
        `${before} -> ${after}${artisan ? ' (Master Artisan week)' : ''}`,
      );
    } else {
      record('a x10 craft produces ten items in one press', true, 'no recipe affordable at x10 - skipped');
    }
  }

  // Enabled only when the chest holds the recipe's materials, which a fresh
  // fixture may not - a disabled button is a correct answer, not a stall.
  const work = page.getByRole('button', { name: /Put to work/i }).first();
  const hasWork = (await work.count()) > 0 && (await work.isEnabled());
  if (hasWork) {
    await work.click();
    await page.waitForTimeout(1500);

    await go('Character');
    // The person switcher names each person's job; the craft may be on any of them.
    const roster = await everyPersonText();
    // The roster names the craft rather than "Idle" or a bare activity id.
    record(
      'an assigned character reports the craft as its job',
      /Smelting:|Cooking:|Alchemy:|Equipment:/i.test(roster),
      'roster shows the recipe',
    );
  }
}

// --- guild -------------------------------------------------------------------
await go('Guild');
{
  const text = await page.evaluate(() => document.body.innerText);
  record('guild screen loads roster and depot', /Depot|Roster|Guild war/i.test(text));
  record('cross-shard section resolves', !text.includes('Checking for a match'), 'match query settled');

  // Modul: GUILD WARS ARE LOCKED behind a population floor (GuildWarUnlock).
  // The screen has to say so WITH the progress the server reports, not just
  // "No war is active" for ever. Compared against the endpoint itself so the
  // check holds on a box that has crossed the floor too.
  const warUnlock = await apiGet('/api/v1/guild/war-unlock');
  if (warUnlock === null) {
    record('the guild war lock reports its progress', false, '/api/v1/guild/war-unlock did not answer');
  } else if (warUnlock.Unlocked) {
    record('the guild war lock reports its progress', true, 'already unlocked on this server');
  } else {
    const lockLine = await page
      .locator('[data-testid="guild-war-locked"]')
      .waitFor({ timeout: 10000 })
      .then(() => true)
      .catch(() => false);
    const warText = await page.evaluate(() => document.body.innerText);
    const shows = `${warUnlock.QualifyingPlayers} / ${warUnlock.RequiredPlayers}`;
    record(
      'the guild war lock reports its progress',
      lockLine && warText.includes(shows) && warText.includes(`${warUnlock.QualifyingGuilds} / ${warUnlock.RequiredGuilds}`),
      `players ${shows}, guilds ${warUnlock.QualifyingGuilds} / ${warUnlock.RequiredGuilds}`,
    );
  }

  // Modul: the whole Donate Materials panel shipped DEAD and rendered
  // perfectly while doing so. depotMaterial held a base-id string from the
  // <select> while depotMax looked it up by numeric definition id, so the
  // comparison never matched, depotMax was permanently 0, and all three
  // buttons are disabled on `depotMax === 0`. The live database had zero rows
  // in GuildDepotBalances and GuildContributionLedgers as a result.
  //
  // A render check cannot see any of that, which is exactly why it is checked
  // here instead. The assertion is that the button ENABLES and the donation
  // LANDS - not that the panel exists.
  const materialSelect = page.locator('select').filter({ hasText: 'Choose...' }).first();

  // The options carry base ids. Two kinds matter and they exercise different
  // code: a BUFF_MATERIAL_IDS entry proves the enable path, and one that is
  // NOT in that set proves the log/ore filter renders at all - it used to test
  // `definition.Subtype`, a field ItemDefinition does not have, so the branch
  // silently produced no options whatsoever.
  const options = await materialSelect.locator('option').evaluateAll((els) =>
    els.map((o) => ({ value: o.value, label: o.textContent.trim() })),
  );
  // The label carries the held quantity as "(xN)". Anything reading x0 is
  // listed but not owned - the buff set is rendered unconditionally - so an
  // option is only useful here if the fixture actually holds some.
  const held = options.filter((o) => o.value && !/\(x0\)\s*$/.test(o.label));

  // Modul: must be a CATALOGUED material. GuildDepotBalances is keyed on
  // ItemDefinitionId, so a commodity with no items.json entry is a 400 however
  // much of it the player holds. Four of the twenty buff materials used to be
  // exactly that; copper_ore, iron_ore, obsidian_ore and silver_ore were
  // catalogued on 2026-09-01 and all twenty are donatable now.
  const buffOption = held.find((o) => o.value === 'birch_log' || o.value === 'malachite_ore');

  // raw_log and oak_log are NOT in BUFF_MATERIAL_IDS, so they can only come
  // from the second block - the one whose filter used to test a Subtype field
  // ItemDefinition does not have, and therefore rendered nothing at all.
  const plainLogOre = held.find((o) => o.value === 'raw_log' || o.value === 'oak_log');

  record('donate dropdown offers a held buff material', Boolean(buffOption), buffOption?.label);
  record(
    'the log/ore filter lists materials outside the hardcoded buff set',
    Boolean(plainLogOre),
    plainLogOre ? plainLogOre.label : 'only the hardcoded buff set is listed',
  );

  // The uncatalogued side of the same rule, pinned rather than left implicit.
  // raw_log and oak_log are gathering slugs the Village spends and items.json
  // does not carry, so the depot cannot store them and the button must stay
  // disabled rather than offering a 400. This check used to watch copper_ore
  // and fired correctly the moment copper_ore was catalogued, which is what it
  // is for - if these two ever gain an ItemDefinition, expect it to fail again
  // and move it to whatever is still uncatalogued.
  const uncatalogued = held.find((o) => o.value === 'raw_log' || o.value === 'oak_log');
  if (uncatalogued) {
    await materialSelect.selectOption(uncatalogued.value);
    const donateBtn = page.getByRole('button', { name: 'Donate', exact: true }).first();
    const off = await donateBtn.evaluate((b) => b.disabled);
    record(
      'a material the depot cannot store is not offered as donatable',
      off,
      off ? `${uncatalogued.value} has no ItemDefinition - correctly disabled` : "enabled, and the server will refuse it",
    );
  }

  if (buffOption) {
    await materialSelect.selectOption(buffOption.value);

    // Modul: read `.disabled` through evaluate rather than isDisabled(). The
    // editability check is defined for inputs and selects and answers "not
    // disabled" for anything else, which is how a greyed-out control once
    // reported a broken feature as working.
    const donate = page.getByRole('button', { name: 'Donate', exact: true }).first();
    const stillDisabled = await donate.evaluate((b) => b.disabled);
    record(
      'choosing a material enables the Donate button',
      !stillDisabled,
      stillDisabled ? 'still disabled - depotMax did not resolve' : buffOption.value,
    );

    if (!stillDisabled) {
      // Modul: WAIT FOR THE OUTCOME, NOT FOR ANY CHANGE AT ALL.
      //
      // This used to wait for document.body.innerText to differ from a snapshot
      // taken before the click, then read the page. The donation is a REST
      // round trip and the panel refetches on a timer, so the FIRST thing that
      // changes is usually the depot number arriving - before the confirmation
      // has rendered. The check then read too early and reported a working
      // feature as broken.
      //
      // Verified by hand on 2026-09-05: POST /api/v1/guilds/depot/donate
      // answers 200, the depot balance rises and the contribution points rise
      // with it. The donation was never the problem.
      //
      // Waiting for the OUTCOME - the confirmation or a named failure - is the
      // form that keeps holding. Same discipline as the three checks that were
      // red on a working game for a long time; see CLAUDE.md.
      await donate.click();

      const settled = await page
        .waitForFunction(
          () => /donated/i.test(document.body.innerText)
            || /Failed to donate|not in a guild|must be positive/i.test(document.body.innerText),
          undefined,
          { timeout: 15000 },
        )
        .then(() => true)
        .catch(() => false);

      const after = await page.evaluate(() => document.body.innerText);
      const failed = /Failed to donate|not in a guild|must be positive/i.test(after);
      record(
        'donating a material is accepted by the server',
        settled && !failed,
        failed
          ? after.match(/Failed to donate.*/i)?.[0] ?? 'refused'
          : settled
            ? 'contribution points granted'
            : 'no outcome shown within 15s - the panel said nothing either way',
      );
    }
  }
}

// --- leave the guild, and come back (task 94) --------------------------------
//
// Modul: LeaveGuildAsync existed for months with no route and no button, so a
// player in a dead guild was stuck for good. This leaves through the real
// confirm and REJOINS, so the fixture ends the step in the guild it started in.
//
// Only one shape can be restored exactly: the fixture is a plain Member of an
// OPEN guild with others in it, and Join puts it back. Everything else is only
// ARMED and read, then disarmed - never committed:
//   - the LAST member: leaving CLOSES the guild. Refounding it under the same
//     name gives a new guild id with an empty depot, treasury and buffs, so
//     every run would spend the fixture's guild (CLAUDE.md: a check that
//     spends fixture state passes once and fails forever);
//   - a leader with others: the successor would lead for good;
//   - an application-only guild: rejoining files an application.
// GuildLeaveTests commits all of those on the server, HTTP route included.
await go('Guild');
{
  const stats = await apiGet('/api/v1/player/statistics');
  const guildName = stats?.GuildName ?? '';
  const preview = await apiGet('/api/v1/guilds/leave-preview');
  const directory = (await apiGet('/api/v1/guilds/list')) ?? [];
  const entry = directory.find((g) => g.Name === guildName);

  const leaveButton = page.getByRole('button', { name: /^(Leave guild|Really leave\?|Really close it\?)$/ }).first();
  // The note is drawn from the preview query, which lands after the screen.
  const noteText = async () => {
    const note = page.locator('[data-testid="guild-leave-note"]').first();
    await note.waitFor({ timeout: 10000 }).catch(() => {});
    return (await note.textContent().catch(() => '')) ?? '';
  };

  if (!guildName || !preview?.InGuild) {
    record('leaving a guild round-trips the fixture', false, 'the fixture is in no guild - nothing to leave (re-seed, or found one by hand)');
  } else {
    const closes = preview.ClosesGuild === true;
    const canRestore = !closes && !preview.IsLeader && entry && entry.JoinType === 0;

    // The confirm must say what the server will do BEFORE the second tap.
    const note = await noteText();
    record(
      'the leave confirm says what leaving will do',
      closes ? /closes the guild/i.test(note) : preview.IsLeader ? /will lead/i.test(note) : note.length > 0,
      note || 'no note under the Leave guild button',
    );

    if (!canRestore) {
      await leaveButton.click();
      const armed = await leaveButton.textContent();
      await page.keyboard.press('Escape');
      record(
        'leaving a guild round-trips the fixture',
        /Really/.test(armed ?? ''),
        `armed only ("${armed?.trim()}") - ${closes ? 'leaving would close the fixture\'s guild and lose its depot' : preview.IsLeader ? 'a leader with others would hand the guild over for good' : 'the guild takes applications, so rejoining is not immediate'}`,
      );
    } else {
      await leaveButton.click();
      await leaveButton.click();

      let left = false;
      for (let i = 0; i < 20 && !left; i++) {
        await page.waitForTimeout(500);
        left = ((await apiGet('/api/v1/player/statistics'))?.GuildName ?? 'x') === '';
      }
      record('the Leave guild confirm takes the fixture out of its guild', left, left ? guildName : 'still a member 10s after the second tap');

      if (left) {
        // Social's Join/Create used to stay disabled for good; they must open now.
        await go('Friends');
        const escaped = guildName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        await page
          .locator('li.guild')
          .filter({ has: page.locator('.name', { hasText: new RegExp(`^\\s*${escaped}\\s*$`) }) })
          .getByRole('button', { name: 'Join', exact: true })
          .first()
          .click();

        let back = false;
        for (let i = 0; i < 20 && !back; i++) {
          await page.waitForTimeout(500);
          back = ((await apiGet('/api/v1/player/statistics'))?.GuildName ?? '') === guildName;
        }
        const after = await apiGet('/api/v1/guilds/leave-preview');
        const sameRole = after?.InGuild === true && after.IsLeader === preview.IsLeader;
        record(
          'leaving a guild round-trips the fixture',
          back && sameRole,
          back
            ? `rejoined "${guildName}"${sameRole ? '' : ' but the role changed'}`
            : `not back in "${guildName}" - THE FIXTURE IS NOW GUILDLESS; join it by hand`,
        );
      }
    }
  }
}

// --- private messages persist -------------------------------------------------
//
// Modul: chat used to be written down NOWHERE. Every channel was Redis fan-out
// to whoever happened to be connected, and the client kept the last 200 lines
// in a store a page reload wiped. Two things followed, and the second was a
// defect rather than a gap: there was no history, and a whisper to an OFFLINE
// player was silently dropped - the dispatch looked the recipient up in the
// connected-client map and returned, so the sender saw it sent and the
// recipient never learned it existed.
//
// This asserts the durable half. The message is sent through the real UI, then
// read back through the conversations endpoint - if persistence regresses, the
// send still LOOKS fine and only this check notices.
// Chat is the floating dock, not a nav tab - see the round-trip check above.
await page.getByRole('button', { name: /Show chat/i }).first().click();
await page.waitForTimeout(600);
{
  const stamp = `e2e-${Date.now()}`;
  const whisperTab = page.getByRole('button', { name: 'Whispers', exact: true });
  const hasWhispers = (await whisperTab.count()) > 0;
  record('chat offers a whispers channel', hasWhispers);

  if (hasWhispers) {
    // Modul: the recipient must exist on ANY database, not only the owner's
    // (the old hardcoded name was an account that lives in one dev DB, so a
    // fresh one failed both checks below and added a 404). Register a
    // throwaway over the same REST the client uses - it is a second account
    // by construction, which whispering to yourself would not be.
    const whisperStamp = Date.now();
    const whisperName = `exercise${(whisperStamp + 7) % 1_000_000}`;
    const whisperReg = await fetch(`${API_BASE}/api/v1/auth/register`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: `${whisperName}w${whisperStamp}@folkidle.local`,
        password: 'FolkIdleExercise123!',
        username: whisperName,
        deviceId: `exercise-whisper-${whisperStamp}`,
      }),
    }).then((r) => r.status).catch(() => 0);
    record('a whisper recipient account can be registered', whisperReg >= 200 && whisperReg < 300, `${whisperName} -> HTTP ${whisperReg}`);

    await whisperTab.first().click();
    await page.waitForTimeout(400);

    // The recipient is resolved by NAME to a player id before the message is
    // sent, so this needs a real second account - registered just above.
    const target = page.getByPlaceholder(/who|player|name/i).first();
    const composer = page.getByPlaceholder(/Say something|Message|whisper/i).last();

    if ((await target.count()) > 0 && (await composer.count()) > 0) {
      await target.fill(whisperName);
      await composer.fill(stamp);
      await composer.press('Enter');
      await page.waitForTimeout(1500);

      // Read back with the app's own token rather than a second login.
      const rows = (await apiGet('/api/v1/conversations/list')) ?? [];
      const thread = rows.find((r) => r.LastMessage === stamp);
      record(
        'a private message is written down, not just broadcast',
        Boolean(thread),
        thread
          ? `thread with ${thread.Username}`
          : `${rows.length} thread(s), none carrying the sent text`,
      );

      // Modul: and that the SCREEN shows it. The Whispers tab used to be one
      // flat log of every whisper from everybody; it is a list of people now,
      // and a list that renders while showing nothing is the exact failure
      // this whole file exists to catch.
      //
      // Reloading first is the point: it proves the thread came from the
      // server rather than from the in-memory log, which a reload wipes and
      // which was previously the ONLY place a message existed.
      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(1500);
      await dismissOfflineSummary(3000);
      // The "FolkIdle has been updated" prompt sits in the same bottom-right
      // corner as the chat handle, and a reload does not clear it on a dev
      // server whose bundle changed during the session. dismissToasts presses
      // its "Later" now, never its "Reload".
      await dismissToasts();
      await page.getByRole('button', { name: /Show chat/i }).first().click();
      await page.waitForTimeout(600);
      await page.getByRole('button', { name: 'Whispers', exact: true }).first().click();
      await page.waitForTimeout(1200);

      const listed = page.locator('.thread', { hasText: whisperName }).first();
      const inList = (await listed.count()) > 0;
      record('the whisper list survives a reload', inList, inList ? `${whisperName} listed` : 'no thread rendered');

      if (inList) {
        await listed.click();
        await page.waitForTimeout(1500);
        const threadText = await page.locator('.thread-log').innerText().catch(() => '');
        record(
          'opening a conversation shows its history',
          threadText.includes(stamp),
          threadText.includes(stamp) ? 'the sent message is in the thread' : 'thread opened but the message is absent',
        );
      }
    }
  }

  // Modul: SHUT THE DOCK. It is a floating overlay, so leaving it open makes
  // its handle intercept pointer events for every check that follows - the
  // paper doll's slots then fail with "subtree intercepts pointer events",
  // which reads as equipment being broken rather than as this block being
  // untidy. The round-trip check above closes it for the same reason.
  await page.getByRole('button', { name: /Hide chat/i }).first().click().catch(() => {});
  await page.waitForTimeout(400);
}

// --- the affix lock ----------------------------------------------------------
//
// Modul: THE READ SIDE OF THIS WAS WIRED IN TEN PLACES AND THE WRITE SIDE DID
// NOT EXIST. IsAffixLocked was honoured by the reroll, by forge fusion, by the
// command validator and by both chest removal paths, and set to true by
// nothing - so none of that code could ever run and three wire bytes carried a
// constant zero.
//
// What it protects against is the sweep, which clears a whole rarity band in
// one call. Its ceiling of Epic was the only way to say "not that one", and a
// ceiling cannot express "keep THIS Epic sword".
await go('Chest');
{
  // Task 81: the lock moved into the row's "More" menu, and its state is a
  // "Locked" badge on the row. The row is addressed by its equipment id so a
  // refetch that re-renders the list cannot swap the piece under the check.
  const firstRow = page.locator('.row[data-equipment-id]').first();
  const present = (await firstRow.count()) > 0;
  record('the chest offers a lock on each piece', present);

  if (present) {
    const id = await firstRow.getAttribute('data-equipment-id');
    const row = page.locator(`.row[data-equipment-id="${id}"]`);
    const isLocked = async () => (await row.locator('.lockbadge').count()) > 0;
    const toggleLock = async () => {
      await row.getByRole('button', { name: 'More', exact: true }).click();
      await page.getByRole('menuitem', { name: /^(Lock|Unlock)$/ }).click();
    };
    const waitFor = async (want) => {
      for (let i = 0; i < 30 && (await isLocked()) !== want; i++) await page.waitForTimeout(500);
      return isLocked();
    };

    const before = await isLocked();
    await toggleLock();
    const after = await waitFor(!before);
    record('locking a piece changes its state on the server', after !== before, `${before} -> ${after}`);

    // Modul: AND PUT IT BACK. A check that leaves the fixture locked would
    // change what every later run of this script is looking at - the same
    // discipline the Ancestors "Keep" and the village steps had to learn.
    await toggleLock();
    const restored = await waitFor(before);
    record('the lock round-trips both ways', restored === before, `back to ${restored}`);
  }

  // --- undo on a sale (task 81) ---------------------------------------------
  //
  // The client holds a sale for five seconds before sending it. Asserted as a
  // round trip that spends nothing: sell, undo, wait past the window, and the
  // piece must still be on the SERVER - not just still on the screen.
  const sellableId = await page.evaluate(() => {
    const rows = [...document.querySelectorAll('.row[data-equipment-id]')];
    const row = rows.find(
      (r) => !r.querySelector('.lockbadge') && [...r.querySelectorAll('button')].some((b) => b.textContent.trim() === 'Equip'),
    );
    return row ? row.getAttribute('data-equipment-id') : null;
  });
  if (sellableId === null) {
    record('a sale can be undone', false, 'no unworn, unlocked piece in view to try it on');
  } else {
    const row = page.locator(`.row[data-equipment-id="${sellableId}"]`);
    await row.getByRole('button', { name: 'More', exact: true }).click();
    // The menu names the server's price (SellValueGold), not a client guess.
    const sellItem = page.getByRole('menuitem', { name: /^Sell · / });
    const sellText = ((await sellItem.textContent().catch(() => '')) ?? '').trim();
    const priced = ((await apiGet('/api/v1/player/inventory'))?.Equipment ?? []).find((e) => String(e.Id) === sellableId);
    record(
      'the Sell item names the price the server will pay',
      Boolean(priced) && priced.SellValueGold > 0 && sellText.replace(/\D/g, '') !== '' && /^Sell · /.test(sellText),
      `"${sellText}" vs SellValueGold ${priced?.SellValueGold}`,
    );
    await sellItem.click();
    const undo = row.getByRole('button', { name: 'Undo', exact: true });
    const offered = (await undo.count()) > 0;
    if (offered) await undo.click();
    await page.waitForTimeout(6500);
    const inv = await apiGet('/api/v1/player/inventory');
    const stillThere = (inv?.Equipment ?? []).some((e) => String(e.Id) === sellableId);
    record('a sale can be undone', offered && stillThere, offered ? `piece ${sellableId} kept: ${stillThere}` : 'no Undo button appeared');
  }
}
// --- the Wiki's odds line (task 26) -------------------------------------------
//
// Modul: the line is the SERVER's arithmetic over the luck its loot worker last
// rolled with, so it can only read "Your odds" once a kill from the combat step
// above has reached that worker. A line stuck on "after your next kill" after
// minutes of fighting means the odds snapshot was never written.
await go('Wiki');
{
  await page.getByRole('button', { name: /Items & rarity/ }).first().click();
  const line = page.getByTestId('loot-odds-line');
  await line.waitFor({ timeout: 10000 }).catch(() => {});
  await page.waitForFunction(
    () => !/Working out/.test(document.querySelector('[data-testid="loot-odds-line"]')?.textContent ?? ''),
    { timeout: 10000 },
  ).catch(() => {});
  const text = ((await line.textContent().catch(() => '')) ?? '').replace(/\s+/g, ' ').trim();
  record(
    'the wiki quotes the odds the loot roll is using',
    /Your odds:.*1 in [\d,\s ]+ is Ancient or better/.test(text),
    text.slice(0, 160),
  );
}

// --- world boss --------------------------------------------------------------
//
// Modul: this used to be three presses of a button that posted a damage figure
// the CLIENT computed about itself. It is five armour plates now, one of them
// soft, and the player picks which to strike - see docs/world_boss_design.md.
//
// Modul: AND IT USED TO STRIKE ONLY WHEN THE CALENDAR ALLOWED (task 25). The
// window is the 1st-7th and 15th-22nd UTC, so on 13-16 days a month this block
// never pressed Strike at all - which is how "no attack has ever landed" went
// uncaught. It opens its own window now through the dev-only route
// (FOLKIDLE_DEV_TOOLS=1, set by run-dev.ps1; the route 404s everywhere else),
// strikes, asserts the world changed, and closes it again. Opening a window
// deletes every attempt row, so each run starts with three fresh attempts and
// the round trip leaves the fixture as the calendar would.
const bossWindow = (open) =>
  apiPostStatus('/api/v1/dev/worldboss/window', open ? { open: true, durationSeconds: 900 } : { open: false });
const bossStateIs = (label, timeout) =>
  page
    .waitForFunction(
      (want) => (document.querySelector('.state')?.textContent ?? '').trim() === want,
      label,
      { timeout },
    )
    .then(() => true)
    .catch(() => false);

await go('World Boss');
{
  const openStatus = await bossWindow(true);
  record(
    'a world boss window can be opened for this run',
    openStatus === 200,
    openStatus === 404
      ? 'the server answered 404 - start it with FOLKIDLE_DEV_TOOLS=1 (run-dev.ps1 does)'
      : `status ${openStatus}`,
  );
  const active = openStatus === 200 && (await bossStateIs('Active', 70000));
  record('the forced window reaches the screen', active);

  const text = await page.evaluate(() => document.body.innerText);
  record('world boss state is shown', /Active|Dormant|Concluded/.test(text));

  const plates = page.locator('.armour-plate');
  const plateCount = await plates.count();
  record('the boss shows its armour', plateCount === 5, `${plateCount} plates`);

  // Modul: THE SCREEN MUST NOT ASK FOR A DECISION IT WILL NOT SHOW THE INPUTS
  // TO. Every plate says intact, broken or soft; a picker that hid that would
  // be a slot machine wearing a puzzle's clothes.
  const plateStates = await page.evaluate(() =>
    [...document.querySelectorAll('.armour-plate .armour-plate-state')].map((el) => el.textContent.trim()),
  );
  record(
    'every plate says what state it is in',
    plateStates.length === 5 && plateStates.every((t) => /intact|broken|soft/.test(t)),
    plateStates.join(', '),
  );

  // Modul: UNDER THE WHEEL (FOLKIDLE_BOSS_MINIGAME=wheel, task 36 Phase 2) the
  // plate buttons are the AUTO-strike over REST and button.attack opens the
  // wheel, so this block strikes with the auto button; the wheel itself has
  // its own block below.
  // The mode arrives by REST after the screen renders; give it a moment.
  await page.waitForSelector('[data-testid="wheel-strike"]', { timeout: 5000 }).catch(() => {});
  const wheelMode = (await page.locator('[data-testid="wheel-strike"]').count()) > 0;
  const plateStrike = wheelMode ? 'button.auto' : 'button.attack';

  // Picking a plate has to change what the button says it will do, or the
  // choice is invisible at the moment it matters.
  if (plateCount === 5) {
    await plates.nth(3).click();
    await page.waitForTimeout(200);
    const label = await page
      .locator(plateStrike)
      .first()
      .innerText()
      .catch(() => '');
    record('choosing a plate is reflected on the button', /4/.test(label), label.trim());
  }

  if (active) {
    const strike = page.locator(plateStrike).first();
    const disabled = await strike.isDisabled();
    const reason = await page.locator('.strike-reason').innerText().catch(() => '');
    record('a fresh window lets the fixture strike', !disabled, disabled ? `grey: ${reason}` : '');

    if (!disabled) {
      const read = () =>
        page.evaluate(() => ({
          hp: Number(document.querySelector('.bar[role="progressbar"]')?.getAttribute('aria-valuenow') ?? -1),
          pips: document.querySelectorAll('.pip.spent').length,
          states: [...document.querySelectorAll('.armour-plate .armour-plate-state')].map((el) => el.textContent.trim()),
        }));
      const before = await read();
      const boardBefore = Number((await apiGet('/api/v1/worldboss/board'))?.Me?.Damage ?? 0);

      await strike.click();
      await page
        .waitForFunction((n) => document.querySelectorAll('.pip.spent').length > n, before.pips, { timeout: 10000 })
        .catch(() => {});
      await page.waitForTimeout(600);
      const after = await read();

      // The attempt is the thing the server always spends, whichever plate
      // was struck. The plate STATES change too, but only when the strike
      // missed the weak point - so the pip and the health are the honest
      // assertions and the plate change is reported rather than required.
      record('striking a plate spends an attempt', after.pips > before.pips, `${before.pips} -> ${after.pips} spent`);
      // Modul: the fixture's OWN damage, not the boss's HP (TASK_BOARD 53).
      // LiveOps rescales the shared HP with the population, and a run on
      // 2026-09-28 saw it rise 50M -> 75M across a strike that had landed.
      const boardAfter = Number((await apiGet('/api/v1/worldboss/board'))?.Me?.Damage ?? 0);
      record('the strike adds to the fixture damage on the board', boardAfter > boardBefore, `${boardBefore} -> ${boardAfter} (hp ${before.hp} -> ${after.hp})`);
      record(
        'the strike is reflected on the boss',
        after.states.join() !== before.states.join() || after.pips > before.pips,
        `${before.states.join('/')} -> ${after.states.join('/')}`,
      );

      const row = await apiGet('/api/v1/dev/worldboss/attempt');
      record(
        'the strike is recorded on the server',
        row !== null && row.AttemptCount >= 1 && row.TotalInflictedDamage > 0,
        row === null ? 'no answer' : `attempts ${row.AttemptCount}, damage ${row.TotalInflictedDamage}`,
      );
      if (wheelMode) {
        const card = page.locator('[data-testid="auto-card"]');
        const damage = Number((await card.getAttribute('data-damage').catch(() => null)) ?? 0);
        record('the auto-strike shows its damage on the screen', damage > 0, `${damage} damage`);

        // The boss does not have to fall (owner, 2026-09-26): the damage board
        // is what a strike is for, so it has to move when one lands.
        await page
          .waitForFunction(() => /#\d+/.test(document.querySelector('[data-testid="boss-me"]')?.textContent ?? ''), null, { timeout: 10000 })
          .catch(() => {});
        const me = await page.locator('[data-testid="boss-me"]').innerText().catch(() => '');
        const total = await page.locator('[data-testid="boss-total"]').innerText().catch(() => '');
        record(
          'the damage board shows your place and what everyone dealt together',
          /#\d+/.test(me) && (damage > 100000 ? /\d\s*[kMBT]\b/.test(me) : me.replace(/[\s\u00a0\u202f,.]/g, '').includes(String(damage))) && /dealt/.test(total),
          `"${me.trim()}" / "${total.trim()}"`,
        );
        const grey = await page.locator('[data-testid="wheel-strike"]').isDisabled();
        const reason = await page.locator('.strike-reason').innerText().catch(() => '');
        record(
          "a second strike the same day is greyed with today's reason",
          grey && /today's strike/i.test(reason),
          `grey ${grey}: "${reason.trim()}"`,
        );
      }
    }
  }

  // A closed window must say WHY the button is grey, next to the button. The
  // owner saw a grey Strike on a dormant day with the reason far above it,
  // and read it as broken.
  const closeStatus = await bossWindow(false);
  const concluded = closeStatus === 200 && (await bossStateIs('Concluded', 15000));
  const greyReason = await page.locator('.strike-reason').innerText().catch(() => '');
  const greyNow = await page.locator('button.attack').first().isDisabled().catch(() => false);
  record(
    'a closed window greys the strike and says why beside it',
    concluded && greyNow && /not here|next encounter|returns/i.test(greyReason),
    `status ${closeStatus}, grey ${greyNow}: "${greyReason.trim()}"`,
  );
}

// --- the shield wheel, practice (task 36 Phase 1) -----------------------------
//
// Modul: PRACTICE MUST PROVE SKILL MOVES THE NUMBER, AND THAT IT TOUCHES NOTHING.
// Two runs: one aimed (every interrupt read correctly and countered, every
// wheel spear timed to a seam crossing) and one that reads every tell wrongly.
// The aimed run must reach M >= 1.6 on the card; the wrong run must show lost
// spears. Neither may move the boss's health or an attempt pip - practice is
// free by design (spec 1, decision 5).
//
// The taps are fired INSIDE the page, against the overlay's own t0 (data-t0),
// because a Playwright click cannot choose its event timestamp and the score is
// all timestamps. The geometry below is the same arithmetic as
// src/lib/game/shieldWheel.ts, duplicated here only to aim.
const wheelAngleAt = (s, t) => {
  let a = s.StartAngleDeg;
  for (const seg of s.Segments) {
    if (t <= seg.StartMs) break;
    const end = seg.StartMs + seg.DurationMs;
    a += (seg.DegPerSec * (Math.min(t, end) - seg.StartMs)) / 1000;
    if (t <= end) break;
  }
  return ((a % 360) + 360) % 360;
};
const wheelFrozenAt = (s, t) => s.Interrupts.some((i) => t >= i.TellAtMs && t < i.TellAtMs + i.InterruptMs);
/** Landing times (ms after t0) when some plate's seam centre sits at the impact point. */
const seamLandings = (s) => {
  const out = [];
  let prev = wheelAngleAt(s, 0);
  for (let t = 1; t < s.MaxPlayMs - 200; t++) {
    const cur = wheelAngleAt(s, t);
    if (!wheelFrozenAt(s, t)) {
      let d = cur - prev;
      if (d > 180) d -= 360;
      if (d < -180) d += 360;
      const lo = Math.min(prev, prev + d);
      const hi = Math.max(prev, prev + d);
      for (let p = 0; p < 5; p++) {
        const c = p * 72 + 36;
        if ([c - 360, c, c + 360].some((x) => x > lo && x <= hi)) out.push(t);
      }
    }
    prev = cur;
  }
  return out;
};
// The buttons are named by the blow since the 2026-09-25 playtest: the right
// answer is the side the tell says the blow comes from.
const correctParry = { Left: 'From the left', Right: 'From the right', Overhead: 'From above' };
const wrongParry = { Left: 'From above', Right: 'From the left', Overhead: 'From the right' };

/**
 * Plays one run in the page. `aimed` reads and counters; otherwise every read
 * is wrong. The same overlay serves practice and the real strike (task 36
 * Phase 2); only the card at the end differs.
 */
async function playPractice(aimed, card = 'practice-card', multiplier = 'practice-m') {
  await page.locator('[data-schedule]').waitFor({ timeout: 10000 });
  const schedule = JSON.parse(await page.locator('[data-schedule]').getAttribute('data-schedule'));

  const actions = [];
  const interrupts = schedule.Interrupts;
  for (const i of interrupts) {
    actions.push({ at: i.TellAtMs + 250, kind: 'parry', label: aimed ? correctParry[i.Tell] : wrongParry[i.Tell] });
    if (aimed) actions.push({ at: i.TellAtMs + 650, kind: 'counter', plate: 0 });
  }
  // Wheel spears: the rest of the five, at seam crossings minus the flight
  // time, spaced past the reload and clear of every freeze and its edges.
  const wheelCount = aimed ? 5 - interrupts.length : 5;
  let last = -1e9;
  const wheel = [];
  for (const landing of seamLandings(schedule)) {
    const tap = landing - schedule.FlightMs;
    if (tap < 150 || tap - last < 450) continue;
    if (wheelFrozenAt(schedule, tap) || wheelFrozenAt(schedule, tap + 60) || wheelFrozenAt(schedule, tap - 60)) continue;
    // Aimed: keep the wheel spears before the last interrupt's counter so
    // each counter still has a spear to throw. Wrong: throw after the
    // first tell so the missed reads are REACHED and cost their spears.
    wheel.push(tap);
    last = tap;
    if (wheel.length === wheelCount) break;
  }
  if (!aimed) {
    // Modul: ONE SPEAR BEFORE THE FIRST TELL, THE REST AFTER IT (2026-09-26).
    // An interrupt only costs a spear if it is REACHED - some spear is thrown
    // after it (spec 5.5). Taps spread evenly from 800 ms failed about one
    // schedule in four: the first tell fell after the fourth tap, its wrong
    // read spent the client's fifth spear, and the server - rightly - counted
    // nothing lost, so the card said so. Throwing right after the first freeze
    // ends makes the read reached on every schedule.
    wheel.length = 0;
    const first = interrupts[0];
    const after = first.TellAtMs + first.InterruptMs + 200;
    const plan = [800, ...[0, 1, 2, 3].map((k) => after + k * 1500)];
    for (let t of plan) {
      while (wheelFrozenAt(schedule, t) || wheelFrozenAt(schedule, t + 60)) t += 100;
      wheel.push(t);
    }
  }
  for (const t of wheel) actions.push({ at: t, kind: 'tap' });
  actions.sort((a, b) => a.at - b.at);

  await page.locator('[data-schedule][data-t0]').waitFor({ timeout: 10000 });
  await page.evaluate(async (plan) => {
    const overlay = document.querySelector('[data-schedule]');
    const t0 = Number(overlay.getAttribute('data-t0'));
    const sleepUntil = (ms) => new Promise((r) => setTimeout(r, Math.max(0, t0 + ms - performance.now())));
    for (const a of plan) {
      await sleepUntil(a.at);
      if (a.kind === 'tap') {
        const zone = overlay.querySelector('.throw-zone');
        zone?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch' }));
      } else if (a.kind === 'parry') {
        const button = [...overlay.querySelectorAll('button')].find((b) => b.textContent.includes(a.label));
        button?.click();
      } else if (a.kind === 'counter') {
        const button = [...overlay.querySelectorAll('.plate-btn')].find((b) => b.textContent.trim() === String(a.plate + 1));
        button?.click();
      }
    }
  }, actions);

  await page.locator(`[data-testid="${card}"]`).waitFor({ timeout: 40000 });
  return {
    m: Number((await page.locator(`[data-testid="${multiplier}"]`).innerText()).replace(/[^0-9.]/g, '')),
    text: await page.locator(`[data-testid="${card}"]`).innerText(),
    damage: Number((await page.locator(`[data-testid="${card}"]`).getAttribute('data-damage')) ?? 0),
    played: Number((await page.locator(`[data-testid="${card}"]`).getAttribute('data-played')) ?? 0),
    interrupts: interrupts.length,
  };
}

{
  await go('World Boss');
  const practiceButton = page.getByRole('button', { name: /Practice the shield wheel/i }).first();
  const offered = (await practiceButton.count()) > 0;
  record(
    'the World Boss screen offers shield wheel practice',
    offered,
    offered ? '' : 'no Practice button - start the server with FOLKIDLE_BOSS_MINIGAME=practice (run-dev.ps1 does)',
  );

  if (offered) {
    // Modul: A WINDOW OF ITS OWN (2026-09-25). There is a boss every week now,
    // so after the block above closes its dev window, LiveOps reopens THIS
    // week's encounter on its next 60-second tick - fresh HP, attempts wiped -
    // and a before/after that spans that reopening compares two different
    // encounters. Holding a dev window open keeps LiveOps' hands off for the
    // whole practice block, so any change really would be practice's doing.
    const practiceWindow = await bossWindow(true);
    await bossStateIs('Active', 70000);
    await page.waitForTimeout(1000);
    // Not the boss's HP: LiveOps rescales it with the online population every
    // minute, so it moves on its own. What practice must not touch is THIS
    // player's attempt row - its count and the damage it has dealt.
    const board = async () => {
      const row = await apiGet('/api/v1/dev/worldboss/attempt');
      return {
        attempts: row?.AttemptCount ?? 0,
        damage: row?.TotalInflictedDamage ?? 0,
        pips: await page.evaluate(() => document.querySelectorAll('.pip.spent').length),
      };
    };
    const before = await board();

    await practiceButton.click();
    const aimed = await playPractice(true);
    record(
      'an aimed practice run reads, counters and scores high',
      aimed.m >= 1.6 && /No damage dealt/i.test(aimed.text),
      `M ${aimed.m.toFixed(2)} with ${aimed.interrupts} interrupts`,
    );

    await page.locator('[data-schedule]').getByRole('button', { name: /Practice again/i }).first().click();
    const wrong = await playPractice(false);
    record(
      'wrong reads cost spears in practice',
      /lost to missed reads/i.test(wrong.text) && wrong.m < aimed.m,
      `M ${wrong.m.toFixed(2)}; ${/(\d+) spears? lost/.exec(wrong.text)?.[0] ?? 'nothing lost'}`,
    );

    await page.locator('[data-schedule]').getByRole('button', { name: /^\s*Close\s*$/i }).first().click();
    await page.waitForTimeout(500);

    // Code review, 2026-09-25: a reopened run must resume from what the
    // server kept, not restart at Seq 0 with five spears (which replayed the
    // server's OLD answers against new taps). Throw one, reload, reopen.
    await practiceButton.click();
    await page.locator('[data-schedule][data-t0]').waitFor({ timeout: 10000 });
    const schedule = JSON.parse(await page.locator('[data-schedule]').getAttribute('data-schedule'));
    const firstTap = (() => {
      for (let t = 200; t < schedule.MaxPlayMs; t += 50) if (!wheelFrozenAt(schedule, t) && !wheelFrozenAt(schedule, t + 200)) return t;
      return 200;
    })();
    await page.evaluate(async (at) => {
      const overlay = document.querySelector('[data-schedule]');
      const t0 = Number(overlay.getAttribute('data-t0'));
      await new Promise((r) => setTimeout(r, Math.max(0, t0 + at - performance.now())));
      overlay.querySelector('.throw-zone')?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch' }));
    }, firstTap);
    await page.waitForTimeout(1200);
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    await dismissOfflineSummary(3000);
    await go('World Boss');
    await page.getByRole('button', { name: /Practice the shield wheel/i }).first().click();
    await page.locator('[data-schedule]').waitFor({ timeout: 10000 });
    await page.waitForTimeout(600);
    const resumed = await page.evaluate(() => ({
      chips: document.querySelectorAll('[data-schedule] .chip').length,
      spears: document.querySelector('[data-schedule] .status [aria-label$="spears left"]')?.getAttribute('aria-label') ?? '',
    }));
    record(
      'a reopened practice run resumes with the spear it already threw',
      resumed.chips === 1 && /^[0-4] spears left$/.test(resumed.spears),
      `${resumed.chips} chip(s), "${resumed.spears}"`,
    );
    // Leave the half-played run behind; it expires on its own.
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    await dismissOfflineSummary(3000);
    await go('World Boss');

    const after = await board();
    record(
      'practice moved neither the boss nor an attempt',
      practiceWindow === 200 && before.attempts === after.attempts && before.damage === after.damage && before.pips === after.pips,
      `window ${practiceWindow}, attempts ${before.attempts} -> ${after.attempts}, damage ${before.damage} -> ${after.damage}, pips ${before.pips} -> ${after.pips}`,
    );
    await bossWindow(false);
  }
}

// --- the shield wheel, for real (task 36 Phase 2) ------------------------------
//
// Modul: THIS IS THE CHECK THAT PROVES SKILL REACHES DAMAGE. A blind run (every
// read wrong, spears spread evenly) and an aimed run (every read right and
// countered, every spear on a seam) each spend a real strike. One strike a
// day, so each run gets a fresh dev window (opening one deletes the attempt
// rows); the damage compared is the card's, because two windows are two boss
// health bars.
//
// NOT "the aimed run deals more". Each attempt draws its own weak plate, and a
// played strike is never worth less than auto-striking the best plate it hit
// (spec 3.2), so a blind run that happens to land on the weak plate is paid
// 3.0x and can match an aimed run that did not (measured 2026-09-26: blind
// M 1.00 and aimed M 2.00 both dealt 3,000). What is invariant is the wiring:
// both runs hit with the same base (damage / played), and the aimed run's
// damage is at least that base times its higher M.
{
  await go('World Boss');
  const wheelOffered = (await page.locator('[data-testid="wheel-strike"]').count()) > 0;
  if (!wheelOffered) {
    record(
      'the World Boss screen strikes with the shield wheel',
      false,
      'no wheel Strike - start the server with FOLKIDLE_BOSS_MINIGAME=wheel (run-dev.ps1 does)',
    );
  } else {
    const realRun = async (aimed) => {
      const status = await bossWindow(true);
      await bossStateIs('Active', 70000);
      await page.waitForTimeout(1000);
      const button = page.locator('[data-testid="wheel-strike"]');
      await page.waitForFunction(() => !document.querySelector('[data-testid="wheel-strike"]')?.disabled, null, { timeout: 15000 }).catch(() => {});
      if (status !== 200 || (await button.isDisabled())) return null;
      await button.click();
      const run = await playPractice(aimed, 'strike-card', 'strike-m');
      await page.locator('[data-schedule]').getByRole('button', { name: /^\s*Close\s*$/i }).first().click();
      await page.waitForTimeout(500);
      return run;
    };

    const blind = await realRun(false);
    record(
      'a blind shield wheel strike lands at the floor or a little above',
      blind !== null && blind.damage > 0 && blind.m >= 1.0 && blind.m <= 1.6,
      blind ? `M ${blind.m.toFixed(2)}, ${blind.damage} damage` : 'the strike could not be opened',
    );
    const aimed = await realRun(true);
    const base = (run) => (run && run.played > 0 ? run.damage / run.played : NaN);
    record(
      'an aimed shield wheel strike scores higher, and its skill reaches the damage',
      blind !== null &&
        aimed !== null &&
        aimed.m > blind.m + 0.2 &&
        Math.abs(base(aimed) - base(blind)) <= 1 &&
        aimed.damage >= Math.floor(base(aimed) * aimed.m),
      aimed && blind
        ? `M ${blind.m.toFixed(2)} -> ${aimed.m.toFixed(2)}, played ${blind.played} -> ${aimed.played}, damage ${blind.damage} -> ${aimed.damage}, base ${base(blind)} / ${base(aimed)}`
        : 'a run was missing',
    );
    const row = await apiGet('/api/v1/dev/worldboss/attempt');
    record(
      'the wheel strike is recorded on the server',
      row !== null && row.AttemptCount === 1 && row.TotalInflictedDamage === (aimed?.damage ?? -1),
      row === null ? 'no answer' : `attempts ${row.AttemptCount}, damage ${row.TotalInflictedDamage}`,
    );
    await bossWindow(false);
  }
}

// --- the objective track: the game keeps answering "what now" -----------------
//
// Modul: TIER THREE, AND WHY IT NEEDED A CHECK OF ITS OWN.
//
// Tier one stops after three steps, about ten minutes in. Tier two is reactive
// - it explains a system the first time the player REACHES it, and says nothing
// about one they have not found. So a mature account had nothing telling it
// what to do next, which is the reported problem this closes.
//
// The fixture is exactly the account that proves it: level 40, geared, and past
// every discovery in the table. Working through them one "Got it" at a time and
// arriving at an OBJECTIVE is the only way to show the chain actually reaches
// its third tier on a real account rather than only in a node runner.
//
// Modul: THIS CHECK SPENDS FIXTURE STATE, and only `--seed-dev` gives it back.
// Walking to the objective tier means clicking "Got it" on everything in front
// of it, and an acknowledgement is persisted on the PLAYER ROW
// (OnboardingSeenIds) rather than in the browser - so the second run of this
// script in a row finds no cue at all and reports "only saw no cue". That is
// the repo's own "a check that spends fixture state passes once and fails
// forever" trap, and it cannot be made to round-trip from here: the acknowledge
// is the only way to advance the chain. Re-seed before believing a failure.
{
  let seenKinds = new Set();
  let reachedObjective = null;

  for (let i = 0; i < 30; i++) {
    const panel = page.locator('.coach').first();
    if ((await panel.count()) === 0) break;

    const kind = await panel.getAttribute('data-onboarding-kind');
    const id = await panel.getAttribute('data-onboarding-cue');
    if (kind) seenKinds.add(kind);
    if (kind === 'objective' && !reachedObjective) {
      reachedObjective = { id, text: (await panel.innerText()).replace(/\s+/g, ' ').slice(0, 90) };
      break;
    }

    // Task 70: a card already read folds to its title line on the next
    // screen, so open it the way a player would - by its header - first.
    const head = panel.locator('button.head[aria-expanded="false"]');
    if ((await head.count()) > 0) {
      await head.first().click();
      await page.waitForTimeout(150);
    }

    const gotIt = panel.getByRole('button', { name: /^Got it$/ });
    if ((await gotIt.count()) === 0) break;
    await gotIt.first().click();
    await page.waitForTimeout(250);
  }

  record(
    'a mature account is told what to do next, not just what it has found',
    Boolean(reachedObjective),
    reachedObjective ? `${reachedObjective.id}: ${reachedObjective.text}` : `only saw ${[...seenKinds].join(', ') || 'no cue'}`,
  );

  // Modul: an objective must be ACTIONABLE - it names a screen the nav
  // actually has. A dead nav key sends the player nowhere and announces
  // nothing, which is the same class as the screen lists that rotted in three
  // separate checkers.
  if (reachedObjective) {
    const folded = page.locator('.coach button.head[aria-expanded="false"]');
    if ((await folded.count()) > 0) await folded.first().click();
    await page.locator('.coach').first().getByRole('button', { name: /Take me there/i }).click();
    await page.waitForTimeout(900);
    const arrived = await page.evaluate(() => document.body.innerText.length > 0);
    record('an objective can take you to the screen it is about', arrived);

    // Acting on it is acknowledging it: the same objective must not come back.
    const after = page.locator('.coach[data-onboarding-cue="' + reachedObjective.id + '"]');
    record(
      'and acting on an objective retires it',
      (await after.count()) === 0,
      'the panel moved on rather than repeating itself',
    );
  }
}

// --- the Delve: gold goes in, and the world has to change ---------------------
//
// Modul: A GOLD SINK IS ONLY A SINK IF THE GOLD ACTUALLY LEAVES.
//
// This is the exact shape this whole file exists to catch: a screen of doors
// that render beautifully and resolve nothing. The assertions are therefore on
// the SERVER's own view of the run, read back through /api/v1/delve, not on
// what the page happens to be drawing - and the last one is read after a
// reload, because a run that lives only in the tab is not a run.
await go('The Delve');
{
  const before = await apiGet('/api/v1/delve');
  record(
    'the Delve prices a run against the region reached',
    Boolean(before) && before.EntryFeeForNextRun > 0,
    before ? `region ${before.HighestRegionReached}, ${before.EntryFeeForNextRun.toLocaleString()}g` : 'no view',
  );

  if (before && !before.Active && before.CurrentGold >= before.EntryFeeForNextRun) {
    const goldBefore = before.CurrentGold;

    await page.getByRole('button', { name: /Pay and descend/i }).first().click();
    await page.waitForTimeout(1200);

    const started = await apiGet('/api/v1/delve');
    record(
      'paying the gate opens a run and takes the gold',
      Boolean(started?.Active) && started.CurrentGold === goldBefore - before.EntryFeeForNextRun,
      started ? `${goldBefore.toLocaleString()} -> ${started.CurrentGold.toLocaleString()}g, floor ${started.CurrentFloor}` : 'no run',
    );

    record(
      'a floor offers three doors and at least one says what it wants',
      Boolean(started) && started.DoorDemands.length === 3 && started.DoorDemands.some((d) => d >= 0),
      started ? started.DoorDemands.map((d) => (d < 0 ? '???' : ['Might', 'Finesse', 'Vigour', 'Fortune'][d])).join(' / ') : '',
    );

    // Modul: the odds for a HIDDEN door must not be published - they would give
    // the demand away by inference and make Fortune worthless. Asserted here
    // and in DelveEngineTests, because this one crosses the wire.
    record(
      'a door that has not shown itself publishes no odds',
      Boolean(started) && started.DoorDemands.every((d, i) => (d < 0 ? started.DoorOdds[i] === -1 : started.DoorOdds[i] > 0)),
    );

    // Open a door. The outcome is the server's to decide, so this asserts that
    // SOMETHING resolved - a floor cleared or a charge burned - rather than
    // predicting which.
    await page.locator('.door').first().click();
    await page.waitForTimeout(1200);

    const afterDoor = await apiGet('/api/v1/delve');
    const resolved =
      !afterDoor?.Active ||
      afterDoor.FloorsCleared > started.FloorsCleared ||
      afterDoor.ChargesRemaining < started.ChargesRemaining;
    record(
      'opening a door is resolved by the server',
      resolved,
      afterDoor?.Active
        ? `floor ${afterDoor.CurrentFloor}, ${afterDoor.FloorsCleared} cleared, ${afterDoor.ChargesRemaining} charges`
        : 'the run ended on that door',
    );

    if (afterDoor?.Active) {
      await page.getByRole('button', { name: /Climb out/i }).first().click();
      await page.waitForTimeout(1200);
    }

    // Modul: AFTER A RELOAD. The run row, the gold and the diamonds are all
    // server state, and the only assertion that proves it is one made against
    // a page that has thrown its memory away. The persistence defect this
    // codebase shipped in September was invisible to every in-session check.
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    await dismissOfflineSummary(3000);

    const settled = await apiGet('/api/v1/delve');
    record(
      'the run is closed out and does not survive a reload',
      Boolean(settled) && !settled.Active,
      settled?.Active ? `still on floor ${settled.CurrentFloor}` : 'no run in progress',
    );

    // The entry fee is at least twice the best consolation, so a completed run
    // always costs gold on net however it went. That is what makes it a sink.
    record(
      'the gold actually left',
      Boolean(settled) && settled.CurrentGold < goldBefore,
      settled ? `${goldBefore.toLocaleString()} -> ${settled.CurrentGold.toLocaleString()}g` : '',
    );

    record(
      'the weekly diamond ceiling is stated, not hidden',
      Boolean(settled) && settled.WeeklyDiamondCeiling > 0 && settled.DiamondsEarnedThisWeek <= settled.WeeklyDiamondCeiling,
      settled ? `${settled.DiamondsEarnedThisWeek} of ${settled.WeeklyDiamondCeiling} this week` : '',
    );
  } else {
    // Written as a conditional rather than a hard failure, the same shape the
    // attribute and locked-slot checks use: a fixture too poor for the gate
    // reports itself instead of looking like a broken feature.
    record(
      'the Delve gate is reachable',
      Boolean(before),
      before?.Active ? 'a run is already in progress' : 'fixture cannot afford the gate - farm gold or re-seed',
    );
  }
}

// --- the Deep: a toll has to leave, and the record has to move ----------------
//
// Modul: TASK 37. The Deep is an endless, gold-tolled continuation past floor
// 8, and the defect it is most exposed to is the one this file exists for: a
// "Descend" button that renders, takes a click, and moves nothing. So every
// assertion is on the SERVER's view, the gold is checked to the coin, and the
// last read is after a page reload.
//
// A run at the bottom of floor 8 cannot be reached reliably by playing - the
// doors are a gamble by design - so the dev-only route puts one there. It
// round-trips: the run is walked out of, so the next exercise starts clean, and
// the fixture's 5M (DevFixtureInvariantTests holds it at ten region-5 gates)
// pays a toll priced at half a percent of what it holds.
{
  const res = await fetch(`${API_BASE}/api/v1/dev/delve/at-bottom`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${await authToken()}`, 'Content-Type': 'application/json' },
    body: '{}',
  });

  if (res.status === 404) {
    record(
      'the Deep: a run can be placed at the bottom of floor 8',
      false,
      'the server answered 404 - start it with FOLKIDLE_DEV_TOOLS=1 (run-dev.ps1 does)',
    );
  } else {
    // Reloaded, not just navigated: the Delve screen may already be open with
    // the view it read before the dev route moved the run.
    await go('The Delve');
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    await dismissOfflineSummary(3000);
    await go('The Delve');
    await page.waitForTimeout(800);
    const atBottom = await apiGet('/api/v1/delve');

    if (!atBottom?.DeepEnabled) {
      record(
        'the Deep is open on the dev box',
        false,
        'DeepEnabled is false - start the server with FOLKIDLE_DELVE_DEEP=on (run-dev.ps1 does)',
      );
    } else {
      const g0 = atBottom.CurrentGold;
      const stake = atBottom.StakeGold;
      const toll = atBottom.DescendQuote;
      const bankPayout = atBottom.ConsolationGoldIfCapped;
      const diamondsBefore = atBottom.DiamondsEarnedThisWeek;
      const diamondsBanked = atBottom.DiamondsAfterCeiling;

      record(
        'the bottom of floor 8 offers a descent with a toll',
        atBottom.Active && atBottom.AtLanding && atBottom.CanDescend && toll > 0 && stake >= atBottom.EntryFeeForNextRun,
        `stake ${stake.toLocaleString()}g, toll ${toll.toLocaleString()}g`,
      );

      await page.getByRole('button', { name: /Descend into the Deep/i }).first().click();
      await page.waitForTimeout(1500);

      const down = await apiGet('/api/v1/delve');
      const expectedGold = g0 + bankPayout - toll;
      record(
        'descending banks floors 1-8 and takes exactly the toll',
        Boolean(down) && down.CurrentGold === expectedGold,
        down ? `${g0.toLocaleString()} + ${bankPayout.toLocaleString()} - ${toll.toLocaleString()} = ${expectedGold.toLocaleString()}; server says ${down.CurrentGold.toLocaleString()}` : 'no view',
      );
      record(
        'the run is in the Deep on floor 9, on a frozen stake',
        Boolean(down) && down.Active && down.IsDeep && down.CurrentFloor === 9 && down.StakeGold === stake,
        down ? `floor ${down.CurrentFloor}, deep=${down.IsDeep}, stake ${down.StakeGold.toLocaleString()}` : '',
      );

      // A lantern, bought at the price the SCREEN showed, must take exactly
      // that - and the request carries no price at all. The light is put out
      // by the dev route rather than by failing doors until chance obliges.
      const dark = await apiPostStatus('/api/v1/dev/delve/lantern-out', {});
      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(1500);
      await dismissOfflineSummary(3000);
      await go('The Delve');
      await page.waitForTimeout(800);
      const unlit = await apiGet('/api/v1/delve');
      const lanternPrice = unlit?.LanternPrice ?? 0;
      record(
        'the Deep offers a lantern when the light goes out',
        dark === 200 && Boolean(unlit) && unlit.ChargesRemaining === 0 && lanternPrice === stake,
        unlit ? `status ${dark}, charges ${unlit.ChargesRemaining}, price ${lanternPrice.toLocaleString()}g (stake ${stake.toLocaleString()}g)` : `status ${dark}`,
      );

      if (lanternPrice > 0) {
        // Modul: what the lantern COST is read off the server's answer to the
        // click (GoldCharged), not off two balance reads - the fixture is still
        // fighting, so the balance drifted by combat income in between and this
        // failed on an exact charge (+580g, 2026-10-01). Same fix as Walk out.
        const lanternAnswer = page
          .waitForResponse((r) => r.url().includes('/api/v1/delve/deep/lantern'), { timeout: 10000 })
          .then((r) => r.json())
          .catch(() => null);
        await page.getByRole('button', { name: /Light another lantern/i }).first().click();
        const lit = await lanternAnswer;
        await page.waitForTimeout(1500);
        const relit = await apiGet('/api/v1/delve');
        record(
          'a lantern takes exactly the quoted price and relights the run',
          lit?.GoldCharged === lanternPrice && Boolean(relit) && relit.ChargesRemaining === 1 && relit.LanternsBought === 1,
          lit ? `quoted ${lanternPrice.toLocaleString()}g, charged ${lit.GoldCharged.toLocaleString()}g (${lit.Result}), ${relit?.ChargesRemaining} charge` : 'no answer from the lantern route',
        );
      }

      const goldInTheDeep = (await apiGet('/api/v1/delve'))?.CurrentGold;
      // Modul: WHAT WALKING OUT PAID is read off the server's own answer to the
      // click. The gold balance alone drifted by a few hundred between the two
      // reads whenever the fixture was still fighting - the reload below is a
      // relogin, and the catch-up pays the seconds in between - so it failed on
      // a Deep that paid exactly nothing (checked by hand against the API,
      // 2026-09-29).
      const bankAnswer = page
        .waitForResponse((r) => r.url().includes('/api/v1/delve/bank'), { timeout: 10000 })
        .then((r) => r.json())
        .catch(() => null);
      await page.getByRole('button', { name: /^\s*Walk out\s*$/i }).first().click();
      const walkedOut = await bankAnswer;
      await page.waitForTimeout(1200);

      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(1500);
      await dismissOfflineSummary(3000);

      const out = await apiGet('/api/v1/delve');
      record(
        'walking out of the Deep closes the run and pays nothing',
        Boolean(out) && !out.Active && walkedOut?.GoldReturned === 0 && walkedOut?.DiamondsGranted === 0,
        out
          ? `active=${out.Active}, paid ${walkedOut?.GoldReturned}g + ${walkedOut?.DiamondsGranted} diamonds; balance ${goldInTheDeep?.toLocaleString()} -> ${out.CurrentGold.toLocaleString()} (passive income included)`
          : '',
      );
      record(
        'the Deep minted no diamonds: only the floors-1-8 bank moved the weekly count',
        Boolean(out) && out.DiamondsEarnedThisWeek === diamondsBefore + diamondsBanked,
        out ? `${diamondsBefore} + ${diamondsBanked} banked = ${out.DiamondsEarnedThisWeek}` : '',
      );
      record(
        'the record moved',
        Boolean(out) && out.DeepestFloor >= 8 && out.DeepestThisWeek >= 8,
        out ? `deepest ${out.DeepestFloor}, this week ${out.DeepestThisWeek}` : '',
      );

      // Task 61: the Deep is one seeded course per ISO week, and the screen
      // says so - with when it turns over.
      {
        const course = await page.locator('[data-testid="deep-weekly-course"]').first().innerText().catch(() => '');
        record(
          'the Deep names this week as one course for everyone',
          Boolean(out) && out.DeepWeekKey > 202600 && /same course for everyone/i.test(course),
          `week ${out?.DeepWeekKey}, ends ${out?.DeepWeekEndsUtc}: ${course.slice(0, 80)}`,
        );
      }

      // --- the Deepest board shows the record, on the screen -----------------
      await go('Leaderboards');
      await page.getByRole('tab', { name: /Deepest this week/i }).first().click();
      await page.waitForTimeout(1500);
      const board = (await apiGet('/api/v1/leaderboard/deepest'))?.Entries ?? [];
      // The fixture's username is DevFixtureSeeder.Username, 'dev'.
      const selfId = board.find((r) => r.Name === 'dev') ?? null;
      const rowText = selfId
        ? await page.locator('.deepest .board li', { hasText: selfId.Name }).first().textContent().catch(() => '')
        : '';
      record(
        'the Deepest board shows the fixture and its record',
        Boolean(selfId) && (rowText ?? '').includes(`floor ${out?.DeepestThisWeek}`),
        selfId ? `#${selfId.Rank} ${selfId.Name}, floor ${selfId.Floor}; row "${(rowText ?? '').trim().replace(/\s+/g, ' ')}"` : `${board.length} rows, fixture not among them`,
      );

      // --- a title: granted, worn, seen in the profile, and taken off -------
      const granted = await apiPostStatus('/api/v1/dev/titles/grant', { Slug: 'deep_10' });
      await go('The Delve');
      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(1500);
      await dismissOfflineSummary(3000);
      await go('The Delve');
      await page.waitForTimeout(1000);
      const titleButton = page.locator('.titles .picker button', { hasText: 'Lamplighter' }).first();
      if (granted === 200 && (await titleButton.count()) > 0) {
        await titleButton.click();
        await page.waitForTimeout(1200);
        const worn = await apiGet('/api/v1/player/titles');
        record('wearing a title is saved on the server', worn?.Active?.Slug === 'deep_10', worn?.Active ? worn.Active.Name : 'no active title');

        await go('Leaderboards');
        await page.getByRole('tab', { name: /Deepest this week/i }).first().click();
        await page.waitForTimeout(1200);
        const nameButton = selfId ? page.locator('.deepest .who-btn', { hasText: selfId.Name }).first() : null;
        let modalTitle = '';
        if (nameButton && (await nameButton.count()) > 0) {
          await nameButton.click();
          await page.waitForTimeout(1500);
          modalTitle = (await page.locator('.modal .title-badge').first().textContent().catch(() => '')) ?? '';
          await page.locator('.modal .close-btn').first().click().catch(() => {});
        }
        record('the profile shows the title the server named', modalTitle.trim() === 'Lamplighter', `badge "${modalTitle.trim()}"`);

        // Round-trip: take it off, so the next run starts from a bare name.
        await go('The Delve');
        await page.waitForTimeout(800);
        await page.locator('.titles .picker button', { hasText: 'No title' }).first().click();
        await page.waitForTimeout(1200);
        const bare = await apiGet('/api/v1/player/titles');
        record('a title can be taken off again', Boolean(bare) && bare.Active === null, bare?.Active ? bare.Active.Name : 'none worn');
      } else {
        record('a granted title appears in the picker', false, `grant status ${granted}, picker button ${await titleButton.count()}`);
      }
    }
  }
}

// --- task 54: a cosmetic chest, opened and worn, then put back ---------------
// A real chest is thousands of kills or five levels away, so the dev tool puts
// one in the fixture's hands through the real insert. Everything after that is
// the screen: the Open button, the reveal, Wear it. Round-trips the worn avatar
// and frame so the next run starts from the same face.
{
  const before = await apiGet('/api/v1/cosmetics');
  const cosmeticsHeld = (view) => (view?.Owned ?? []).filter((o) => o.Kind !== 0).length;
  const granted = await apiPostStatus('/api/v1/dev/cosmetics/chest', { Rarity: 2 });
  await go('Wardrobe');
  await page.waitForTimeout(800);
  const openButton = page.getByTestId('open-chest-2');
  const openable = granted === 200 && (await openButton.count()) > 0 && (await openButton.isEnabled());
  record('the Wardrobe offers the granted chest', openable, `grant ${granted}, button ${await openButton.count()}`);
  if (openable) {
    await openButton.click();
    await page.waitForTimeout(1500);
    const after = await apiGet('/api/v1/cosmetics');
    record(
      'opening a chest turns it into a cosmetic',
      Boolean(after) && cosmeticsHeld(after) === cosmeticsHeld(before) + 1 && after.Chests[2] === (before?.Chests[2] ?? 0),
      after ? `cosmetics ${cosmeticsHeld(before)} -> ${cosmeticsHeld(after)}, rare chests ${after.Chests[2]}` : 'no view',
    );
    const reveal = page.getByTestId('chest-reveal');
    const revealed = (await reveal.count()) > 0 ? ((await reveal.textContent()) ?? '').trim().replace(/\s+/g, ' ') : '';
    record('the reveal names what came out', revealed.length > 0, revealed);

    await reveal.getByRole('button', { name: 'Wear it' }).click().catch(() => {});
    await page.waitForTimeout(1200);
    const worn = await apiGet('/api/v1/cosmetics');
    const newest = [...(after?.Owned ?? [])].filter((o) => o.Kind !== 0).sort((a, b) => b.Id - a.Id)[0];
    const wearing = newest?.Kind === 1 ? worn?.EquippedAvatarId : worn?.EquippedFrameId;
    record('Wear it puts it on, on the server', Boolean(newest) && wearing === newest.DefinitionId, `${newest?.DefinitionId} worn as ${wearing}`);

    // Put the face back as it was.
    await apiPost('/api/v1/cosmetics/equip', { Kind: 1, Id: before?.EquippedAvatarId ?? null });
    await apiPost('/api/v1/cosmetics/equip', { Kind: 2, Id: before?.EquippedFrameId ?? null });
    const restored = await apiGet('/api/v1/cosmetics');
    record(
      'the fixture wears what it wore before',
      restored?.EquippedAvatarId === (before?.EquippedAvatarId ?? null) && restored?.EquippedFrameId === (before?.EquippedFrameId ?? null),
      `${restored?.EquippedAvatarId ?? 'default'} / ${restored?.EquippedFrameId ?? 'no frame'}`,
    );
  }
}

// --- task 54 phase 4: a cosmetic on the market, and taken down again --------
// Round-trips: whatever is listed is taken down, so the fixture keeps what it
// owns. Uses the Market screen's own controls, at a price no corridor allows.
{
  const owned = (await apiGet('/api/v1/cosmetics'))?.Owned ?? [];
  const spare = owned.find((o) => !o.IsListed && o.Kind !== 0);
  await go('Market');
  await page.getByTestId('market-tab-cosmetics').click();
  await page.waitForTimeout(1200);
  const pick = spare ? page.getByTestId(`cosmetic-sell-${spare.Id}`) : null;
  if (!spare || !pick || (await pick.count()) === 0) {
    record('the cosmetic market offers something to sell', false, `${owned.length} owned, spare ${spare?.Id ?? 'none'}`);
  } else {
    await pick.click();
    await page.getByTestId('cosmetic-sell-price').fill('123456789');
    await page.getByTestId('cosmetic-sell').click();
    await page.waitForTimeout(1500);
    const listed = ((await apiGet('/api/v1/market/cosmetics'))?.Listings ?? []).find((l) => l.CosmeticItemId === spare.Id && l.IsMine);
    record('a cosmetic lists at the seller\'s own price', listed?.Price === 123456789, listed ? `${listed.DefinitionId} for ${listed.Price}` : 'no listing');

    const takeDown = page.locator('[data-testid="cosmetic-listings"] li.mine', { hasText: '123' }).getByTestId('cosmetic-take-down').first();
    if ((await takeDown.count()) > 0) await takeDown.click();
    await page.waitForTimeout(1500);
    const back = ((await apiGet('/api/v1/cosmetics'))?.Owned ?? []).find((o) => o.Id === spare.Id);
    const stillListed = ((await apiGet('/api/v1/market/cosmetics'))?.Listings ?? []).some((l) => l.CosmeticItemId === spare.Id);
    record('taking it down gives it back', Boolean(back) && !back.IsListed && !stillListed, back ? `listed=${back.IsListed}, on market=${stillListed}` : 'gone');
  }
}

// --- task 55: the boss challenges are listed where bosses are fought --------
// Meeting one needs a boss kill with conditions this script cannot arrange in
// a fair time, so the judgement is covered by BossChallengeTests; this checks
// the output side: the server answers for every region, and the Combat screen
// shows region 1's three under region 1.
{
  const regions = (await apiGet('/api/v1/boss-challenges'))?.Regions ?? [];
  record(
    'the server lists three challenges for every region boss',
    regions.length === 5 && regions.every((r) => r.Challenges.length === 3),
    regions.map((r) => `${r.Region}:${r.Challenges.filter((c) => c.Completed).length}/${r.Challenges.length}`).join(' '),
  );
  // Task 98: Challenges and Ascension fold into one line under the boss, and
  // that line exists only once the boss has fallen. An unbeaten boss is
  // checked for the ABSENCE of the line here; the ascension block below marks
  // region 1 beaten and checks the unfolded challenges there.
  const beaten1 = Boolean((await apiGet('/api/v1/boss-ascension'))?.Bosses?.find((b) => b.Region === 1)?.BossDefeated);
  await go('Combat');
  await page.waitForTimeout(1200);
  const fold = page.getByTestId('boss-extras-1');
  if (beaten1) {
    if ((await fold.count()) > 0 && (await fold.getAttribute('aria-expanded')) !== 'true') await fold.click();
    const shown = page.getByTestId('boss-challenges-1');
    const text = (await shown.count()) > 0 ? ((await shown.textContent()) ?? '').replace(/\s+/g, ' ').trim() : '';
    record("Combat shows region 1's boss challenges", /Starved/.test(text) && /Young blood/.test(text) && /Swift/.test(text), text.slice(0, 120));
  } else {
    record("an unbeaten boss folds its challenges away", (await fold.count()) === 0, 'region 1 boss not beaten yet');
  }
}

// --- task 87: the Boss Ascension ladder ---------------------------------------
// The output side, end to end: the server lists ten steps per boss, Combat draws
// them as buttons, a locked step cannot be started, and starting the next one
// puts the character on the boss with the step armed (the button reads "is
// running") - or, when the fixture kills the boss before the poll, clears it.
// ROUND TRIP: the fight is stood down and resumed as it was found, and the
// ladder (and, for a fixture that has never beaten region 1's boss, that
// boss's codex mark) is put back through the dev-tools route, so the check
// leaves no title, frame or progress behind and passes on every run.
{
  const view = await apiGet('/api/v1/boss-ascension');
  const bosses = view?.Bosses ?? [];
  let b1 = bosses.find((b) => b.Region === 1);
  const wasBeaten = Boolean(b1?.BossDefeated);
  const startedAt = b1?.HighestStep ?? 0;
  record(
    'the server lists a ten-step ladder for every region boss',
    bosses.length === 5 && bosses.every((b) => b.Steps.length === 10 && b.Steps.every((s) => s.RewardTitle && s.Effects.length > 0)),
    bosses.map((b) => `${b.Region}:${b.HighestStep}/10`).join(' '),
  );
  record(
    'every step carries the modifiers of the one below it, and pays only a title or a frame',
    Boolean(b1) && b1.Steps.every((s, i) => i === 0 || s.Effects.length >= b1.Steps[i - 1].Effects.length),
    b1 ? b1.Steps.slice(0, 3).map((s) => s.Effects.join(' + ')).join(' | ') : 'no region 1 ladder',
  );

  // A ladder is climbed against a boss already beaten once, which the dev
  // fixture has not done - so, for this check only, it has (and un-does it).
  if (b1 && !wasBeaten) {
    await apiPost('/api/v1/dev/boss-ascension/restore', { Region: 1, Step: startedAt, BossDefeated: true });
    await page.waitForTimeout(2500);
    b1 = (await apiGet('/api/v1/boss-ascension'))?.Bosses?.find((b) => b.Region === 1);
    // Away and back, so the Combat screen's ladder query is asked again.
    await go('Character');
  }

  await go('Combat');
  await page.waitForTimeout(1500);
  // Task 98: the ladder sits behind the boss's one-line fold.
  {
    const fold = page.getByTestId('boss-extras-1');
    if ((await fold.count()) > 0 && (await fold.getAttribute('aria-expanded')) !== 'true') {
      await fold.click();
      await page.waitForTimeout(300);
    }
    if (b1?.BossDefeated) {
      const ch = page.getByTestId('boss-challenges-1');
      const chText = (await ch.count()) > 0 ? ((await ch.textContent()) ?? '').replace(/\s+/g, ' ').trim() : '';
      record("the boss's fold opens its challenges", /Starved/.test(chText) && /Young blood/.test(chText) && /Swift/.test(chText), chText.slice(0, 120));
    }
  }
  const ladder = page.getByTestId('boss-ascension-1');
  const ladderText = (await ladder.count()) > 0 ? ((await ladder.textContent()) ?? '').replace(/\s+/g, ' ').trim() : '';
  if (b1?.BossDefeated) record('Combat draws region 1\'s ladder', /Boss Ascension/.test(ladderText), ladderText.slice(0, 100));

  if (b1?.BossDefeated && b1.HighestStep < 10) {
    const before = b1.HighestStep;
    const next = b1.NextStep;
    const stopButton = page.getByRole('button', { name: 'Stand down', exact: true });
    const wasFighting = (await stopButton.count()) > 0;
    const start = page.getByTestId('ascension-start-1');

    // A locked step can be looked at but not started.
    if (next < 10) {
      await page.getByTestId(`ascension-step-1-${next + 1}`).click();
      record('a locked step cannot be started', await start.isDisabled(), `step ${next + 1} looked at, Start disabled`);
    }

    // The steps are 44px buttons, not a native <select> (Android's dialog).
    const stepBox = await page.getByTestId(`ascension-step-1-${next}`).boundingBox();
    record('the ladder steps are buttons at least 44px square', Boolean(stepBox) && stepBox.width >= 43.5 && stepBox.height >= 43.5, stepBox ? `${Math.round(stepBox.width)}x${Math.round(stepBox.height)}` : 'no box');

    await page.getByTestId(`ascension-step-1-${next}`).click();
    const startLabel = ((await start.textContent()) ?? '').trim();
    await start.click();
    let outcome = 'neither';
    for (let i = 0; i < 24 && outcome === 'neither'; i++) {
      await page.waitForTimeout(500);
      if (/is running/.test((await start.textContent().catch(() => '')) ?? '')) outcome = 'running';
      else {
        const now = (await apiGet('/api/v1/boss-ascension'))?.Bosses?.find((b) => b.Region === 1);
        if (now && now.HighestStep > before) outcome = 'cleared';
      }
    }
    record('starting the next step runs it, or clears it', outcome !== 'neither', `"${startLabel}" -> ${outcome}`);

    // Put everything back: the fight as it was, the ladder as it was.
    if (await stopButton.count()) await stopButton.first().click();
    await page.getByTestId('combat-continue').waitFor({ timeout: 8000 }).catch(() => {});
    if (wasFighting) {
      const cont = page.getByTestId('combat-continue');
      if ((await cont.count()) > 0) await cont.first().click();
    }
    const restored = await apiPost('/api/v1/dev/boss-ascension/restore', { Region: 1, Step: startedAt, BossDefeated: wasBeaten });
    await page.waitForTimeout(2500);
    const after = (await apiGet('/api/v1/boss-ascension'))?.Bosses?.find((b) => b.Region === 1);
    record(
      'the ladder and the boss mark are back where they started',
      restored !== null && after?.HighestStep === startedAt && after?.BossDefeated === wasBeaten,
      `step ${after?.HighestStep} (was ${startedAt}), boss beaten ${after?.BossDefeated} (was ${wasBeaten})`,
    );
  } else {
    // Task 98: an unbeaten boss shows no ladder at all (no placeholder).
    record(
      'the ladder waits for a first clear (boss not beaten, or the ladder is complete)',
      (!b1?.BossDefeated && ladderText === '') || /cleared/.test(ladderText),
      ladderText.slice(0, 100) || 'no ladder drawn',
    );
  }
}

// --- task 84: the Great Works -------------------------------------------------
// The output side, end to end: the server lists five monuments of five stages,
// the Village panel's Deposit button spends the region's materials and the
// monument's progress MOVES, and a built stage puts a landmark on the Map.
// ROUND TRIP: the check grants itself 2,000 birch logs (a signed StockDelta on
// the dev-tools route), deposits, and puts monument 1 and the stock back exactly
// - so it never permanently spends the fixture and passes on every run.
{
  const view = await apiGet('/api/v1/great-works');
  const works = view?.Works ?? [];
  record(
    'the server lists five Great Works of five stages, with the two ceilings',
    works.length === 5 && works.every((w) => w.Stages.length === 5 && w.BonusPerStage && w.LogItem && w.OreItem)
      && view.MaxYieldPct > 0 && view.MaxOfflineMinutes > 0,
    works.map((w) => `${w.Region}:${w.Stage}/5`).join(' '),
  );

  const w1 = works.find((w) => w.Region === 1);
  const origStage = w1?.Stage ?? 0;
  const origProgress = w1?.Progress ?? 0;
  if (w1 && origStage === 0) {
    const granted = await apiPost('/api/v1/dev/great-works/restore', { Region: 1, Stage: 0, Progress: origProgress, Material: 0, StockDelta: 2000 });
    await page.waitForTimeout(2500);
    const held = (await apiGet('/api/v1/great-works'))?.Works?.find((w) => w.Region === 1);

    await go('Village');
    await page.waitForTimeout(1200);
    const panel = page.getByTestId('great-work-1');
    record('the Village draws the Great Works panel', (await panel.count()) > 0, `${(await page.getByTestId('great-works').count())} panel(s)`);

    // Modul: DEPOSIT OPENS A SHEET (task 103). Each monument is one compact
    // row now, and its two deposit buttons, the ladder and the completion
    // reward live in a sheet the row's Deposit opens. The sheet's backdrop
    // covers the nav, so it is closed before this step navigates anywhere.
    const gwSheet = page.getByTestId('great-work-sheet');
    const closeGwSheet = async () => {
      if ((await gwSheet.count()) > 0) {
        await gwSheet.getByRole('button', { name: 'Close', exact: true }).click();
        await page.waitForTimeout(200);
      }
    };
    await page.getByTestId('great-work-open-1').click().catch(() => {});
    await gwSheet.waitFor({ timeout: 3000 }).catch(() => {});

    const deposit = page.getByTestId('great-work-deposit-1-log');
    const enabled = (await deposit.count()) > 0 && (await deposit.isEnabled());
    record('Deposit is enabled while the region\'s log is held', enabled, `held ${held?.HeldLog}`);
    const box = (await deposit.count()) > 0 ? await deposit.boundingBox() : null;
    record('the Deposit button is at least 44px tall', Boolean(box) && box.height >= 43.5, box ? `${Math.round(box.width)}x${Math.round(box.height)}` : 'no box');

    let moved = null;
    if (enabled) {
      await deposit.click();
      for (let i = 0; i < 16 && !moved; i++) {
        await page.waitForTimeout(500);
        const now = (await apiGet('/api/v1/great-works'))?.Works?.find((w) => w.Region === 1);
        if (now && (now.Progress > origProgress || now.Stage > 0)) moved = now;
      }
    }
    const spent = held && moved ? held.HeldLog - moved.HeldLog : 0;
    record(
      'depositing moved the monument and took exactly that much material',
      Boolean(moved) && spent > 0 && (moved.Stage > 0 || moved.Progress - origProgress === spent),
      moved ? `progress ${moved.Progress} (was ${origProgress}), stage ${moved.Stage}, spent ${spent}` : 'nothing moved',
    );
    if (moved) {
      const text = ((await panel.textContent()) ?? '').replace(/\s+/g, ' ');
      record('the panel shows the new progress', /\/\s*50\D?000/.test(text) || moved.Stage > 0, text.slice(0, 140));
    }
    await closeGwSheet();

    // Put everything back: the monument as found, the stock as found (what was
    // spent comes back, the 2,000 granted goes away).
    await apiPost('/api/v1/dev/great-works/restore', { Region: 1, Stage: origStage, Progress: origProgress, Material: 0, StockDelta: spent - 2000 });
    await page.waitForTimeout(2500);
    const after = (await apiGet('/api/v1/great-works'))?.Works?.find((w) => w.Region === 1);
    record(
      'the monument and its stock are back where they started',
      after?.Stage === origStage && after?.Progress === origProgress && after?.HeldLog === (held?.HeldLog ?? 0) - 2000,
      `stage ${after?.Stage}, progress ${after?.Progress}, held ${after?.HeldLog} (was ${(held?.HeldLog ?? 0) - 2000})`,
    );

    // A built stage is a landmark on the Map; an unbuilt monument is not there.
    await go('Map');
    await page.waitForTimeout(800);
    const before = await page.getByTestId('hub-monument-1').count();
    await apiPost('/api/v1/dev/great-works/restore', { Region: 1, Stage: 2, Progress: 0, Material: 0, StockDelta: 0 });
    await page.waitForTimeout(500);
    await go('Village');
    await go('Map');
    await page.waitForTimeout(1500);
    const marker = page.getByTestId('hub-monument-1');
    record(
      'a built stage puts the monument on the Map',
      before === 0 && (await marker.count()) === 1 && (await marker.getAttribute('data-stage')) === '2',
      `before ${before}, after ${await marker.count()} at stage ${await marker.getAttribute('data-stage').catch(() => null)}`,
    );
    await apiPost('/api/v1/dev/great-works/restore', { Region: 1, Stage: origStage, Progress: origProgress, Material: 0, StockDelta: 0 });
    await page.waitForTimeout(500);

    // Completion (task 84): The Ebon Crown complete is one Hall of Ancestors
    // slot ABOVE the diamond ceiling, and the panel names what completion pays.
    // Round-trips through the dev route; the frame itself is granted only by a
    // real fifth-stage deposit (GreatWorksTests), so the fixture gains nothing.
    const crown = (await apiGet('/api/v1/great-works'))?.Works?.find((w) => w.Region === 5);
    const hallBefore = await apiGet('/api/v1/ancestors/hall');
    await apiPost('/api/v1/dev/great-works/restore', { Region: 5, Stage: 5, Progress: 0, Material: 0, StockDelta: 0 });
    const hallAfter = await apiGet('/api/v1/ancestors/hall');
    record(
      'a completed Ebon Crown adds one Hall slot above the diamond ceiling',
      Boolean(hallBefore && hallAfter) && hallAfter.Cap === hallBefore.Cap + 1 && hallAfter.MaxCap === hallBefore.MaxCap + 1
        && hallAfter.GreatWorkSlots === 1,
      `cap ${hallBefore?.Cap} -> ${hallAfter?.Cap}, ceiling ${hallBefore?.MaxCap} -> ${hallAfter?.MaxCap}`,
    );
    await go('Map');
    await go('Village');
    await page.getByTestId('great-work-open-5').click().catch(() => {});
    const completion = await page.getByTestId('great-work-completion-5').innerText().catch(() => '');
    {
      const sheet = page.getByTestId('great-work-sheet');
      if ((await sheet.count()) > 0) await sheet.getByRole('button', { name: 'Close', exact: true }).click();
    }
    record('the panel names what completing a monument pays', /Frame and \+1 Hall of Ancestors slot/.test(completion), completion);
    await apiPost('/api/v1/dev/great-works/restore', { Region: 5, Stage: crown?.Stage ?? 0, Progress: crown?.Progress ?? 0, Material: 0, StockDelta: 0 });
    const hallBack = await apiGet('/api/v1/ancestors/hall');
    record('the Hall is back to its own ceiling', hallBack?.Cap === hallBefore?.Cap, `cap ${hallBack?.Cap} (was ${hallBefore?.Cap})`);
  } else {
    record('Great Works round trip skipped (monument 1 already started on this account)', true, `stage ${origStage}`);
  }
}

// --- the paper doll ----------------------------------------------------------
// Equipment used to be a LIST of seven rows, each with its own dropdown and
// Equip button, in the same panel that handed out jobs. Dressing a character
// and telling them what to do are different acts and looked identical.
await go('Character');
{
  // The slots a person cannot stand in are listed on the Work tab (task 97).
  await characterTab('work');
  const text = await page.evaluate(() => document.body.innerText);
  // The dev fixture is Town Hall 5, so all three slots are open and there is
  // nothing to lock. Asserted as a conditional rather than dropped: a locked
  // slot must NEVER render as a bare row again, which is what it did before -
  // visible, unusable and silent about why.
  const lockedRows = await page.locator('.rostercard.locked').count();
  record(
    'a locked character slot names what unlocks it',
    lockedRows === 0 || /Town Hall \d/.test(text),
    lockedRows === 0 ? 'all slots open at Town Hall 5' : `${lockedRows} locked`,
  );

  // Modul: ATTRIBUTES ARE SPENT, NOT DEALT.
  //
  // Levelling used to allocate STR/DEX/CON/LCK by race with no say from the
  // player - and OfflineSimulationEngine.ApplyCombatXp never called that at
  // all, so a level-86 live account was still holding a brand-new
  // registration's 50/50/50/25 and no screen in the game would have shown it.
  //
  // This asserts the world CHANGED, which is the whole point of this script: a
  // panel that renders four numbers and a button proves nothing if the button
  // does not move them.
  {
    // Modul: THIS CHECK SPENDS FIXTURE STATE, one point a run, and attribute
    // points are deliberately permanent - there is no unspend command to
    // restore them with. The seeder grants
    // DevFixtureSeeder.FixtureUnspentAttributePoints (25), so this survives
    // twenty-five runs and then needs `--seed-dev`, which is idempotent.
    // Written as a conditional rather than a hard failure so a drained pool
    // reports itself instead of looking like a broken feature - the same shape
    // the locked-slot check above uses.
    await characterTab('attributes');
    const before = await page.evaluate(() => document.body.innerText);
    const pointsBefore = Number(/(\d+)\s+points? to spend/.exec(before)?.[1] ?? 0);
    record(
      'the character screen offers attribute points',
      pointsBefore > 0 || /No points to spend/.test(before),
      pointsBefore > 0 ? `${pointsBefore} to spend` : 'pool spent - re-seed with --seed-dev',
    );

    if (pointsBefore > 0) {

    const strBefore = await page
      .locator('.attrpanel .card', { hasText: 'Might' })
      .locator('.value')
      .innerText();

    await page.locator('.attrpanel .card', { hasText: 'Might' }).locator('button', { hasText: '+1' }).first().click();

    // Modul: WAIT FOR THE CHANGE, not for a clock. The pool moves when the
    // next StateUpdate lands, and a fixed sleep raced it - this check flaked
    // on its first run for exactly that reason. Every other timing-sensitive
    // assertion in this file that was written as a sleep has eventually done
    // the same.
    await page
      .waitForFunction(
        (expected) => new RegExp(`\b${expected}\b\s+points? to spend`).test(document.body.innerText),
        pointsBefore - 1,
        { timeout: 15000 },
      )
      .catch(() => {});

    const after = await page.evaluate(() => document.body.innerText);
    const pointsAfter = Number(/(\d+)\s+points? to spend/.exec(after)?.[1] ?? 0);
    const strAfter = await page
      .locator('.attrpanel .card', { hasText: 'Might' })
      .locator('.value')
      .innerText();

    const spent = pointsAfter === pointsBefore - 1;
    record('spending a point takes it out of the pool', spent, `${pointsBefore} -> ${pointsAfter}`);

    const strengthRose = parseInt(strAfter.replace(/[^0-9]/g, ''), 10) > parseInt(strBefore.replace(/[^0-9]/g, ''), 10);
    record('and puts it into the attribute', strengthRose, `Might ${strBefore.trim()} -> ${strAfter.trim()}`);

    // Modul: the milestone track is the half that makes this a system rather
    // than four spinners - it has to be on screen, not just in the registry.
    const trackText = await page.evaluate(() => document.body.innerText);
    record(
      'each attribute shows its milestone track',
      /next at \d+:/.test(trackText) || /track complete/.test(trackText),
    );

    // Modul: AND THAT IT SURVIVES A RELOAD, which is the half that was broken.
    //
    // Everything above passed while the placement was being thrown away: the
    // command reached the tick, the payload moved, the packet carried it and
    // the panel drew it - all in memory. TrackState returned the moment Redis
    // took the session frame, so the periodic checkpoint never reached
    // FlushState, and the frame carries neither the four attributes nor the
    // unspent pool. Reported as "I distribute my points, press F5, and they
    // are all back". An in-session assertion cannot see that at all.
    //
    // Might only ever goes UP (a respec is the one exception, and this script
    // never issues one), so this compares against the post-spend value rather
    // than an exact pool figure the fixture's own levelling could move.
    if (strengthRose) {
      await page.reload({ waitUntil: 'networkidle' });
      await page.waitForTimeout(1500);
      await dismissOfflineSummary(3000);
      await go('Character');
      await characterTab('attributes');

      const strReloaded = await page
        .locator('.attrpanel .card', { hasText: 'Might' })
        .locator('.value')
        .innerText()
        .catch(() => '');
      const reloadedNum = parseInt(strReloaded.replace(/[^0-9]/g, ''), 10);
      const afterNum = parseInt(strAfter.replace(/[^0-9]/g, ''), 10);
      record(
        'a placed attribute point survives a reload',
        Number.isFinite(reloadedNum) && reloadedNum >= afterNum,
        `Might ${strAfter.trim()} -> ${strReloaded.trim() || 'not rendered'} after F5`,
      );
    }

    }

    // The attributes had never been explained anywhere in the game before this
    // panel; a player asked to allocate a stat has to be told what it buys.
    // Outside the conditional above - the explanations must be there whether or
    // not there is anything left to spend.
    const panelText = await page.evaluate(() => document.body.innerText);
    record(
      'each attribute says what it buys',
      /accuracy/i.test(panelText) && /max health/i.test(panelText) && /armour penetration/i.test(panelText),
    );
    record('the four attributes are named and distinct', /Might/.test(panelText) && /Finesse/.test(panelText) && /Vigour/.test(panelText) && /Fortune/.test(panelText));
  }

  // A gear slot is a button now; clicking one opens its picker.
  // Modul: TASK 97 - one 4-column grid of all ELEVEN slots on the Gear tab,
  // addressed by data-slot-index rather than by position in a doll layout.
  await characterTab('gear');
  const allSlots = await page.locator('.gearslot[data-slot-index]').count();
  record('the gear grid has all eleven slots', allSlots === 11, `${allSlots} slots`);

  // Modul: tools are gear now - three slots of their own, rolled with a rarity
  // and gathering affixes, where they used to be stackable materials that
  // could carry neither.
  const toolSlots = await page.locator('.gearslot.tool').count();
  record('the doll has the three tool slots', toolSlots === 3, `${toolSlots} tool slots`);

  // Modul: a WORN TOOL HAS TO SHOW. Counting the slots proved only that three
  // buttons render, and for as long as tools have existed all three rendered
  // EMPTY however many were equipped: the inventory snapshot recorded the
  // eight combat slots and never the tool ones, so an axe written to
  // EquippedAxeId came back as EquippedByCharacterSlot -1.
  //
  // Modul: EQUIPS ONE HERE rather than trusting the fixture to have done it.
  // Which character occupies a playable slot is not stable across runs - the
  // Hall of Ancestors step below FIELDS somebody - so driving the equip makes
  // this self-contained, and it is the exact act that was reported broken.
  const axeSlot = page.locator('.gearslot[data-slot-index="8"]').first();
  await axeSlot.click();
  await page.waitForTimeout(500);

  const toolPick = page.locator('[data-testid="equip-picker"] button', { hasText: /^Wear$/ }).first();
  const pickable = (await toolPick.count()) > 0;

  if (pickable) {
    await toolPick.click();
    await page
      .waitForFunction(() => document.querySelector('.gearslot[data-slot-index="8"]')?.classList.contains('filled'), null, { timeout: 8000 })
      .catch(() => {});
  }
  // Close the picker so it does not sit over the slots being read.
  await page.getByRole('button', { name: 'Close', exact: true }).first().click().catch(() => {});
  await page.waitForTimeout(400);

  const axeFilled = await axeSlot.evaluate((el) => el.classList.contains('filled'));
  const axeText = (await axeSlot.innerText()).replace(/\s+/g, ' ').trim();
  record(
    'equipping a tool fills its slot',
    axeFilled,
    axeFilled ? axeText : pickable ? 'equipped, but the slot still rendered empty' : 'no tool available to equip',
  );

  const gearSlot = page.locator('.gearslot[data-slot-index="0"]').first();
  const hasDoll = (await gearSlot.count()) > 0;
  record('the character has a paper doll with clickable slots', hasDoll);

  const wornId = async () => (await gearSlot.getAttribute('data-item-id').catch(() => '')) ?? '';
  // mode: 'changed' (a piece other than `value` is worn), 'empty', 'filled'.
  const waitWorn = (mode, value) =>
    page
      .waitForFunction(
        ([m, v]) => {
          const id = document.querySelector('.gearslot[data-slot-index="0"]')?.dataset.itemId ?? '';
          if (m === 'empty') return id === '';
          if (m === 'filled') return id !== '';
          return id !== '' && id !== v;
        },
        [mode, value],
        { timeout: 10000 },
      )
      .then(() => true)
      .catch(() => false);

  if (hasDoll) {
    await gearSlot.click();
    await page.waitForTimeout(500);
    const opened = await page.evaluate(() => document.querySelector('[data-testid="equip-picker"]') !== null);
    record('clicking a slot opens its item picker', opened);

    const wear = page.locator('[data-testid="equip-picker"] button[data-piece-id]', { hasText: /^Wear$/ });
    if ((await wear.count()) > 0) {
      await dismissToasts();
      // Modul: BY INSTANCE ID, not by the slot's text. The picker never lists
      // a worn piece (equipped ids are filtered out), but two pieces can share
      // a name - so "the text changed" read a working swap between two Hunter
      // Swords as a failure. data-item-id is the instance actually worn.
      const before = await wornId();
      await wear.first().click();
      const changed = await waitWorn('changed', before);
      const after = await wornId();
      const msgs = await toasts();
      record(
        'wearing an item from the doll dresses the character',
        changed || msgs.length > 0,
        msgs.join(' | ') || `weapon instance ${before || 'none'} -> ${after || 'none'}`,
      );

      // Modul: AND TAKES IT OFF, then puts it back - a round trip, so the
      // fixture fights the later steps with a weapon (a check that leaves
      // state behind passes once and fails for ever, root CLAUDE.md).
      if (after) {
        if ((await page.locator('[data-testid="equip-picker"]').count()) === 0) {
          await gearSlot.click();
          await page.waitForTimeout(400);
        }
        await page.locator('[data-testid="take-off"]').first().click().catch(() => {});
        const emptied = await waitWorn('empty', '');
        record('taking the weapon off from the gear grid empties the slot', emptied, emptied ? `instance ${after} taken off` : `still ${await wornId()}`);

        if ((await page.locator('[data-testid="equip-picker"]').count()) === 0) {
          await gearSlot.click();
          await page.waitForTimeout(400);
        }
        const same = page.locator(`[data-testid="equip-picker"] button[data-piece-id="${after}"]`);
        await ((await same.count()) > 0 ? same : wear).first().click().catch(() => {});
        let restored = await waitWorn('filled', '');
        // Modul: ONE RETRY, because a miss here is not a local failure. It left
        // the fixture unarmed once (2026-10-01), the guided "wear a weapon"
        // overlay came up over the whole shell, and every later step timed
        // out behind it - the run died rather than reporting this one line.
        if (!restored) {
          if ((await page.locator('[data-testid="equip-picker"]').count()) === 0) {
            await gearSlot.click().catch(() => {});
            await page.waitForTimeout(400);
          }
          await ((await same.count()) > 0 ? same : wear).first().click().catch(() => {});
          restored = await waitWorn('filled', '');
        }
        record('and wearing it again fills it', restored, `weapon instance now ${(await wornId()) || 'none'}`);
      }
    }
  }
}

// --- inventory / equip -------------------------------------------------------
await go('Chest');
{
  // Modul: EQUIP OR UNEQUIP. This looked only for "Equip" and called its
  // absence a failure - but the dev fixture arrives wearing all seven pieces,
  // so every row correctly offers "Unequip" and the check reported a fully
  // dressed character as an empty chest. What the step is actually asserting is
  // that the chest lists gear with a working action on it; which direction that
  // action goes is the fixture's business.
  const equipBtn = page.getByRole('button', { name: 'Equip', exact: true });
  const unequipBtn = page.getByRole('button', { name: 'Unequip', exact: true });
  const toggle = (await equipBtn.count()) > 0 ? equipBtn : unequipBtn;
  const wasEquipping = (await equipBtn.count()) > 0;

  if ((await toggle.count()) > 0) {
    await dismissToasts();
    const before = await page.evaluate(() => document.body.innerText);
    await toggle.first().click();
    await page.waitForTimeout(2200);
    const text = await page.evaluate(() => document.body.innerText);
    const msgs = await toasts();
    // Either the item changed hands, or the server said why not. Both are real
    // answers; nothing happening at all is not.
    record(
      wasEquipping ? 'equipping reports an outcome' : 'unequipping reports an outcome',
      text !== before || msgs.length > 0,
      msgs.join(' | '),
    );
    await dismissToasts();
  } else {
    record('chest lists something to act on', false, 'no Equip or Unequip button found');
  }

  // Modul: the reroll entry point. It lives in the Forge, and a player looking
  // for it did not find it because the thing being rerolled is an item and
  // items are here. Asserted because a link nobody can see is the bug that was
  // being fixed.
  // Task 81: it sits in the row's "More" menu now.
  let rerollOffered = false;
  const moreBtn = page.getByRole('button', { name: 'More', exact: true }).first();
  if ((await moreBtn.count()) > 0) {
    await moreBtn.click();
    rerollOffered = (await page.getByRole('menuitem', { name: 'Reroll in Forge' }).count()) > 0;
    await page.keyboard.press('Escape');
  }
  record('the chest offers a route to the reroll', rerollOffered, 'every equipment row menu links to the Forge');

  // --- task 99: rows that say what the piece is ------------------------------
  //
  // Inspect is the FIRST menu item and opens the Forge's Affixes panel for
  // that piece - addressed by id, so a refetch cannot swap the piece under the
  // check. Closed again afterwards so the screen is as it was found.
  {
    const firstRow = page.locator('.row[data-equipment-id]').first();
    if ((await firstRow.count()) > 0) {
      const id = await firstRow.getAttribute('data-equipment-id');
      await firstRow.getByRole('button', { name: 'More', exact: true }).click();
      const firstItem = ((await page.getByRole('menuitem').first().textContent()) ?? '').trim();
      await page.getByRole('menuitem', { name: 'Inspect', exact: true }).click();
      const pane = page.locator(`[data-inspected-id="${id}"]`);
      await pane.waitFor({ timeout: 5000 }).catch(() => {});
      record(
        'Inspect is the first item in the chest menu and opens that piece',
        firstItem === 'Inspect' && (await pane.count()) > 0,
        `first item "${firstItem}", pane ${await pane.count()}`,
      );
      await pane.getByRole('button', { name: 'Close', exact: true }).click().catch(() => {});
    } else {
      record('Inspect is the first item in the chest menu and opens that piece', false, 'no equipment row');
    }

    // Worn pieces are one group on top: in DOM order no worn row may follow a
    // loose one. The loose half is windowed, so only what is rendered is read -
    // which is the top of the list, where the boundary is.
    const order = await page.evaluate(() =>
      [...document.querySelectorAll('.row[data-equipment-id]')].map((r) => r.querySelector('.chip.worn') !== null),
    );
    const firstLoose = order.indexOf(false);
    record(
      'worn pieces are grouped above the loose ones',
      firstLoose === -1 || !order.slice(firstLoose).includes(true),
      `${order.filter(Boolean).length} worn rows, first loose at ${firstLoose}`,
    );

    // Gold is not a material. The server answers Sell all / Bin on "gold" by
    // deleting it for 0 gold (it has no items.json price), so the row must not
    // exist at all. Read with the All tab and no rarity floor, where it was.
    await page.locator('.filters button').first().click();
    await page.waitForTimeout(300);
    const goldListed = await page.evaluate(() =>
      [...document.querySelectorAll('.materials li .name')].some((n) => n.textContent.trim() === 'Gold'),
    );
    const inv = await apiGet('/api/v1/player/inventory');
    const holdsGold = (inv?.Stacks ?? []).some((s) => s.ItemId === 'gold' && s.Quantity > 0);
    record('the chest lists no Gold row among the materials', !goldListed, `gold in snapshot: ${holdsGold}`);
  }

  // Modul: THE CHEST'S ONLY DRAIN.
  //
  // Equipment lands on 15% of kills and nothing removed it but the per-item
  // Sell button - so the table grew for as long as the account was played. One
  // live account reached 17,836 rows, at which point this screen was too slow
  // to open and the cleanup tool and the mess were the same screen.
  //
  // DELIBERATELY NOT PRESSED. A sweep sells thousands of items and there is no
  // way to put them back, so running it here would be a check that passes once
  // and leaves the fixture stripped for every run after - the exact trap
  // CLAUDE.md records. What is asserted is that the control exists, is
  // reachable, and quotes a count that AGREES WITH THE API about what it would
  // take; the destructive half is covered by the server's own path.
  const sweep = page.locator('section.sweep');
  record('the chest offers a bulk clear-out', (await sweep.count()) > 0);

  if ((await sweep.count()) > 0) {
    await sweep.locator('.sweeptoggle').click();
    await page.waitForTimeout(400);

    const settings = await apiGet('/api/v1/chest/settings');
    record(
      'the server publishes its own sweep ceiling',
      settings !== null && settings.MaxSweepableQualityTier >= 1,
      settings ? `up to tier ${settings.MaxSweepableQualityTier}` : 'no settings',
    );

    // The count in the panel has to be the count that disappears. A button that
    // says "0 pieces" over a chest full of junk is worse than no button - it
    // reads as "there is nothing to clean up".
    //
    // Modul: WHAT IS ASSERTED HERE IS DELIBERATELY NOT "panel === API", and the
    // reason took three failing runs to pin down.
    //
    // The panel renders from TanStack's cache, staleTime 30 seconds, and
    // nothing on this screen triggers a refetch. Meanwhile the fixture has been
    // fighting for minutes and equipment lands on 15% of kills. So the panel is
    // legitimately behind a live API reading, and it DRIFTS FURTHER the longer
    // you look: measured at -1, then -8 after forty seconds of polling for
    // agreement. Remounting does not help - a query inside its staleTime serves
    // the cache without refetching, which is the entire point of the staleTime.
    // An exact live comparison is not assertable from outside the cache, and
    // every attempt at one is a flaky check, which this project has learned is
    // worse than no check.
    //
    // These three ARE exact, and hold no matter how stale the panel is, because
    // drops only ever ADD to the chest:
    //
    //   - it never exceeds the live count. Over-counting is the dangerous
    //     direction: a panel that forgot to exclude worn gear would sit ABOVE
    //     the API and be caught here.
    //   - it is not zero while the chest demonstrably holds junk. "Nothing to
    //     clean up" over a full chest is the failure this whole panel exists to
    //     prevent.
    //   - raising the rarity never lowers it. A dead or mis-wired dropdown -
    //     the count not moving, or moving the wrong way - is caught by this and
    //     by nothing else.
    const tier = Number(await sweep.locator('select').inputValue());
    const sweepable = (snapshot, upTo) =>
      (snapshot?.Equipment ?? []).filter((e) => e.QualityTier <= upTo && !e.IsEquipped).length;

    const readPanel = async () => {
      const text = await sweep.innerText();
      const digits = (text.match(/([\d\s, ]+)\s+piece/) ?? [])[1];
      return Number((digits ?? '').replace(/\D/g, ''));
    };

    const shown = await readPanel();
    const live = sweepable(await apiGet('/api/v1/player/inventory'), tier);

    record(
      'the sweep never offers to take more than the player owns',
      shown <= live,
      `panel says ${shown}, live count ${live} at tier ${tier}`,
    );

    record(
      'the sweep sees the junk that is actually there',
      live === 0 || shown > 0,
      `panel says ${shown} with ${live} sweepable`,
    );

    // Raising the floor can only widen the band, so the count must not fall.
    // Compared against the panel's OWN earlier reading, not against the API, so
    // the cache cannot make this flaky either.
    await sweep.locator('select').selectOption(String(settings.MaxSweepableQualityTier));
    await page.waitForTimeout(400);
    const widened = await readPanel();

    record(
      'raising the rarity floor widens what the sweep would take',
      widened >= shown,
      `tier ${tier} -> ${settings.MaxSweepableQualityTier}: ${shown} -> ${widened}`,
    );

    await sweep.locator('select').selectOption(String(tier));

    // Both halves confirm before doing anything, because both are irreversible
    // across thousands of items. A one-click bulk destroy is the finding.
    await page.getByRole('button', { name: 'Bin them all' }).click();
    await page.waitForTimeout(300);
    const confirming = await sweep.innerText();
    record(
      'a bulk bin asks before destroying anything',
      /Permanently bin/i.test(confirming) && (await page.getByRole('button', { name: 'Cancel' }).count()) > 0,
    );
    await page.getByRole('button', { name: 'Cancel' }).click();
  }
}

// --- the gold ledger (task 79) ------------------------------------------------
//
// The stack fusion above spends gold (and the Delve/Deep steps do too). The
// ledger must have recorded it by category, and Progress must draw the split.
{
  const ledger = await apiGet('/api/v1/player/gold-ledger');
  const categories = (ledger?.Categories ?? []).map((c) => c.Category);
  record(
    'the gold ledger records spending by category',
    ledger !== null && ledger.LifetimeSpent > 0 && categories.includes('Fusion'),
    `${ledger?.LifetimeSpent ?? '?'} spent; ${categories.join(', ') || 'no categories'}`,
  );
  // Phase 2: gold IN by source. Kill gold is tallied on the payload and
  // written by the next checkpoint (periodic, a command's, a reload's), and
  // the combat step ran long before this. No single source is required:
  // which ones exist depends on the day.
  const income = ledger?.Income ?? [];
  record(
    'the gold ledger records income by source',
    ledger !== null && ledger.IncomeRecordedSince !== null && income.some((c) => c.SinceRecorded > 0),
    `${income.map((c) => `${c.Category} ${c.SinceRecorded}`).join(', ') || 'no sources'}; since ${ledger?.IncomeRecordedSince ?? '?'}`,
  );
  await go('Progress');
  await page.locator('[data-progress-tab="stats"]').first().click().catch(() => {});
  await page.locator('[data-gold-ledger]').first().waitFor({ timeout: 10000 }).catch(() => {});
  record('Progress shows where the gold went', (await page.locator('[data-gold-ledger] li').count()) > 0);
  if (income.some((c) => c.Last30Days > 0)) {
    record('Progress shows where the gold came from', (await page.locator('[data-gold-income] li').count()) > 0);
  }
}

// --- auto-salvage: the drain at the source -----------------------------------
//
// Modul: the bulk sweep clears a backlog; this stops one forming. A drop at or
// below the chosen rarity is sold on the way in and never becomes a row.
//
// Round-trips deliberately - reads the current value, changes it, reads it
// back, and puts it back the way it was. A check that leaves a setting altered
// is a check that changes the next run's fixture, and this particular setting
// silently destroys loot.
{
  const before = await apiGet('/api/v1/chest/settings');
  record(
    'the auto-salvage setting is readable',
    before !== null && typeof before.AutoSalvageBelowTier === 'number',
  );

  if (before !== null) {
    const target = before.AutoSalvageBelowTier === 2 ? 1 : 2;

    const saved = await apiPost('/api/v1/chest/settings', { AutoSalvageBelowTier: target });
    record(
      'the auto-salvage floor can be changed',
      saved !== null && saved.AutoSalvageBelowTier === target,
      `set to ${target}, server says ${saved?.AutoSalvageBelowTier}`,
    );

    const readBack = await apiGet('/api/v1/chest/settings');
    record(
      'the new floor is what the server reads back',
      readBack !== null && readBack.AutoSalvageBelowTier === target,
    );

    // Above the ceiling is REFUSED, not clamped. Clamping would hand the player
    // a destructive setting they did not choose.
    const tooHigh = await apiPostStatus('/api/v1/chest/settings', {
      AutoSalvageBelowTier: before.MaxSweepableQualityTier + 1,
    });
    record(
      'a floor above the ceiling is refused, not clamped',
      tooHigh === 400,
      `HTTP ${tooHigh}`,
    );

    // Task 81: the per-region rules. Posting only the global floor (above)
    // must leave them alone, and posting them must be read back as posted.
    const beforeRegions = before.AutoSalvageRegionTiers ?? [0, 0, 0, 0, 0];
    record(
      'a floor-only save leaves the region rules alone',
      JSON.stringify(readBack?.AutoSalvageRegionTiers) === JSON.stringify(beforeRegions),
      `${JSON.stringify(beforeRegions)} -> ${JSON.stringify(readBack?.AutoSalvageRegionTiers)}`,
    );
    const regionTarget = beforeRegions.map((t, i) => (i === 0 ? (t === 3 ? 4 : 3) : t));
    const regionSaved = await apiPost('/api/v1/chest/settings', {
      AutoSalvageBelowTier: target,
      AutoSalvageRegionTiers: regionTarget,
    });
    record(
      'a region auto-sell rule can be saved',
      JSON.stringify(regionSaved?.AutoSalvageRegionTiers) === JSON.stringify(regionTarget),
      `sent ${JSON.stringify(regionTarget)}, server says ${JSON.stringify(regionSaved?.AutoSalvageRegionTiers)}`,
    );
    const badRegions = await apiPostStatus('/api/v1/chest/settings', {
      AutoSalvageRegionTiers: [0, 0, before.MaxSweepableQualityTier + 1, 0, 0],
    });
    record('a region rule above the ceiling is refused', badRegions === 400, `HTTP ${badRegions}`);

    // Put it back. See above - this is the restore half of the round trip.
    await apiPost('/api/v1/chest/settings', {
      AutoSalvageBelowTier: before.AutoSalvageBelowTier,
      AutoSalvageRegionTiers: beforeRegions,
    });
    const restored = await apiGet('/api/v1/chest/settings');
    record(
      'the auto-salvage check restores what it changed',
      restored !== null &&
        restored.AutoSalvageBelowTier === before.AutoSalvageBelowTier &&
        JSON.stringify(restored.AutoSalvageRegionTiers) === JSON.stringify(beforeRegions),
      `back to ${restored?.AutoSalvageBelowTier} / ${JSON.stringify(restored?.AutoSalvageRegionTiers)}`,
    );
  }
}

// --- inheritance: the only thing a season leaves behind ----------------------
//
// Modul: this is the one purchase in the game whose whole point is that it
// SURVIVES. A screen that renders six stats and buys none of them is
// indistinguishable from a working one until a rollover three months later
// proves otherwise, so the check spends real diamonds and reads the level back.
//
// The fixture carries 5,000 diamonds and the first level costs 40, so the
// purchase is affordable by construction - if the button is disabled here, that
// is the finding, not a reason to skip.
await go('Inheritance');
{
  const text = await page.evaluate(() => document.body.innerText);
  record(
    'inheritance lists all six permanent bonuses',
    /Damage/.test(text) && /Health/i.test(text) && /Experience/i.test(text) &&
      /Gold/i.test(text) && /Gathering/i.test(text) && /Luck/i.test(text),
  );

  // The screen's own promise to the player. Worth asserting because it is the
  // only place the carry-over rule is stated, and a season that quietly wiped
  // one of these would still render this sentence.
  record(
    'inheritance states what a season does not touch',
    /does not touch these, your village, or the race\s+mastery/i.test(text) ||
      /does not touch/i.test(text),
  );

  const buyButtons = page.getByRole('button', { name: /^Buy \+/ });
  const buyable = await buyButtons.count();
  record('inheritance offers a purchase per uncapped stat', buyable > 0, `${buyable} buyable`);

  if (buyable > 0) {
    await dismissToasts();
    // The card being BOUGHT is the one that has to move - its "0 / 20" bar
    // label and its "+2%". Read that card rather than the whole screen: the
    // diamond balance also appears in the header, so a body-text diff would
    // pass on a purchase that charged the player and granted nothing.
    //
    // Modul: resolved from the button rather than as `.stats li` first. Levels
    // are permanent, so a fixture that has been exercised often enough caps its
    // first stat - and then the first Buy button belongs to the SECOND card
    // while the check still read the first, which never changes. That is a
    // failure that only appears after the twentieth run and blames the wrong
    // thing when it does.
    const targetCard = page.locator('.stats li').filter({ has: page.getByRole('button', { name: /^Buy \+/ }) }).first();
    const cardText = async () => targetCard.innerText();

    const before = await cardText();
    const disabled = await buyButtons.first().isDisabled();
    record(
      'the fixture can afford the first inheritance level',
      !disabled,
      disabled ? 'button disabled with 5,000 diamonds - check the cost curve' : '40 diamonds',
    );

    await buyButtons.first().click();
    await page.waitForTimeout(2500);
    const after = await cardText();
    const msgs = await toasts();

    record(
      'buying an inheritance level raises it',
      after !== before,
      after.replace(/\s+/g, ' ').slice(0, 80),
    );
    // A level bought is a level shown as a percentage. The bar alone moving
    // could be the client optimistically painting; the "+2%" comes from the
    // wire, so it is the server agreeing.
    record(
      'the purchased bonus reads back as a percentage',
      /\+\d+%/.test(after),
      msgs.join(' | '),
    );
    await dismissToasts();
  }
}

// --- the village roster: pay for one, send one away --------------------------
//
// Modul: both of these had rules, a price curve and fifteen tests, and no way
// to reach any of it. A full village STOPS the arrival clock, so before this
// existed a bad roll occupied its slot for the rest of the season and the gold
// sink the top of the economy lacks was unreachable.
await go('Village');
{
  // Modul: BUILDINGS FIRST, AND A CAPPED ROW IS TEXT (task 103). Read-only: an
  // upgrade spends the fixture's materials and raises a level nothing lowers,
  // so this asserts the shape the screen promises rather than pressing it.
  // Every row carries a "Lv n / m" pill; a capped row has no button at all;
  // the filled Upgrade appears only on a row the server's quote says is
  // affordable, and those rows sort above the rest.
  {
    const buildingRows = page.locator('[data-testid="village-building"]');
    await buildingRows.first().waitFor({ timeout: 10000 }).catch(() => {});
    const count = await buildingRows.count();
    const pills = await page.locator('[data-testid="village-building-level"]').allInnerTexts();
    record(
      'every building row shows its level against its ceiling',
      count > 0 && pills.length === count && pills.every((t) => /^Lv \d+ \/ \d+$/.test(t.trim())),
      pills.slice(0, 3).join(', '),
    );
    const capped = page.locator('[data-testid="village-building"][data-state="capped"]');
    const cappedButtons = await capped.locator('button').count();
    record('a capped building offers no button', cappedButtons === 0, `${await capped.count()} capped, ${cappedButtons} buttons`);
    const states = await buildingRows.evaluateAll((els) => els.map((el) => el.getAttribute('data-state')));
    const rank = { building: 0, ready: 1, short: 2, loading: 2, capped: 3 };
    const sorted = states.every((st, i) => i === 0 || rank[states[i - 1]] <= rank[st]);
    record('affordable upgrades sort above the rest', sorted, states.join(' '));
    // Before the gene pool in the document - which is also first on a phone,
    // where the grid is one column. (On a wide screen they sit side by side.)
    const leads = await page.evaluate(() => {
      const building = document.querySelector('[data-testid="village-building"]');
      const pool = document.querySelector('[data-testid="gene-pool"]');
      if (!building || !pool) return null;
      return Boolean(building.compareDocumentPosition(pool) & Node.DOCUMENT_POSITION_FOLLOWING);
    });
    record('the buildings lead the Village page', leads === true, leads === null ? 'building or gene pool missing' : '');
  }

  // Modul: COUNTED FROM THE TALLY, NOT THE ROWS (task 103). The gene pool
  // shows the best five by aptitude sum and collapses the married-in, so the
  // number of rows on screen is no longer the number of people. The tally's
  // data-count is the server's (every VillageNewcomers row, the same count the
  // feast refusal quotes).
  const tally = async () => Number(await page.locator('[data-testid="gene-pool-count"]').getAttribute('data-count').catch(() => '0'));

  const before = await tally();
  const feastButton = page.getByRole('button', { name: /^Throw a feast/ });
  // Modul: read the published exact price - the label compacts above 100,000 (task 74).
  const price = async () => Number(await feastButton.first().getAttribute('data-exact'));

  const offered = (await feastButton.count()) > 0;
  record('the village offers a feast with a price', offered,
    offered ? `${(await price()).toLocaleString()}g` : '');

  if (offered && !(await feastButton.first().isDisabled())) {
    const askedBefore = await price();
    // Modul: the header's gold is a separate wire from the database debit - a
    // feast pays out of a notification that tells the LIVE session what it
    // cost, and a unit test proving that notification is enqueued is not
    // proof it ever reaches this element. `data-exact` is the same
    // machine-readable attribute Money.svelte documents for exactly this.
    const headerGold = page.locator('header span.money[data-kind="gold"]');
    const readHeaderGold = async () => Number(await headerGold.getAttribute('data-exact'));
    const goldBefore = await readHeaderGold();
    await dismissToasts();
    await feastButton.first().click();
    await page.waitForTimeout(2500);

    const after = await tally();
    record('paying for a feast brings somebody in', after > before, `${before} -> ${after}`);

    const goldAfter = await readHeaderGold();
    record(
      'the header gold drops live for a feast, no reload',
      goldAfter < goldBefore,
      `${goldBefore.toLocaleString()} -> ${goldAfter.toLocaleString()}g`,
    );

    // The escalation is what stops this being a slot machine: a flat price
    // would hand a player forty rolls at a twenty in one sitting, and the
    // two-phase climb assumes the village deals about forty-five a season.
    const askedAfter = await price();
    record(
      'the next feast costs more than the last',
      askedAfter > askedBefore,
      `${askedBefore.toLocaleString()}g -> ${askedAfter.toLocaleString()}g`,
    );
  }

  // The shortlist holds five; "Show all" draws everybody who can be sent on.
  const showAll = page.locator('[data-testid="gene-pool-show-all"]');
  if ((await showAll.count()) > 0 && /^Show all/.test((await showAll.innerText()).trim())) {
    await showAll.click();
    await page.waitForTimeout(300);
  }
  const sendButtons = page.getByRole('button', { name: 'Send on', exact: true });
  const dismissable = await sendButtons.count();
  record('the village offers to send somebody on', dismissable > 0, `${dismissable} not yet married in`);

  // Modul: LEAVE THE LAST ONE FOR THE BREEDING STEP. Marrying makes a villager
  // an elder and sending one on deletes them, so both steps eat out of the one
  // pool of villagers who have not married in - and the pool only refills
  // through a feast or a re-seed. This step ran first and took the last
  // unmarried villager every time, so the breeding step below it failed for
  // want of a partner rather than for a defect. Holding one back means the two
  // steps stop competing; when only one is left, the honest thing to report is
  // that this run declined to spend it, not a fake pass and not a fake failure.
  if (dismissable > 1) {
    const held = await tally();
    // Modul: sending somebody on is permanent, so it is a two-tap
    // ConfirmButton (task 92): the first tap only arms it and relabels it
    // "Really send?". One tap here would assert a confirm guard as a defect.
    await sendButtons.first().click();
    const armed = page.getByRole('button', { name: 'Really send?', exact: true }).first();
    const armedOk = await armed.isVisible({ timeout: 2000 }).catch(() => false);
    const leftAfterOneTap = await tally();
    record('one tap only arms Send on', armedOk && leftAfterOneTap === held, armedOk ? `${held} -> ${leftAfterOneTap}` : 'no "Really send?" after the first tap');
    if (armedOk) await armed.click();
    await page.waitForTimeout(2500);
    const left = await tally();
    record('sending somebody on frees the slot', left < held, `${held} -> ${left}`);
    await dismissToasts();
  } else if (dismissable === 1) {
    record(
      'sending somebody on frees the slot',
      true,
      'held back - the last unmarried villager is the breeding step\'s partner',
    );
  }
}

// --- breeding: marrying the village in ---------------------------------------
//
// Modul: THE STANDARD PAIR. A child takes each aptitude from ONE parent, so it
// can never exceed the best number already in the pair - crossing your own
// characters converges on what you already have, and the village is the only
// thing that puts a number into a bloodline that was not in it.
//
// That makes this the one step where "the screen renders" is worth nothing:
// the gene pool was VISIBLE and INERT for a whole release, filling up every
// season with people nothing in the game could marry. So this reads the roster
// back and asserts it GREW.
//
// Declared out here, not inside the block below, so the Hall of Ancestors
// section further down (a separate block) can read which child was born and
// whether the server actually rolled it a trait - server truth, not a guess
// from the DOM alone.
let bredChildName = null;
let bredChildHadTrait = null;
let bredChildId = null;
await go('Breeding');
{
  const text = await page.evaluate(() => document.body.innerText);

  // Modul: NOT <select>s ANY MORE. Both pickers were native selects whose
  // options counted down every second, which on Android's WebView closes the
  // open dialog or drops the choice. They are PersonPicker now: a trigger
  // button, and a list of option buttons that exists only while it is open.
  // These helpers open one, read it, and close it again.
  const heroPicker = page.getByTestId('hero-picker');
  const partnerPicker = page.getByTestId('partner-picker');

  // The open list is found by `data-picker`, not inside the picker element: on
  // a phone it is portalled to <body> so no stacking context can bury it.
  const listOf = (picker) =>
    page.locator(`[role="listbox"][data-picker="${picker === heroPicker ? 'hero-picker' : 'partner-picker'}"]`);

  async function openPicker(picker) {
    if ((await listOf(picker).count()) === 0) {
      await picker.locator('.trigger').click();
      await page.waitForTimeout(150);
    }
  }

  async function closePicker(picker) {
    if ((await listOf(picker).count()) > 0) {
      await listOf(picker).locator('.close').click();
      await page.waitForTimeout(100);
    }
  }

  // Every option, with the refusal it carries. `disabled` is read off the DOM
  // rather than through isDisabled(), which is the habit the old <option>
  // reading taught this script.
  //
  // Modul: `hasTrait`, 2026-09-16. PersonPicker.svelte renders a TraitBadge
  // per option whenever `person.traits.length > 0` - so whether an option's
  // card contains a `.trait` element is a direct, RNG-free read of whether
  // THAT candidate carries a trait, without needing to cross-reference a
  // separate roster/newcomer fetch by key.
  async function readOptions(picker) {
    await openPicker(picker);
    const options = await listOf(picker).locator('[role="option"]').evaluateAll((nodes) =>
      nodes.map((n) => ({
        key: n.getAttribute('data-key') ?? '',
        label: n.textContent.replace(/\s+/g, ' ').trim(),
        reason: (n.querySelector('.reason')?.textContent ?? '').trim(),
        disabled: n.disabled,
        hasTrait: n.querySelector('.trait') !== null,
      })),
    );
    await closePicker(picker);
    return options;
  }

  // Modul: ONE QUESTION, NOT TWO TABS. The partner list carries both the
  // village and the player's own line, grouped.
  await page.waitForTimeout(800);
  await openPicker(heroPicker);
  const firstHero = listOf(heroPicker).locator('[role="option"]:not([disabled])').first();
  if ((await firstHero.count()) > 0) await firstHero.click();
  await closePicker(heroPicker);
  await openPicker(partnerPicker);
  const groups = await listOf(partnerPicker).locator('.group h4').allInnerTexts();
  await closePicker(partnerPicker);
  record(
    'one partner list carries both the village and your own line',
    groups.some((g) => /village/i.test(g)) && groups.some((g) => /own line/i.test(g)),
    groups.join(' | ') || 'no groups',
  );

  // The screen has to say what the Inn and the Grounds each buy, or the two
  // levers are invisible and a player cannot tell why their line has stalled.
  record(
    'the screen explains what raises a bloodline',
    /Marrying the\s+village is what raises a bloodline/i.test(text.replace(/\s+/g, ' ')) ||
      /raises a bloodline/i.test(text),
  );

  const heroOptions = await readOptions(heroPicker);
  const heroCount = heroOptions.length;
  // Every character the fixture owns needs a lineage row to appear here at
  // all - the roster endpoint skips a character that has none, which is what
  // made this list silently empty.
  record('the hero list is populated', heroCount > 0, `${heroCount} characters`);

  // THE ANDROID DEFECT, asserted where it lived: nothing inside a picker may
  // tick by the second. Two reads a little over a second apart must agree.
  {
    const first = heroOptions.map((o) => o.label).join('|');
    await page.waitForTimeout(1300);
    const second = (await readOptions(heroPicker)).map((o) => o.label).join('|');
    record('picker labels do not tick every second', first === second);
  }

  // THE HERO FIRST, and this order is the whole point rather than tidiness:
  // who a villager can marry depends on which hero is chosen (same race,
  // opposite sex), so enumerating the village before picking one reads every
  // villager as available and then picks one who is not.
  let marriable = null;
  let marriableLabel = '';
  let villagerTotal = 0;
  let refusals = [];
  let heroesTried = 0;
  // Modul: whether the PICKED hero and PICKED partner each carry a trait -
  // read off the picker cards themselves (see readOptions' hasTrait), so the
  // preview assertion below can say what it should show instead of guessing.
  let chosenHeroHasTrait = false;
  let chosenPartnerHasTrait = false;
  for (const heroOption of heroOptions) {
    if (marriable !== null) break;
    // Skip the heroes the screen has already refused: a child from an earlier
    // run, and anybody inside the cooldown a previous marriage started. Both
    // are honest states rather than failures, and the picker will not let
    // them be chosen anyway.
    if (heroOption.disabled) continue;

    heroesTried++;
    await openPicker(heroPicker);
    await listOf(heroPicker).locator(`[role="option"][data-key="${heroOption.key}"]`).click();
    await page.waitForTimeout(250);

    // VILLAGERS ONLY. The partner list also carries the player's own line,
    // and a run that married a cousin would pass this step while proving
    // nothing about the gene pool. The 'v:' key prefix is the screen's own marker.
    const options = (await readOptions(partnerPicker)).filter((o) => o.key.startsWith('v:'));
    villagerTotal = options.length;
    refusals = options.filter((o) => o.disabled);

    const open = options.find((o) => !o.disabled);
    if (open) {
      marriable = open.key;
      marriableLabel = open.label;
      chosenHeroHasTrait = heroOption.hasTrait;
      chosenPartnerHasTrait = open.hasTrait;
    }
  }

  // Modul: AN EXHAUSTED POOL IS NOT A DEFECT, and the screen says which it is.
  //
  // Three things legitimately leave this step with nobody to marry, and all
  // three are the rules working: every villager marries exactly once, a hero
  // who has just married is resting for half an hour, and a child cannot marry
  // at all. This step used to call all of that a failure - so a run that had
  // simply happened recently reported the breeding screen as broken, which is
  // how the one script that verifies gameplay ends up crying wolf.
  //
  // What must NEVER pass is an option greyed out for no stated reason. Every
  // refused card carries its cause in a `.reason` line - "has already married
  // in", "both women" - so the assertion is that a refusal is explained, not
  // that it is one of a list of causes I happened to enumerate.
  const spent = villagerTotal > 0 && refusals.length === villagerTotal;
  const allExplained = refusals.every((o) => o.reason.length > 0);
  const noHeroFree = heroesTried === 0;
  record(
    'the village offers somebody marriable',
    marriable !== null || noHeroFree || (spent && allExplained),
    marriable
      ? `${villagerTotal} in the village - ${marriableLabel}`
      : noHeroFree
        ? 'every hero is resting or still a child - nobody free to marry this run'
        : spent && allExplained
          ? `${villagerTotal} in the village, every one refused with a reason - pool spent`
          : `${villagerTotal} in the village, ${refusals.filter((o) => !o.reason).length} refused without a reason`,
  );

  if (heroCount > 0 && marriable !== null) {
    const before = heroCount;
    await openPicker(partnerPicker);
    await listOf(partnerPicker).locator(`[role="option"][data-key="${marriable}"]`).click();
    await page.waitForTimeout(1200);

    const preview = await page.evaluate(() => document.body.innerText);
    // The four aptitudes ARE the decision. A pairing screen without them is
    // the loci preview all over again: precise about the thing nobody picks a
    // partner for.
    record(
      'the preview quotes what the child would inherit',
      /What the child would inherit/i.test(preview) &&
        /Strength/.test(preview) && /Fortune/.test(preview),
    );
    // On a failure the screen's own refusal is the useful detail - it is a
    // sentence now rather than a server code, so it says what to fix.
    const priced = /Costs [\d,.\s]+[kMBT]?g/.test(preview);
    record(
      'the preview quotes a price',
      priced,
      priced
        ? (preview.match(/Costs [\d,.\s]+[kMBT]?g/) ?? [''])[0]
        : (await page.locator('.panel .warn').allInnerTexts()).join(' | ') || 'no reason shown',
    );

    // Modul: TRAITS replaced the genes (2026-09-13). The section renders for
    // every pair - "Neither parent carries a trait." is an honest answer - and
    // the mutation chance comes from the Breeding Grounds, so both must be here.
    // Case-insensitive like the sibling check above: every <h3> in
    // ChildPreview.svelte is `text-transform: uppercase`, and innerText
    // reflects the rendered (upper-cased) text, not the source markup.
    const traitsText = await page.evaluate(() => document.body.innerText);
    record(
      'the preview explains traits and the mutation chance',
      /Traits the child can inherit/i.test(traitsText) && /chance of a new trait/i.test(traitsText),
    );
    record('the preview no longer lists genes', !/And its genes/i.test(traitsText));

    // Modul: THE HEADING RENDERING IS NOT THE CLAIM. Task 13's own capture of
    // this screen showed TraitOdds: [] - the assertion above passed on the
    // heading and "Neither parent carries a trait." alone, and the
    // `{#each preview.TraitOdds}` branch (TraitBadge, the odds row) had never
    // once been exercised. `chosenHeroHasTrait`/`chosenPartnerHasTrait` come
    // from the picker cards themselves (PersonPicker renders a TraitBadge per
    // candidate whenever it carries one), which is a deterministic read - no
    // RNG is involved in whether the ODDS list is non-empty, only in what a
    // bred child actually gets. So this asserts the *rendering*, both ways:
    // a real percentage when the pair carries a trait, and the honest empty
    // message when it does not - never one where the other belongs.
    const traitOddsSection = await page.evaluate(() => {
      const heading = Array.from(document.querySelectorAll('h3'))
        .find((h) => /Traits the child can inherit/i.test(h.textContent ?? ''));
      if (!heading) return { found: false };
      let sibling = heading.nextElementSibling;
      let sawEmptyMessage = false;
      const badges = [];
      while (sibling && sibling.tagName !== 'H3') {
        if (/Neither parent carries a trait/i.test(sibling.textContent ?? '')) sawEmptyMessage = true;
        sibling.querySelectorAll('li').forEach((li) => {
          const name = li.querySelector('.trait')?.textContent?.trim()
            ?? li.querySelector('.name')?.textContent?.trim() ?? '';
          const pct = li.querySelector('.band')?.textContent?.trim() ?? '';
          if (name) badges.push({ name, pct });
        });
        sibling = sibling.nextElementSibling;
      }
      return { found: true, sawEmptyMessage, badges };
    });

    const expectTraitOdds = chosenHeroHasTrait || chosenPartnerHasTrait;
    const gotTraitOdds = traitOddsSection.found
      && traitOddsSection.badges.length > 0
      && traitOddsSection.badges.every((b) => /%/.test(b.pct));
    record(
      'the preview lists a named trait with a percentage when the pair carries one',
      traitOddsSection.found && (expectTraitOdds ? gotTraitOdds : traitOddsSection.sawEmptyMessage),
      expectTraitOdds
        ? (gotTraitOdds
          ? traitOddsSection.badges.map((b) => `${b.name} ${b.pct}`).join(', ')
          : `expected odds (hero trait ${chosenHeroHasTrait}, partner trait ${chosenPartnerHasTrait}) but got none`)
        : 'neither picked candidate carries a trait this run - honestly empty',
    );

    // Modul: THE BREEDING GROUNDS' FIRST REAL EFFECT. Its level was read in
    // four places on the server and every one tested `<= 0`, so every upgrade
    // past the first changed no number in the game. The fixture's Grounds is
    // seeded above the level that buys a selection precisely so this can be
    // clicked; if the checkbox is not here, the feature is inert again.
    const aptBoxes = page.locator('.apt-choice input');
    const aptCount = await aptBoxes.count();
    record(
      'the Breeding Grounds offers an aptitude to breed for',
      aptCount === 4,
      `${aptCount} aptitude checkboxes`,
    );

    if (aptCount === 4) {
      await aptBoxes.first().check();
      await page.waitForTimeout(200);
      const checked = await page.locator('.apt-choice input:checked').count();
      // Capped at what the Grounds permits - checking a second must SWAP
      // rather than silently do nothing, which is the worse of the two.
      await aptBoxes.nth(1).check();
      await page.waitForTimeout(200);
      const afterSecond = await page.locator('.apt-choice input:checked').count();
      record(
        'the selection is capped at what the Grounds permits',
        checked >= 1 && afterSecond === checked,
        `${checked} selected, still ${afterSecond} after checking another`,
      );
    }

    await dismissToasts();
    const marryButton = page.getByRole('button', { name: 'Breed', exact: true });
    const blocked = await marryButton.isDisabled();
    record('the fixture can afford to marry', !blocked);

    if (!blocked) {
      // Modul: SERVER TRUTH, fetched before the click, so the child can be
      // identified afterwards by which CharacterId is new - not by DOM
      // position, which readOptions' own ordering makes no guarantee about.
      const rosterBefore = await apiGet('/api/v1/breeding/roster');

      await marryButton.click();
      await page.waitForTimeout(2500);

      // A child on the roster is the whole claim. The villager list shrinking
      // would not prove it - a dismissal does that too.
      const after = (await readOptions(heroPicker)).length;
      record('marrying the village produces a child', after > before, `${before} -> ${after} characters`);

      // Modul: WHAT THE CHILD ACTUALLY GOT, read from the roster endpoint
      // rather than guessed from the parents' odds - inheritance is rolled
      // server-side, so this is the one place in the script that can say
      // whether the trait system produced a real, persisted result rather
      // than only a plausible-looking preview. Held for the Hall of Ancestors
      // section further down, which is where a player actually SEES it.
      if (Array.isArray(rosterBefore) && after > before) {
        const rosterAfter = await apiGet('/api/v1/breeding/roster');
        const beforeIds = new Set(rosterBefore.map((c) => c.CharacterId));
        const child = (rosterAfter ?? []).find((c) => !beforeIds.has(c.CharacterId));
        if (child) {
          bredChildName = child.Name;
          bredChildId = child.CharacterId;
          bredChildHadTrait = Number(child.TraitMask) !== 0;
        }
      }

      // ONE CHILD PER VILLAGER, forever. Without this a single lucky twenty
      // fathers the whole roster and the pool collapses onto one ancestor.
      const spentVillager = (await readOptions(partnerPicker)).find((o) => o.key === marriable);
      record(
        'the villager who married is spent',
        spentVillager !== undefined && /already married in/i.test(spentVillager.reason),
        spentVillager ? `${spentVillager.label.slice(0, 40)} - ${spentVillager.reason || 'no reason'}` : 'villager gone from the list',
      );
      await dismissToasts();
    }
  }
}

// --- the Book of Deeds -------------------------------------------------------
//
// Modul: five chapters, and the Seals that couple them to the skill tree. A
// Seal grants +2 permanent skill points EVERY season, so the chapters and the
// awarding both live on the server - this checks the client renders the real
// answer, with a number on every deed. The old tiered achievements returned 0
// from GetNextTierTarget for most ids and drew "0 / MAX"; a deed without a
// number does not exist to the player, which is why the counter is what gets
// asserted rather than the list.
await go('Progress');
{
  // Modul: TASK 51 - personal records. The fixture has fought by now, so its
  // highest hit is a real number on the live stream (hydrated at login), and
  // the durable copy answers on /player/records.
  const records = await apiGet('/api/v1/player/records');
  record(
    'personal records answer on /player/records',
    !!records && Array.isArray(records.BossBestKillTenths) && records.BossBestKillTenths.length === 5,
    records ? `hit ${records.BestHit}, drop tier ${records.BestDropTier}, deep ${records.DelveDeepestFloor}` : 'no answer',
  );
  // Tasks 56/57: Progress is four tabs; the records live under Statistics.
  await page.locator('[data-progress-tab="stats"]').first().click();
  await page.waitForTimeout(1500);
  const hitLine = await page
    .locator('[data-records] div', { hasText: 'Highest hit' })
    .first()
    .innerText()
    .catch(() => '');
  record('the Progress screen shows a real highest hit', /Highest hit\s*[\d,.\s]*[1-9]/.test(hitLine), hitLine.replace(/\s+/g, ' '));

  // Task 56: the rates table and the style come from the server's samples.
  const insights = await apiGet('/api/v1/player/insights');
  const rateRows = await page.locator('[data-testid="player-insights"] table.rates tbody tr').count();
  record(
    'Statistics shows per-hour rates over three windows',
    !!insights && insights.Rates?.length === 3 && rateRows === 5,
    insights ? `${insights.Rates.map((r) => `${r.Window}: ${r.KillsPerHour} kills/h over ${r.CoveredSeconds}s`).join('; ')}` : 'no answer',
  );

  // Task 57: the collection log - every catalogued piece, the fixture owns some.
  await page.locator('[data-progress-tab="collection"]').first().click();
  await page.waitForTimeout(1500);
  const collection = await apiGet('/api/v1/player/collection');
  const pieceIcons = await page.locator('[data-testid="collection-log"] .pieces li').count();
  record(
    'the collection lists all 75 pieces and records what the fixture owns',
    !!collection && collection.PiecesTotal === 75 && collection.PiecesOwned > 0 && pieceIcons === 75,
    collection ? `${collection.PiecesOwned}/${collection.PiecesTotal} pieces, ${collection.MonstersRecorded}/${collection.MonstersTotal} monsters, ${collection.Percent}%` : 'no answer',
  );

  await page.locator('[data-progress-tab="goals"]').first().click();
  await page.waitForTimeout(1500);
  const text = await page.evaluate(() => document.body.innerText);

  // Task 57: the achievements are the Book's Lifetime chapter now, paid
  // automatically - and nothing on Progress offers a claim any more (a claim
  // used to pay three of the four a second time).
  const lifetimeRows = await page.locator('[data-testid="deeds-lifetime"] li').count();
  const claimButtons = await page.getByRole('button', { name: /^Claim/ }).count();
  record('the Book has a Lifetime chapter of four named achievements', lifetimeRows === 4, `${lifetimeRows} rows`);
  record('Progress offers no claim button', claimButtons === 0, `${claimButtons} claim button(s)`);
  const hiddenText = await page.locator('[data-testid="deeds-hidden"]').first().innerText().catch(() => '');
  record(
    'hidden deeds are listed by category, unnamed until done',
    /Hidden deeds/.test(hiddenText) && /(\?\?\?|Every throne|One enormous blow)/.test(hiddenText),
    hiddenText.replace(/\s+/g, ' ').slice(0, 90),
  );
  record('the Book of Deeds is shown', /Book of Deeds/i.test(text));
  record(
    'all five chapters are listed',
    /The Village Road/.test(text) && /Smiths/.test(text) && /Hunters/.test(text) &&
      /Stewards/.test(text) && /Ledger of Legends/.test(text),
  );
  record('a Seal is priced in skill points', /skill points/i.test(text));

  // Every unfinished deed with a target above one must show its x / y.
  const meters = await page.locator('.deeds .count').allInnerTexts();
  record(
    'unfinished deeds carry a live counter',
    meters.length > 0 && meters.every((m) => /\d[\d,]*\s*\/\s*\d/.test(m)),
    meters.slice(0, 3).join(', '),
  );

  // The fixture has done chapter I many times over, so its Seal must be real.
  record(
    'a finished chapter is sealed',
    /sealed/i.test(text),
    (text.match(/(\d+) Seals?/) ?? ['no seals'])[0],
  );
}

// --- the home screen: what everybody is doing, and the closest goal ----------
//
// Modul: the map used to say where things are and nothing about what was
// happening. The cards under it must name every working character's job and
// offer the nearest deed - and its Go must actually leave the map, because a
// card whose button renders but goes nowhere is this project's oldest bug.
await go('Map');
{
  await page.waitForFunction(() => /Right now/.test(document.body.innerText), null, { timeout: 10000 }).catch(() => {});
  const text = await page.evaluate(() => document.body.innerText);
  record('the home screen says what the characters are doing', /Right now/.test(text) && /(Fighting|Idle|Crafting| in )/.test(text));
  const goalCard = page.locator('section.card', { hasText: 'Closest goal' });
  if ((await goalCard.count()) > 0) {
    await goalCard.getByRole('button', { name: 'Go', exact: true }).click();
    await page.waitForTimeout(800);
    const left = !(await page.evaluate(() => /Right now/.test(document.body.innerText)));
    record('the closest goal takes you to where it is done', left);
  } else {
    record('the closest goal takes you to where it is done', false, 'no goal card - every open deed finished?');
  }
}

// --- the Hall of Ancestors ---------------------------------------------------
//
// Modul: the roster that outlives a season, and the door that never existed.
// Nothing in this server had ever written a CharacterRecord.SlotIndex after
// creation, so a child bred past the third slot was permanently unplayable -
// which makes "begin the next season with your best child", the loop the whole
// long game is built on, impossible to perform.
await go('Ancestors');
{
  const text = await page.evaluate(() => document.body.innerText);
  record('the Hall states what a season does not take', /does not take these/i.test(text));
  record(
    'the Hall shows how many carry',
    /\d+\s*\/\s*\d+/.test(text),
    (text.match(/(\d+)\s*\/\s*(\d+)/) ?? [''])[0],
  );

  const rows = page.locator('.panel li');
  record('the Hall lists the roster', (await rows.count()) > 0, `${await rows.count()} members`);

  // Modul: CARRIED AND LOST (task 104). The Hall is two sections now: the
  // carried few, open, and "Lost at rebirth", collapsed and paged 20 at a
  // time. Traits, parents, fielding and the keep toggle open in a sheet when a
  // row is tapped. So a row is FOUND (opening Lost and paging until it is
  // drawn) and then OPENED, rather than assumed to be on the page.
  const hallSheet = page.locator('[data-testid="hall-sheet"]');
  const hallRow = (id) => page.locator(`.panel li[data-character-id="${id}"]`);
  async function revealHallRow(id) {
    if ((await hallRow(id).count()) > 0) return true;
    const lostToggle = page.locator('[data-testid="hall-lost-toggle"]');
    if ((await lostToggle.count()) > 0 && (await lostToggle.getAttribute('aria-expanded')) !== 'true') {
      await lostToggle.click();
      await page.waitForTimeout(300);
    }
    for (let i = 0; i < 30 && (await hallRow(id).count()) === 0; i++) {
      const more = page.locator('[data-testid="hall-lost-more"]');
      if ((await more.count()) === 0) break;
      await more.click();
      await page.waitForTimeout(200);
    }
    return (await hallRow(id).count()) > 0;
  }
  async function openHallSheet(id) {
    if (!(await revealHallRow(id))) return false;
    await hallRow(id).locator('button.open').click();
    return hallSheet.isVisible({ timeout: 3000 }).catch(() => false);
  }
  async function closeHallSheet() {
    if ((await hallSheet.count()) === 0) return;
    await hallSheet.getByRole('button', { name: 'Close', exact: true }).click();
    await page.waitForTimeout(200);
  }
  const hallNow = async () => (await apiGet('/api/v1/ancestors/hall'))?.Members ?? [];

  // The split is the SERVER's WouldCarry, and the counter says the same number.
  {
    const members = await hallNow();
    const carriedIds = members.filter((m) => m.WouldCarry).map((m) => m.CharacterId).sort();
    const drawn = (await page.locator('[data-testid="hall-carried-list"] li').evaluateAll(
      (els) => els.map((el) => el.getAttribute('data-character-id')),
    )).sort();
    record(
      'the carried section is exactly who the server says carries',
      carriedIds.length > 0 && JSON.stringify(carriedIds) === JSON.stringify(drawn),
      `${drawn.length} drawn, ${carriedIds.length} carried by the server`,
    );
    // No row may say "Kept" without saying whether it carries.
    const badges = await page.locator('.panel li .badge').allInnerTexts();
    record(
      'no row says Kept without saying whether it carries',
      badges.every((t) => !/^Kept$/i.test(t.trim())),
      `${badges.length} badges`,
    );
  }

  // Modul: THE BRED CHILD'S OWN TRAITS, not just the preview that promised it.
  // `bredChildHadTrait` came from GET /api/v1/breeding/roster right after the
  // Breed click - server truth, rolled server-side - so this is checking that
  // what the server actually granted the child is what the player can see on
  // the one screen that shows a roster member's traits after birth
  // (`m.TraitMask > 0` gates TraitBadge in the Hall's detail sheet). A pass
  // here with no badge rendered would be exactly the "output side was never
  // wired" shape this project keeps shipping.
  if (bredChildName === null) {
    record('a bred child with a trait shows it on the Hall', true, 'no child identified this run - skipped');
  } else if (bredChildHadTrait === false) {
    record(
      'a bred child with a trait shows it on the Hall',
      true,
      `${bredChildName} inherited no trait this roll (RNG) - odds were still asserted on the preview above`,
    );
  } else {
    // By id, not name: the fixture had NINE characters called Muirenn and
    // `.first()` matched an older one with no trait - a false FAIL on a
    // child the server had granted a trait to (measured 2026-09-23).
    const opened = await openHallSheet(bredChildId);
    const badgeCount = opened ? await hallSheet.locator('.trait').count() : 0;
    record(
      'a bred child with a trait shows it on the Hall',
      opened && badgeCount > 0,
      opened ? `${bredChildName}: ${badgeCount} trait badge(s)` : `${bredChildName} not found in the Hall`,
    );
    await closeHallSheet();
  }

  // The pedigree: everybody came from somewhere, and a founder says so. By
  // NAME - it printed eight hex digits of each parent's Guid until 2026-09-13.
  {
    const firstId = await rows.first().getAttribute('data-character-id').catch(() => null);
    const opened = firstId !== null && (await openHallSheet(firstId));
    const sheetText = opened ? await hallSheet.innerText() : '';
    record(
      'every member names where they came from',
      /a founder of the line|child of /i.test(sheetText) && !/\b[0-9a-f]{8} x [0-9a-f]{8}\b/.test(sheetText),
      sheetText.replace(/\s+/g, ' ').slice(0, 100),
    );
    await closeHallSheet();
  }

  // Marking. The whole reason the cap is a decision rather than a surprise.
  //
  // Modul: A ROUND TRIP, not a one-way click. Marking is a flag that nothing
  // ever clears, so "click Keep, expect a Kept" only works while an unmarked
  // member is left: every run marked one more until all 23 non-main ancestors
  // were marked, and the step then failed permanently. Toggling whichever
  // direction is available asserts both directions and puts the flag back.
  //
  // Modul: BY ID, AND READ BACK FROM THE SERVER. Since task 104 a mark
  // re-ranks the row (marked members sort up, and a mark can move somebody
  // between Carried and Lost), so neither a position nor the button's label is
  // a stable handle. The row's pin carries `aria-pressed`, and the server's
  // IsKept is the truth both are checked against. A carried, non-main member
  // is used: marking them cannot push them out, and unmarking returns them to
  // exactly the place they had.
  {
    const members = await hallNow();
    const target = members.find((m) => m.WouldCarry && !m.IsMainCharacter && !m.IsKept)
      ?? members.find((m) => m.WouldCarry && !m.IsMainCharacter);
    if (!target) {
      record('marking an ancestor to carry sticks', false, 'no carried member besides the main character');
    } else {
      await dismissToasts();
      const before = target.IsKept;
      const pin = () => hallRow(target.CharacterId).locator('button.keep');
      const keptNow = async () => (await hallNow()).find((m) => m.CharacterId === target.CharacterId)?.IsKept;
      const waitFor = async (want) => {
        for (let i = 0; i < 12; i++) {
          await page.waitForTimeout(400);
          if ((await keptNow()) === want) return true;
        }
        return false;
      };
      await pin().click();
      const flipped = await waitFor(!before);
      await page.waitForTimeout(1200);
      const pressed = (await revealHallRow(target.CharacterId)) ? await pin().getAttribute('aria-pressed') : null;
      let restored = false;
      if (flipped) {
        await pin().click();
        restored = await waitFor(before);
        await page.waitForTimeout(1200);
      }
      record(
        'marking an ancestor to carry sticks',
        flipped && restored && pressed === String(!before),
        `${before} -> ${flipped ? !before : before} (pin pressed ${pressed}) -> ${restored ? before : 'not restored'}`,
      );
    }
  }

  // FIELDING - the missing door. A benched member picks a slot and the roster
  // has to actually change. Slot BUTTONS since 2026-09-13 (the select was a
  // native Android dialog), and inside the row's detail sheet since task 104.
  {
    const members = await hallNow();
    const benchedMembers = members.filter((m) => m.PlayableSlot < 0);
    record('benched members can be fielded', benchedMembers.length > 0, `${benchedMembers.length} on the bench`);

    const candidate = benchedMembers.find((m) => m.WouldCarry) ?? benchedMembers[0];
    if (candidate) {
      // Modul: WHO SLOT 1 BELONGED TO, so the swap can be undone. It used to be
      // left in place: the main character (the fixture's only armed one) went to
      // the bench, the fielded ancestor wore nothing, and the tutorial's "wear
      // your weapon" step then fenced every screen for the geometry checkers
      // and every later run until a --seed-dev.
      const displacedId = members.find((m) => m.PlayableSlot === 0)?.CharacterId ?? null;

      const opened = await openHallSheet(candidate.CharacterId);
      const slotButton = hallSheet.locator('.field .field-slot').first();
      const offered = opened && (await slotButton.count()) > 0;
      let fieldedAt = -1;
      if (offered) {
        await slotButton.click();
        for (let i = 0; i < 20 && fieldedAt < 0; i++) {
          await page.waitForTimeout(500);
          fieldedAt = (await hallNow()).find((m) => m.CharacterId === candidate.CharacterId)?.PlayableSlot ?? -1;
        }
      }
      record(
        'fielding an ancestor swaps them into a playable slot',
        fieldedAt >= 0,
        offered ? `${candidate.Name || candidate.CharacterId} -> slot ${fieldedAt + 1}` : 'the sheet offered no slot button',
      );
      await closeHallSheet();
      await dismissToasts();

      // Put slot 1 back the way it was. Not a click: the ancestor now in slot 1
      // wears nothing, so the tutorial's guided fence covers the Hall at once.
      if (displacedId !== null) {
        await page.evaluate((id) => globalThis.__folkidleAssignSlot?.(id, 0), displacedId);
        let back = -1;
        for (let i = 0; i < 20 && back !== 0; i++) {
          await page.waitForTimeout(500);
          back = (await hallNow()).find((m) => m.CharacterId === displacedId)?.PlayableSlot ?? -1;
        }
        record('fielding is undone: slot 1 holds who it held before', back === 0, back === 0 ? '' : `playable slot ${back}`);
        await dismissToasts();
      } else {
        record('fielding is undone: slot 1 holds who it held before', false, 'slot 1 was empty or not found before the swap');
      }
    }
  }
}

// --- orders: automation rules, round-tripped (task 85) -----------------------
//
// Modul: SET, READ BACK, RESTORE. The fixture is level 40, so slots 1 and 2 are
// open and slot 3 (level 60) is not. This gives slot 1 of the first fielded
// character an order THROUGH THE PANEL, reads it back from the server, asks
// the server for an order in the locked slot and expects the refusal to be
// ANSWERED (200 + Result, never a silent no-op), and then puts the character's
// three rules back exactly as they were - a check that left an order behind
// would change how the fixture fights in every later run.
{
  await dismissToasts();
  const before = await apiGet('/api/v1/automation-rules');
  const first = before?.Characters?.[0];
  record(
    'orders: the rules answer, with three slots per fielded character',
    before !== null && Array.isArray(before.UnlockLevels) && before.UnlockLevels.length === 3
      && first !== undefined && first.Rules.length === 3,
    before ? `level ${before.Level}, ${before.Characters.length} characters, unlocks ${before.UnlockLevels.join('/')}` : 'no answer',
  );

  if (first) {
    const original = first.Rules.map((r) => ({ Type: r.Type, Param: r.Param }));
    await go('Character');
    // Task 97: Orders sit on the Work & orders tab and show the person the
    // switcher is on.
    await characterPerson(first.CharacterId);
    await characterTab('work');
    const block = page.locator(`[data-testid="orders-character"][data-character-id="${first.CharacterId}"]`);
    await block.waitFor({ timeout: 10000 }).catch(() => {});

    // The locked slot says so, and offers no control.
    const locked = block.locator('[data-testid="orders-slot"][data-open="false"]');
    const expectLocked = before.UnlockLevels.filter((l) => before.Level < l).length;
    record(
      'orders: a slot above the level shows its unlock level, not a control',
      (await locked.count()) === expectLocked
        && (expectLocked === 0 || /Opens at level \d+/.test(await locked.first().innerText().catch(() => ''))),
      `${await locked.count()} locked of 3 at level ${before.Level}`,
    );

    // Give slot 1 an order it does not already have, through the panel.
    const wanted = original[0].Type === 2 ? 1 : 2;
    const typeSelect = block.locator('[data-testid="orders-slot"][data-slot="0"] [data-testid="orders-type"]');
    if ((await typeSelect.count()) > 0) {
      // The other slot may already hold the order we want; free it first.
      if (original.some((r, i) => i !== 0 && r.Type === wanted)) {
        await apiPost('/api/v1/automation-rules', {
          CharacterId: first.CharacterId,
          Rules: original.map((r, i) => (i !== 0 && r.Type === wanted ? { Type: 0, Param: 0 } : r)),
        });
        await page.reload({ waitUntil: 'networkidle' });
        await go('Character');
        await characterPerson(first.CharacterId);
        await characterTab('work');
        await block.waitFor({ timeout: 10000 }).catch(() => {});
      }
      await typeSelect.selectOption(String(wanted));
      await block.locator('[data-testid="orders-save"]').click();
      let saved = null;
      for (let i = 0; i < 20; i++) {
        await page.waitForTimeout(300);
        const now = await apiGet('/api/v1/automation-rules');
        saved = now?.Characters?.find((c) => c.CharacterId === first.CharacterId)?.Rules?.[0] ?? null;
        if (saved?.Type === wanted) break;
      }
      record('orders: an order set on the panel is what the server holds', saved?.Type === wanted,
        saved ? `slot 1 type ${saved.Type} param ${saved.Param}` : 'not read back');
    } else {
      record('orders: an order set on the panel is what the server holds', false, 'slot 1 has no control (level below 20?)');
    }

    // A locked slot is refused out loud.
    if (before.Level < before.UnlockLevels[2]) {
      const refused = await apiPost('/api/v1/automation-rules', {
        CharacterId: first.CharacterId,
        Rules: [{ Type: 0, Param: 0 }, { Type: 0, Param: 0 }, { Type: 2, Param: 0 }],
      });
      record('orders: a rule in a locked slot is refused with a reason', refused?.Result === 'SlotLocked', refused?.Result ?? 'no answer');
    }

    // Restore, and prove it.
    const restored = await apiPost('/api/v1/automation-rules', { CharacterId: first.CharacterId, Rules: original });
    const back = restored?.Characters?.find((c) => c.CharacterId === first.CharacterId)?.Rules ?? [];
    record(
      'orders: the fixture ends with the rules it started with',
      restored?.Result === 'Ok' && back.length === 3 && back.every((r, i) => r.Type === original[i].Type && r.Param === original[i].Param),
      restored ? `${restored.Result}: ${JSON.stringify(back)}` : 'no answer',
    );
    await dismissToasts();
  }
}

// --- rebirth: the PREVIEW only, on the fixture (task 88) ---------------------
//
// Modul: THE FIXTURE IS NEVER REBORN. A rebirth takes its level, gear, gold and
// skill tree - every later step and every later run would be testing a level-1
// account with nothing. So on the fixture this opens step one, reads the terms
// against the server's own preview, and CANCELS; the real rebirth is pressed on
// the throwaway account below, which exists to be spent.
{
  // Modul: the panel lives in the Hall of Ancestors. This step once relied on
  // the step before it having left the page there; the orders step (task 85)
  // goes to Character, so it navigates for itself now.
  await go('Ancestors');
  await dismissToasts();
  const panel = page.locator('[data-testid="rebirth-panel"]');
  await panel.waitFor({ timeout: 10000 }).catch(() => {});
  const before = await apiGet('/api/v1/rebirth/preview');
  record(
    'the rebirth preview answers',
    before !== null && typeof before.RebirthCount === 'number' && typeof before.ShardsEarned === 'number',
    before ? `rebirths ${before.RebirthCount}, level ${before.Level}, ${before.ShardsEarned} shards, renowned ${before.Renowned}` : 'no answer',
  );

  const renown = await panel.locator('[data-testid="rebirth-renown"]').innerText().catch(() => '');
  record('the Rebirth panel shows Renown', /Renown \d+/.test(renown), renown.replace(/\s+/g, ' ').slice(0, 90));

  const open = panel.locator('button.rebirth-open');
  if ((await open.count()) > 0) {
    await open.click();
    const terms = await panel.locator('[data-testid="rebirth-terms"]').innerText().catch(() => '');
    record(
      'step one opens the terms: what you lose, what you keep',
      /You lose/.test(terms) && /You keep/.test(terms) && before !== null && terms.includes(`level ${before.Level}`),
      terms.replace(/\s+/g, ' ').slice(0, 120),
    );
    record(
      'step one does not rebirth - step two is its own button',
      (await panel.locator('button.rebirth-confirm').count()) === 1,
    );

    await panel.locator('button.rebirth-cancel').click();
    await page.waitForTimeout(500);
    const after = await apiGet('/api/v1/rebirth/preview');
    record(
      'cancel changes nothing',
      after !== null && before !== null
        && after.RebirthCount === before.RebirthCount
        && after.Level === before.Level
        && (await panel.locator('[data-testid="rebirth-terms"]').count()) === 0,
      after ? `rebirths ${after.RebirthCount}, level ${after.Level}` : 'no answer',
    );
  } else {
    record('step one opens the terms: what you lose, what you keep', false, 'no Rebirth button rendered');
  }
}

// --- onboarding, on an account that has never played -------------------------
//
// Modul: A BRAND-NEW ACCOUNT, in its own browser context, and this is the only
// part of the script the dev fixture cannot stand in for. Onboarding is a
// predicate over the state packet, and every one of the fixture's predicates is
// already true - it has fought, dressed and stocked the larder - so signing in
// as the fixture shows an empty coach panel and proves nothing at all. Seen
// state lives in localStorage, so the context has to be fresh too.
//
// This is the step list in docs/onboarding_steps.md section 6, which claimed
// this coverage existed before it did.
{
  const context = await browser.newContext({ viewport: { width: 1500, height: 1000 } });
  const fresh = await context.newPage();

  // Console errors from the new account count too - a screen that throws for a
  // player who owns nothing is exactly the kind of thing the fixture hides.
  fresh.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  fresh.on('pageerror', (e) => consoleErrors.push(`pageerror: ${e.message}`));

  const stamp = Date.now();
  const email = `exercise${stamp}@folkidle.local`;

  await fresh.goto(BASE, { waitUntil: 'networkidle' });
  // A new browser context, so the Android popup is up again.
  const notNow = fresh.getByRole('button', { name: 'Not now', exact: true });
  if ((await notNow.count()) > 0) await notNow.click();
  await fresh.getByRole('button', { name: 'Create an account' }).click();
  await fresh.locator('input[type="email"]').fill(email);
  // The username field is the only text input that is neither email nor password.
  await fresh
    .locator('input:not([type="email"]):not([type="password"])')
    .first()
    .fill(`exercise${stamp % 1_000_000}`);
  await fresh.locator('input[type="password"]').fill('FolkIdleExercise123!');
  await fresh.getByRole('button', { name: 'Create account', exact: true }).click();

  const registered = await fresh
    .waitForFunction(
      // Modul: the Combat ENTRY, not the word. innerText skips display:none,
      // and since task 82 a desktop header keeps Combat in the closed Play
      // group - the account was in the game and the check said it was not.
      () => !document.body.innerText.includes('Waiting for the first state snapshot')
        && document.querySelector('header [data-nav="combat"]') !== null,
      { timeout: 25000 },
    )
    .then(() => true)
    .catch(() => false);
  record('a brand-new account can register and reach the game', registered, email);

  if (registered) {
    await fresh.waitForTimeout(2500);

    // Task 60: a new account sees the screens that are no use to it yet
    // greyed, with what opens them - and the Market says "Level 10".
    {
      const locked = await fresh.evaluate(() =>
        [...document.querySelectorAll('header button[data-locked]')].map(
          (b) => `${b.dataset.label}=${b.dataset.locked}`,
        ),
      );
      // Task 76: the Market is a TAB of Community now, so the lock is on the
      // tab and the entry stays open for Friends and Leaderboards.
      const community = locked.find((l) => l.startsWith('Community='));
      // A DOM click, not a pointer one: a brand-new account has onboarding
      // overlays up at this point, and what is checked is the lock state,
      // not whether the button can be reached through them.
      await fresh.evaluate(() => document.querySelector('header button[data-nav="social"]')?.click());
      await fresh.waitForTimeout(800);
      const marketTab = await fresh
        .locator('[data-subtab="market"]')
        .first()
        .getAttribute('data-locked')
        .catch(() => null);
      record(
        'a new account sees the Market tab greyed until level 10, Community open',
        community === undefined && marketTab === 'Level 10',
        `Community ${community ?? 'open'}; Market tab ${marketTab ?? 'open'}`,
      );
      record(
        'a new account keeps Combat, Character and Supplies open',
        !locked.some((l) => /^(Combat|Character|Supplies|Map)=/.test(l)),
        locked.join(', ') || 'nothing greyed',
      );
    }

    const cue = () =>
      fresh.evaluate(() => {
        const panel = document.querySelector('.coach');
        if (!panel) return null;
        return {
          id: panel.dataset.onboardingCue,
          kind: panel.dataset.onboardingKind,
          text: panel.innerText.replace(/\s+/g, ' ').trim(),
        };
      });

    // 1. The panel is there, on step one of three, and it is an INSTRUCTION.
    const first = await cue();
    record(
      'a new account is met by the onboarding coach',
      first !== null && first.kind === 'step',
      first ? `${first.id} - ${first.text.slice(0, 60)}` : 'no coach panel rendered',
    );
    // Modul: THE LARDER, and this is a regression test for a closed entrance.
    //
    // The first step used to be "press Fight on Field Mouse", which a new
    // account cannot do: measured here, an empty larder means death at 29 s
    // with the monster still on 264 of its 465 HP. Because the steps block
    // each other in order, the food advice sat in step three behind a step
    // nobody could finish. If this ever reads "combat" again, the entrance has
    // been closed a second time - see tutorialSteps.ts.
    record(
      'the first thing a new player is told is to stock the larder',
      first !== null && /larder|Auto-Eat|fish/i.test(first.text),
      first ? first.text.slice(0, 90) : '',
    );

    // 2. THE GUIDED FIRST MINUTE (owner, 2026-09-28). A new account starts
    //    with ten fish and a claymore in the chest, and the first two steps
    //    are a walk-through: the screen dims, one control is lit, nothing else
    //    can be pressed. Checked the way a player meets it - including that a
    //    press ANYWHERE ELSE really does nothing, because a fence that leaks
    //    teaches nothing and a fence with no way through traps someone.
    const guided = () =>
      fresh.evaluate(() => document.querySelector('[data-guided]')?.getAttribute('data-guided') ?? null);
    await fresh.waitForFunction(() => document.querySelector('[data-guided]') !== null, null, { timeout: 8000 }).catch(() => {});
    const layer = await guided();
    record('a new account is walked through its first step, not just told', layer !== null, layer ?? 'no guided layer');

    // A press on the nav while the layer is up lands on the cover. Pressed by
    // coordinates, because Playwright (rightly) refuses to click a covered
    // element - which is exactly what a thumb does not refuse.
    // Task 82: on a desktop 'Gathering' sits behind the Play dropdown, so the
    // thing pressed is whichever of the two is on screen.
    const gatherNav = fresh.locator('header nav').getByRole('button', { name: 'Gathering', exact: true }).first();
    const navBox = (await gatherNav.isVisible().catch(() => false))
      ? await gatherNav.boundingBox()
      : await fresh.locator('header [data-group-toggle="Play"]').first().boundingBox();
    if (navBox) await fresh.mouse.click(navBox.x + navBox.width / 2, navBox.y + navBox.height / 2);
    await fresh.waitForTimeout(800);
    const fenced = !(await fresh.evaluate(() => /Hauled this session/i.test(document.body.innerText)));
    record('while guided, only the lit control can be pressed', fenced, fenced ? '' : 'the nav press went through');

    await fresh.locator('[data-guided-go]').click({ timeout: 5000 }).catch(() => {});
    await fresh.waitForTimeout(1500);
    const arrived = await fresh.evaluate(() => /Load up to three foods/i.test(document.body.innerText));
    const afterNav = await cue();
    record('the guided step takes you to the screen it is about', arrived, arrived ? 'the larder' : 'did not land on Auto-Eat');
    record(
      'being shown a step does not complete it',
      afterNav !== null && afterNav.id === first?.id,
      afterNav ? `still ${afterNav.id}` : 'the panel vanished on navigation',
    );

    // The starter fish, loaded with the one lit button.
    await fresh.waitForFunction(() => document.querySelector('[data-guided="lit"]') !== null, null, { timeout: 8000 }).catch(() => {});
    await fresh.locator('[data-guide="larder-load"]').click({ timeout: 5000 }).catch(() => {});
    const loaded = await fresh
      .waitForFunction((was) => document.querySelector('.coach')?.dataset.onboardingCue !== was, first?.id, { timeout: 20000 })
      .then(() => true)
      .catch(() => false);
    const second = await cue();
    record('loading the starter fish completes the first step', loaded && second !== null, second ? `${first?.id} -> ${second.id}` : 'the step did not move');
    record('the second step is wearing the weapon', second !== null && /weapon|wear/i.test(second.text), second ? second.text.slice(0, 70) : '');

    // The claymore: the way to Character, the weapon slot, then Wear.
    await fresh.locator('[data-guided-go]').click({ timeout: 5000 }).catch(() => {});
    await fresh.waitForTimeout(1500);
    await fresh.locator('[data-guide="slot-0"]').first().click({ timeout: 8000 }).catch(() => {});
    await fresh.waitForTimeout(800);
    await fresh.locator('[data-guide="wear-first"]').click({ timeout: 8000 }).catch(() => {});
    const worn = await fresh
      .waitForFunction((was) => document.querySelector('.coach')?.dataset.onboardingCue !== was, second?.id, { timeout: 20000 })
      .then(() => true)
      .catch(() => false);
    const third = await cue();
    record('wearing the starter weapon completes the second step', worn && third !== null, third ? `${second?.id} -> ${third.id}` : 'the step did not move');
    record('the third step is the fight, now that it can be won', third !== null && /Fight|Combat/i.test(third.text), third ? third.text.slice(0, 70) : '');
    await fresh.waitForTimeout(600);
    record('the guided layer lets go once both are done', (await guided()) === null);

    // Modul: A NEW PLAYER CAN STRIKE THE WORLD BOSS (task 25). This used to run
    // BEFORE the larder was stocked, to pin that the old empty-larder rule
    // (dropped 2026-09-24) stayed dropped. Since the starter kit (2026-09-28)
    // the guided steps stock the larder before the nav can be reached, so the
    // check now proves a brand-new account can take part at all. Either the strike lands (health moved) or the screen names
    // the reason - never "nothing happened". The window is opened and closed
    // around the check, so the fixture is left as the calendar would leave it.
    {
      const freshToken = await fresh.evaluate(
        () => sessionStorage.getItem('folkidle.token') ?? localStorage.getItem('folkidle.token'),
      );
      const freshWindow = async (open) => {
        const res = await fetch(`${API_BASE}/api/v1/dev/worldboss/window`, {
          method: 'POST',
          headers: { Authorization: `Bearer ${freshToken}`, 'Content-Type': 'application/json' },
          body: JSON.stringify(open ? { open: true, durationSeconds: 900 } : { open: false }),
        });
        return res.status;
      };
      const openStatus = await freshWindow(true);
      await (await navButton(fresh, 'World Boss')).click();
      const active = await fresh
        .waitForFunction(() => (document.querySelector('.state')?.textContent ?? '').trim() === 'Active', null, { timeout: 70000 })
        .then(() => true)
        .catch(() => false);
      const hp = () =>
        fresh.evaluate(() => Number(document.querySelector('.bar[role="progressbar"]')?.getAttribute('aria-valuenow') ?? -1));
      // Modul: THIS PLAYER'S damage on the board, not the boss's HP. The HP is
      // shared and LiveOps rescales it with the population every minute - a
      // brand-new account coming online doubled it (75M -> 150M) inside this
      // check's 10 s window, and a strike that landed read as "nothing
      // happened" (2026-09-28, the same class as TASK_BOARD 53).
      const myDamage = () =>
        fresh.evaluate(async (base) => {
          const token = sessionStorage.getItem('folkidle.token') ?? localStorage.getItem('folkidle.token');
          const res = await fetch(`${base}/api/v1/worldboss/board`, { headers: { Authorization: `Bearer ${token}` } });
          if (!res.ok) return -1;
          return Number((await res.json())?.Me?.Damage ?? 0);
        }, API_BASE);
      // Under the wheel the one-press strike is the auto-strike. The screen
      // learns its mode from a REST call after it renders, so wait for it:
      // counting too early picked button.attack, which under the wheel opens
      // a real run whose overlay then covered everything below.
      await fresh.waitForSelector('[data-testid="auto-strike"]', { timeout: 5000 }).catch(() => {});
      const strike = (await fresh.locator('button.auto').count()) > 0 ? fresh.locator('button.auto').first() : fresh.locator('button.attack').first();
      const grey = await strike.isDisabled().catch(() => true);
      let outcome = 'nothing happened';
      let landed = false;
      if (active && !grey) {
        // Earlier toasts first ("Done." from wearing the starter weapon): the
        // wait below ends on ANY toast, so a stale one read as the strike's
        // answer before the strike had landed.
        const staleToasts = fresh.locator('.toast button[aria-label="Dismiss"], .toast button.ghost');
        for (let i = await staleToasts.count(); i > 0; i--) {
          await staleToasts.first().click().catch(() => {});
        }
        const before = await hp();
        const damageBefore = await myDamage();
        await strike.click();
        await fresh
          .waitForFunction((n) => {
            const v = Number(document.querySelector('.bar[role="progressbar"]')?.getAttribute('aria-valuenow') ?? -1);
            // A command toast, not the "FolkIdle has been updated" prompt,
            // which is also a .toast and appears after any client edit.
            const told = [...document.querySelectorAll('.toast')].some((t) => !/has been updated/i.test(t.textContent ?? ''));
            return (v >= 0 && v < n) || told;
          }, before, { timeout: 10000 })
          .catch(() => {});
        const after = await hp();
        let damageAfter = await myDamage();
        for (let i = 0; i < 10 && damageAfter <= damageBefore; i++) {
          await fresh.waitForTimeout(500);
          damageAfter = await myDamage();
        }
        const toastText = (await fresh.locator('.toast').allInnerTexts())
          .filter((t) => !/has been updated/i.test(t))
          .join(' | ');
        landed = damageBefore >= 0 && damageAfter > damageBefore;
        outcome = landed
          ? `my damage ${damageBefore} -> ${damageAfter} (hp ${before} -> ${after})`
          : toastText ? `told: ${toastText}` : `my damage ${damageBefore} -> ${damageAfter}, hp ${before} -> ${after}, no message`;
      } else {
        const reason = await fresh.locator('.strike-reason').innerText().catch(() => '');
        outcome = reason ? `grey: ${reason}` : `window ${openStatus}, active ${active}, grey with no reason`;
      }
      record('a brand-new account can strike the world boss', landed, outcome);
      await freshWindow(false);

      // Task 36: a brand-new account can play shield wheel practice start to
      // finish. The fixture has done everything and is an admin, so only a
      // fresh account proves the practice path does not lean on either. No
      // taps at all: the run times out, is scored at the floor, and the card
      // still says what happened.
      const practice = fresh.getByRole('button', { name: /Practice the shield wheel/i }).first();
      if ((await practice.count()) > 0) {
        await practice.click();
        const card = await fresh
          .locator('[data-testid="practice-card"]')
          .waitFor({ timeout: 45000 })
          .then(() => fresh.locator('[data-testid="practice-card"]').innerText())
          .catch(() => '');
        record('a brand-new account can finish a shield wheel practice run', /No damage dealt/i.test(card), card ? card.split('\n')[0] : 'no result card');
        await fresh.locator('[data-schedule]').getByRole('button', { name: /^\s*Close\s*$/i }).first().click().catch(() => {});
      } else {
        record('a brand-new account can finish a shield wheel practice run', false, 'no Practice button for the new account');
      }
    }

    // 3. Survives a reload. Progress is re-derived from the packet rather than
    //    stored, so a player who closed the tab mid-step comes back to it.
    await fresh.reload({ waitUntil: 'networkidle' });
    await fresh
      .waitForFunction(
        () => !document.body.innerText.includes('Waiting for the first state snapshot'),
        { timeout: 25000 },
      )
      .catch(() => {});
    await fresh.waitForTimeout(2500);
    const reloaded = await cue();
    record(
      'onboarding survives a reload rather than restarting',
      reloaded !== null && reloaded.id === (third?.id ?? first?.id),
      reloaded ? reloaded.id : 'no cue after reload',
    );

    // 4. THE WAY TO MORE FOOD. The guided steps used the starter fish; the
    //    lesson after them is that food is caught, not given. The account
    //    fishes with the rod it was granted and the catch must reach the chest.
    //
    //    It does not fight: winning is minutes of real combat, and the claim
    //    worth holding here is that the entrance opens, not how long region 1
    //    takes.
    await (await navButton(fresh, 'Gathering')).click();
    await fresh.waitForTimeout(1500);
    const rod = fresh
      .locator('.panel')
      .filter({ has: fresh.getByRole('heading', { name: 'Fishing', exact: true }) })
      .getByRole('button', { name: 'Gather' })
      .first();
    const canFish = (await rod.count()) > 0;
    record('a new account can fish with the rod it was given', canFish);

    if (canFish) {
      await rod.click();
      await fresh.waitForTimeout(45000);

      await (await navButton(fresh, 'Supplies')).click();
      await fresh.waitForTimeout(1500);

      const foodSelect = fresh.locator('select').first();
      // No select at all is the empty state - the guided step loaded every
      // starter fish, so only a catch can bring it back.
      const caught = (await foodSelect.count()) > 0 ? await foodSelect.evaluate((s) => [...s.options].length - 1) : 0;
      record('fishing puts food in the village chest', caught > 0, `${caught} kind(s) of fish offered`);

    }

    // 5. Settings owns the off switch and the way back, and neither is
    //    reachable only once.
    await (await navButton(fresh, 'Settings')).click();
    await fresh.waitForTimeout(1200);

    // Modul: task 96. Settings' panels start closed (they remember being
    // opened, so check before clicking), and the explanations sit behind their
    // own disclosure. Only SEEN explanations are listed now - a new player was
    // shown all 27 in full, every future system spoiled at once - and the rest
    // are a count. Seen rows plus that count must account for every one.
    const signOutVisible = await fresh
      .getByRole('button', { name: 'Sign out', exact: true })
      .first()
      .evaluate((el) => el.getBoundingClientRect().top < window.innerHeight)
      .catch(() => false);
    record('Settings shows Sign out in the first viewport', signOutVisible);

    const tutorialFold = fresh.locator('[data-fold="tutorial"] .fold-toggle');
    if ((await tutorialFold.getAttribute('aria-expanded').catch(() => null)) !== 'true') {
      await tutorialFold.click().catch(() => {});
    }
    const exToggle = fresh.locator('[data-testid="explanations-toggle"]');
    if ((await exToggle.getAttribute('aria-expanded').catch(() => null)) !== 'true') {
      await exToggle.click().catch(() => {});
    }
    await fresh.waitForTimeout(300);
    const toggleText = (await exToggle.innerText().catch(() => '')) ?? '';
    const totalMatch = /(\d+) of (\d+) seen/.exec(toggleText);
    const seenRows = await fresh.locator('.explanations li').count();
    const locked = Number(
      (await fresh.locator('[data-testid="explanations-locked"]').getAttribute('data-count').catch(() => null)) ?? 0,
    );
    record(
      'Settings lists the seen explanations and only counts the rest',
      totalMatch !== null && Number(totalMatch[2]) > 0 && seenRows + locked === Number(totalMatch[2]),
      totalMatch ? `${seenRows} listed + ${locked} locked of ${totalMatch[2]}` : `no count in "${toggleText}"`,
    );

    await fresh.getByRole('button', { name: /^(Skip onboarding|Hide the tutorial)$/ }).first().click();
    await fresh.waitForTimeout(1500);
    const silenced = (await cue()) === null;
    record('onboarding can be switched off', silenced, silenced ? '' : 'the panel stayed after Skip');

    const back = fresh.getByRole('button', { name: 'Turn onboarding back on', exact: true });
    const offerable = (await back.count()) > 0;
    if (offerable) {
      await back.first().click();
      await fresh.waitForTimeout(2000);
    }
    const restored = await cue();
    record(
      'onboarding can be switched back on',
      offerable && restored !== null,
      offerable ? (restored ? `back at ${restored.id}` : 'the switch was there but nothing came back') : 'no way back offered',
    );

    // 6. WHAT THE SERVER KEPT, which is the half no unit test can see.
    //
    // Modul: the seen-set moved off localStorage on 2026-09-10 so a returning
    // player is not taught the whole game again on a second device. The unit
    // tests exercise that against a STUB server; this is the only place the
    // real round trip runs, and a route that silently 404s would look exactly
    // like a working feature from inside the browser - which is precisely how
    // the push token spent a day posting into nothing.
    const freshToken = await fresh.evaluate(
      () => sessionStorage.getItem('folkidle.token') ?? localStorage.getItem('folkidle.token'),
    );

    let serverSeen = null;
    if (freshToken) {
      const res = await fetch(`${API_BASE}/api/v1/player/onboarding-seen`, {
        headers: { Authorization: `Bearer ${freshToken}` },
      });
      serverSeen = res.ok ? await res.json() : null;
    }

    record(
      'the onboarding seen-set reaches the SERVER, not just this browser',
      serverSeen !== null && serverSeen.HasRecord === true,
      serverSeen === null
        ? 'the endpoint did not answer'
        : `HasRecord=${serverSeen.HasRecord}, ${serverSeen.Seen.length} id(s)`,
    );

    // Modul: and it agrees with what the BROWSER thinks. Two stores holding
    // one truth is this codebase's dominant bug class, and the failure here is
    // quiet in both directions - a server that is behind re-teaches, a server
    // that is ahead buries.
    const localSeen = await fresh.evaluate(() => {
      const playerId = Object.keys(localStorage)
        .find((key) => key.startsWith('folkidle.onboardingSeen.'));
      return playerId ? JSON.parse(localStorage.getItem(playerId) ?? '[]') : null;
    });

    const agree =
      Array.isArray(localSeen) &&
      serverSeen !== null &&
      localSeen.every((id) => serverSeen.Seen.includes(id));

    record(
      'the browser and the server agree on what has been shown',
      agree,
      localSeen === null
        ? 'nothing stored locally'
        : `local ${localSeen.length}, server ${serverSeen ? serverSeen.Seen.length : 0}`,
    );
  }

  // --- rebirth, pressed for real, on the throwaway (task 88) ------------------
  //
  // Modul: THE ONLY ACCOUNT THIS SCRIPT MAY REBIRTH. It is online (this page
  // holds its socket), so this drives the live path end to end: the tick
  // suspends and flushes the session, the rebirth runs as the flush's
  // continuation, and the reset payload is reloaded under the open socket.
  // Then the same preview is submitted again, which must be REFUSED rather
  // than rebirth twice - the double-tap guard, measured rather than assumed.
  {
    const token = await fresh.evaluate(
      () => sessionStorage.getItem('folkidle.token') ?? localStorage.getItem('folkidle.token'),
    );
    const call = async (method, path, body) => {
      const res = await fetch(`${API_BASE}${path}`, {
        method,
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body: body ? JSON.stringify(body) : undefined,
      });
      return { status: res.status, json: await res.json().catch(() => null) };
    };

    const before = await call('GET', '/api/v1/rebirth/preview');
    const count = before.json?.RebirthCount ?? -1;
    const first = await call('POST', '/api/v1/rebirth', { ExpectedRebirthCount: count });
    record(
      'a throwaway account is reborn, live',
      first.status === 200 && first.json?.Result === 'Ok' && first.json?.RebirthCount === count + 1,
      `HTTP ${first.status} ${JSON.stringify(first.json)}`,
    );

    const second = await call('POST', '/api/v1/rebirth', { ExpectedRebirthCount: count });
    record(
      'the same preview submitted twice is refused, not a second rebirth',
      second.status === 409 && second.json?.Result === 'AlreadyReborn',
      `HTTP ${second.status} ${JSON.stringify(second.json)}`,
    );

    const after = await call('GET', '/api/v1/rebirth/preview');
    record(
      'after the rebirth the preview reads level 1 and one rebirth more',
      after.json?.Level === 1 && after.json?.RebirthCount === count + 1 && after.json?.Gold === 0,
      JSON.stringify({ Level: after.json?.Level, RebirthCount: after.json?.RebirthCount, Gold: after.json?.Gold }),
    );
  }

  await context.close();
}

// --- icons actually loaded ---------------------------------------------------
{
  const broken = await page.evaluate(() =>
    [...document.querySelectorAll('img')].filter((i) => i.complete && i.naturalWidth === 0).length,
  );
  const total = await page.evaluate(() => document.querySelectorAll('img').length);
  record('artwork loads without broken images', broken === 0, `${total} images, ${broken} broken`);
}

// The friend-add probe deliberately looks up a username that does not exist,
// and /api/v1/players/resolve answers 404 for that - which is the CORRECT
// answer, handled correctly (the screen said 'No player called "..."'). The
// browser logs every non-2xx fetch to the console regardless, so filtering it
// out here is the difference between an assertion that means something and one
// that fails on a working feature.
// Modul: AND THE OPTIONAL HIT CLIPS.
//
// playHit asks for a per-weapon sound (combat_hit_magic.wav and friends) and
// falls back to the one generic clip when the file is not there - which it is
// not, because authoring a WAV is not something code does. The browser logs
// the 404 regardless, once per clip per session now that audio.ts remembers a
// miss. Expected, and the fallback is what makes it harmless.
// Modul: AND THE ADMIN PROBE'S 403.
//
// Every client asks /api/v1/admin/status whether this account may see the
// admin tools, because there is no other way for it to find out. An ordinary
// account is told no, which is the rule working - but the browser logs the 403
// as a console error just the same. The dev fixture IS an admin, so this never
// appeared until the onboarding section above started driving an account that
// owns nothing, which is exactly the class of thing the fixture hides.
const realErrors = consoleErrors.filter(
  (e) => !/status of 404/.test(e) && !/status of 403/.test(e),
);
record('no unexpected console errors', realErrors.length === 0, realErrors.slice(0, 3).join(' | '));
// The optional per-weapon hit clips 404 once each per session - see above -
// so they are counted separately from the one deliberate lookup miss rather
// than inflating a number that is supposed to mean "exactly one".
const audioMisses = missedUrls.filter((u) => u.includes('/audio/')).length;
const otherMisses = missedUrls.filter((u) => !u.includes('/audio/')).length;
record(
  'the only failed request is the deliberate unknown-player lookup',
  otherMisses <= 1,
  `${otherMisses} lookup 404(s), ${audioMisses} optional audio clip(s) absent${otherMisses > 1 ? ': ' + missedUrls.filter((u) => !u.includes('/audio/')).join(' ') : ''}`,
);
// Since 2026-09-27 every clip audio.ts names exists except the crit hit, which
// has never been authored. Any OTHER audio 404 is a clip that failed to reach
// the server's publish output - the way the whole game was once silent.
const unexpectedAudio = missedUrls.filter((u) => u.includes('/audio/') && !u.endsWith('/combat_hit_crit.wav'));
record('every audio clip the client asked for exists', unexpectedAudio.length === 0, unexpectedAudio.slice(0, 3).join(' | '));

await page.screenshot({ path: '/tmp/exercise-last.png', fullPage: true });
await browser.close();

const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
if (failed.length > 0) process.exit(1);
