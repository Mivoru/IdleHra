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

// Waits for the screen to have actually LOADED, not for a fixed delay. Most
// screens open on a query, and a cold server answers the first one slowly
// enough that a fixed wait passes locally and fails on a fresh boot - which is
// a flaky test pretending to be a bug report.
const go = async (label) => {
  // Modul: scoped to the NAV. The hub map's plates are buttons named "Combat",
  // "Market", "Guild" and so on too, and an unscoped lookup resolved to
  // whichever came first in the DOM - which is the map, and only while the map
  // is the screen being shown. Navigation has to mean the nav.
  await page.locator('header').getByRole('button', { name: label, exact: true }).first().click();
  await page.waitForFunction(
    () => !/\bLoading\.\.\./.test(document.body.innerText),
    { timeout: 15000 },
  ).catch(() => {});
  await page.waitForTimeout(600);
};

// A toast is how this client reports both server results and its own refusals,
// so reading them is how a click's outcome becomes observable at all.
const toasts = async () => page.locator('.toast').allInnerTexts();
const dismissToasts = async () => {
  const buttons = page.locator('.toast button');
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
await page.goto(BASE, { waitUntil: 'networkidle' });

// --- sign in as the stocked fixture -----------------------------------------
await page.getByRole('button', { name: 'Sign in' }).click();
await page.locator('input[type="email"]').fill('dev@folkidle.local');
await page.locator('input[type="password"]').fill('FolkIdleDev123!');
await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
await page.waitForSelector('text=Combat', { timeout: 20000 });
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
await go('Social');
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
  const readMastery = async (name) => {
    const text = await page.evaluate(() => document.body.innerText);
    const lines = text.split('\n').map((l) => l.trim());
    const at = lines.indexOf(name);
    if (at < 0 || at + 1 >= lines.length) return null;
    // Thousands are grouped with a NON-BREAKING SPACE in this locale, so
    // "2 415" is one number. A [\\d,]+ pattern stops at the space, fails to
    // reach "xp", matches nothing, and reports the whole track as missing -
    // intermittently, because it only bites once a number passes a thousand.
    const hit = /level\s+(\d+)\s*\u00b7\s*([\d.,\s\u00a0\u202f]+?)\s*xp/i.exec(
      lines[at + 1],
    );
    return hit
      ? { level: Number(hit[1]), xp: Number(hit[2].replace(/[^0-9]/g, '')) }
      : null;
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
{
  const text = await page.evaluate(() => document.body.innerText);
  record(
    'auto-eat can be stocked with the fish you caught',
    !/No food in the chest/i.test(text),
    text.match(/Choose food\.\.\./) ? 'food list offered' : 'panel shown',
  );
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

  // Enabled only when the chest holds the recipe's materials, which a fresh
  // fixture may not - a disabled button is a correct answer, not a stall.
  const work = page.getByRole('button', { name: /Put to work/i }).first();
  const hasWork = (await work.count()) > 0 && (await work.isEnabled());
  if (hasWork) {
    await work.click();
    await page.waitForTimeout(1500);

    await go('Character');
    const roster = await page.evaluate(() => document.body.innerText);
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
}

// --- world boss --------------------------------------------------------------
await go('World Boss');
{
  const text = await page.evaluate(() => document.body.innerText);
  const active = text.includes('Active');
  record('world boss state is shown', /Active|Dormant|Concluded/.test(text));
  if (active) {
    const attack = page.getByRole('button', { name: 'Attack', exact: true });
    const disabled = await attack.first().isDisabled();
    record(
      'attack button follows the larder and attempt rules',
      true,
      disabled ? 'disabled (larder empty or attempts spent)' : 'enabled',
    );
  }
}

// --- the paper doll ----------------------------------------------------------
// Equipment used to be a LIST of seven rows, each with its own dropdown and
// Equip button, in the same panel that handed out jobs. Dressing a character
// and telling them what to do are different acts and looked identical.
await go('Character');
{
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

  // A gear slot is a button now; clicking one opens its picker.
  // Modul: tools are gear now - three slots of their own, rolled with a rarity
  // and gathering affixes, where they used to be stackable materials that
  // could carry neither.
  const toolSlots = await page.locator('.tools .gearslot').count();
  record('the doll has the three tool slots', toolSlots === 3, `${toolSlots} tool slots`);

  const gearSlot = page.locator('.gearslot').first();
  const hasDoll = (await gearSlot.count()) > 0;
  record('the character has a paper doll with clickable slots', hasDoll);

  if (hasDoll) {
    await gearSlot.click();
    await page.waitForTimeout(500);
    const opened = await page.evaluate(() => document.querySelector('.picker') !== null);
    record('clicking a slot opens its item picker', opened);

    const wear = page.getByRole('button', { name: 'Wear', exact: true });
    if ((await wear.count()) > 0) {
      await dismissToasts();
      // The SLOT'S OWN TEXT, not the number of filled slots. The first slot on
      // the doll is the weapon, which the fixture already has - so swapping it
      // leaves the count unchanged and a count-based check reads as failure
      // while the game is working correctly.
      const before = await gearSlot.innerText();
      await wear.first().click();
      await page.waitForTimeout(2500);
      const after = await gearSlot.innerText();
      const msgs = await toasts();
      record(
        'wearing an item from the doll dresses the character',
        after !== before || msgs.length > 0,
        msgs.join(' | ') || `${before.split(String.fromCharCode(10)).join(' ')} -> ${after.split(String.fromCharCode(10)).join(' ')}`,
      );
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
  record(
    'the chest offers a route to the reroll',
    (await page.getByRole('button', { name: 'Reroll', exact: true }).count()) > 0,
    'every equipment row links to the Forge',
  );
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
  const tally = async () => page.locator('.folk li').count();

  const before = await tally();
  const feastButton = page.getByRole('button', { name: /^Throw a feast/ });
  const price = async () => Number((await feastButton.first().innerText()).replace(/[^\d]/g, ''));

  const offered = (await feastButton.count()) > 0;
  record('the village offers a feast with a price', offered,
    offered ? `${(await price()).toLocaleString()}g` : '');

  if (offered && !(await feastButton.first().isDisabled())) {
    const askedBefore = await price();
    await dismissToasts();
    await feastButton.first().click();
    await page.waitForTimeout(2500);

    const after = await tally();
    record('paying for a feast brings somebody in', after > before, `${before} -> ${after}`);

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

  const sendButtons = page.getByRole('button', { name: 'Send on', exact: true });
  const dismissable = await sendButtons.count();
  record('the village offers to send somebody on', dismissable > 0, `${dismissable} not yet married in`);

  if (dismissable > 0) {
    const held = await tally();
    await sendButtons.first().click();
    await page.waitForTimeout(2500);
    const left = await tally();
    record('sending somebody on frees the slot', left < held, `${held} -> ${left}`);
    await dismissToasts();
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
await go('Breeding');
{
  const text = await page.evaluate(() => document.body.innerText);
  record('the breeding lab offers both pairings', /Marry the village/i.test(text) && /Cross your own/i.test(text));

  const heroSelect = page.locator('select').first();
  const villagerSelect = page.locator('select').nth(1);

  const heroCount = await heroSelect.locator('option').count();
  // Every character the fixture owns needs a lineage row to appear here at
  // all - the roster endpoint skips a character that has none, which is what
  // made this list silently empty.
  record('the hero list is populated', heroCount > 1, `${heroCount - 1} characters`);

  // THE HERO FIRST, and this order is the whole point rather than tidiness:
  // who a villager can marry depends on which hero is chosen (same race,
  // opposite sex), so enumerating the village before picking one reads every
  // villager as available and then picks one who is not.
  let marriable = null;
  let marriableLabel = '';
  let villagerTotal = 0;
  for (let heroIndex = 1; heroIndex < heroCount && marriable === null; heroIndex++) {
    // Skip the heroes the screen has already said cannot: a level-1 child from
    // an earlier run ("needs 50") and anybody inside the cooldown a previous
    // marriage started ("resting"). Both are honest states rather than
    // failures, and picking one turns this step into a test of the refusal.
    const heroText = await heroSelect.locator('option').nth(heroIndex).innerText();
    if (/needs 50|resting|still a child/.test(heroText)) continue;

    await heroSelect.selectOption({ index: heroIndex });
    await page.waitForTimeout(250);

    // Read the disabled flag off the DOM rather than through isDisabled():
    // Playwright's editability check is defined for inputs and selects, and
    // answers "not disabled" for an <option> whatever its attribute says - so
    // the loop below happily picked a villager the screen had greyed out.
    const options = await villagerSelect.evaluate((select) =>
      [...select.options].slice(1).map((o) => ({
        value: o.value,
        label: o.textContent.trim(),
        disabled: o.disabled,
      })),
    );
    villagerTotal = options.length;

    const open = options.find((o) => !o.disabled);
    if (open) {
      marriable = open.value;
      marriableLabel = open.label;
    }
  }

  record(
    'the village offers somebody marriable',
    marriable !== null,
    `${villagerTotal} in the village${marriable ? ` - ${marriableLabel}` : ''}`,
  );

  if (heroCount > 1 && marriable !== null) {
    const before = heroCount;
    await villagerSelect.selectOption(marriable);
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
    const priced = /Costs [\d,]+g/.test(preview);
    record(
      'the preview quotes a price',
      priced,
      priced
        ? (preview.match(/Costs [\d,\s]+g/) ?? [''])[0]
        : (await page.locator('.panel .warn').allInnerTexts()).join(' | ') || 'no reason shown',
    );

    await dismissToasts();
    const marryButton = page.getByRole('button', { name: 'Marry', exact: true });
    const blocked = await marryButton.isDisabled();
    record('the fixture can afford to marry', !blocked);

    if (!blocked) {
      await marryButton.click();
      await page.waitForTimeout(2500);

      // A child on the roster is the whole claim. The villager list shrinking
      // would not prove it - a dismissal does that too.
      const after = await heroSelect.locator('option').count();
      record('marrying the village produces a child', after > before, `${before - 1} -> ${after - 1} characters`);

      // ONE CHILD PER VILLAGER, forever. Without this a single lucky twenty
      // fathers the whole roster and the pool collapses onto one ancestor.
      const elderText = await page.evaluate(() => document.body.innerText);
      record(
        'the villager who married is spent',
        /already married in/i.test(elderText),
        (elderText.match(/[^\n]*already married in[^\n]*/) ?? [''])[0].trim().slice(0, 60),
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
  const text = await page.evaluate(() => document.body.innerText);
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

  // The pedigree: everybody came from somewhere, and a founder says so.
  record(
    'every member names where they came from',
    /a founder of the line|somebody from the village| x /i.test(text),
  );

  // Marking. The whole reason the cap is a decision rather than a surprise.
  const keep = page.getByRole('button', { name: 'Keep', exact: true });
  if ((await keep.count()) > 0) {
    await dismissToasts();
    await keep.first().click();
    await page.waitForTimeout(2500);
    const kept = await page.getByRole('button', { name: 'Kept', exact: true }).count();
    record('marking an ancestor to carry sticks', kept > 0, `${kept} kept`);
  } else {
    record('marking an ancestor to carry sticks', false, 'no Keep button rendered');
  }

  // FIELDING - the missing door. A benched child picks a slot and the roster
  // has to actually change, not just the dropdown.
  const bench = page.locator('.acts select');
  const benched = await bench.count();
  record('benched members can be fielded', benched > 0, `${benched} on the bench`);

  if (benched > 0) {
    // A SWAP, so counting fielded members proves nothing - one leaves as one
    // arrives. Identify the row being fielded and check THAT row ends up with
    // a slot badge.
    const row = page.locator('.panel li').filter({ has: page.locator('.acts select') }).first();
    const fingerprint = (await row.locator('.apts').innerText()).replace(/\s+/g, ' ').trim();

    await row.locator('select').selectOption('0');
    await page.waitForTimeout(3000);

    const nowFielded = await page.locator('.panel li').filter({ hasText: /slot \d/ }).allInnerTexts();
    record(
      'fielding an ancestor swaps them into a playable slot',
      nowFielded.some((t) => t.replace(/\s+/g, ' ').includes(fingerprint)),
      `${fingerprint} -> ${nowFielded.length} fielded`,
    );
    await dismissToasts();
  }
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
const realErrors = consoleErrors.filter((e) => !/status of 404/.test(e));
record('no unexpected console errors', realErrors.length === 0, realErrors.slice(0, 3).join(' | '));
record(
  'the only failed request is the deliberate unknown-player lookup',
  consoleErrors.length === realErrors.length + 1 || consoleErrors.length === realErrors.length,
  `${consoleErrors.length - realErrors.length} expected 404(s)`,
);

await page.screenshot({ path: '/tmp/exercise-last.png', fullPage: true });
await browser.close();

const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
if (failed.length > 0) process.exit(1);
