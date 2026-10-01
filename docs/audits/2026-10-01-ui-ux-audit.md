# FolkIdle UI/UX audit, 2026-10-01

The consolidated report of the 2026-10-01 UI/UX audit of the web client
(`client_web/`) and the Android APK built from it. The work it produced is
**TASK_BOARD tasks 89-110** (`docs/TASK_BOARD.md`, section "Tasks 89-110").

Paths are relative to `client_web/src/` unless they start with
`client_web/`, `server/` or `android/`. Line numbers are from `main` at
bd12f46 (2026-09-30).

**Markers used below.**
- **(spec)** follows from the CSS or HTML specification and the code, not
  from a screenshot or a measurement. Confirm it in a browser before you
  build on it.
- **(device)** needs a real phone. Desktop Chromium cannot show it.
- **(fixture?)** may be an artefact of the dev fixture's seeded state.
  Check the fixture before you fix the UI.

**Owner decisions in force (2026-10-01).**
- The APK is **portrait-only** (branch `claude/ui-audit-portrait`, commit
  a3eac07). Tablets, unfolded foldables, split-screen and mobile web still
  rotate, because Android 16 ignores `screenOrientation` on large screens for
  apps that target API 36. So the ShieldWheel landscape query
  (`lib/ui/ShieldWheel.svelte:868`) and the landscape half of
  `check:safearea` stay.
- **A redesign is welcome where a screen clearly benefits.** The verdicts in
  section 5 use that licence.

---

## 1. Overall UI health

There is no score. Here is what holds and what is weak across the whole client.

**What is solid.**
- **Colour tokens.** `app.css:14-167` defines rarity, theme, brass and
  currency colours with a light "parchment" variant, and components use them
  heavily: `var(--border)` 187 times, `--text-dim` 168, `--accent` 116.
- **Number formatting.** `lib/ui/format.ts` cannot print NaN, Infinity, "-0"
  or "undefined". `Money.svelte` spells out "gold" once a value is compacted.
- **The phone geometry floor.** `check:clipping`, `check:overlap`,
  `check:touch` and `check:safearea` all return **0 findings at 360, 390 and
  412 px** on every destination they visit. Every fixed bottom layer reads its
  own safe-area inset.
- **Back button.** It uses AndroidX `OnBackPressedCallback`, so predictive
  back works at targetSdk 36. `resolveBackPress` already orders seven layers
  (`lib/net/backButton.ts:94-114`).
- **Some screens are already the model for the rest.**
  - The loot reveal for a new player: icon, "+13 DEF - helmet slot is empty",
    one-tap Wear.
  - Progress for a guest: the first open deed is boxed, and every deed has a
    Go button.
  - Inheritance: one action per card.
  - Larder's "Go fishing" empty state.
  - The Village upgrade button, which relabels itself "Maxed" or
    "Not enough".
  - Codex at 1440 px.
- **Long text.** Text inside a `.panel` wraps (`app.css:454-461`). A 60- or
  128-character message without spaces wraps correctly. A 20-character
  username overflowed nowhere except chat (section 4.A).

**What is systemically weak.** Six root causes produce most of the findings.
1. **No layer of shared primitives.** There is no shared Button, Modal, Tabs,
   ItemRow, Hint or QueryState. So:
   - `.tiny-btn` is defined 12 times;
   - "primary" means five different things, and nothing at all in two files;
   - there are nine modal implementations;
   - an item row has six different shapes;
   - the `.panel` base is copied into 29 files.
2. **On a phone, the action comes after the explanation.**
   - Combat's first Fight button is about 915 px down. Gathering's first
     Gather is about 990 px down.
   - The Forge's Fuse form is about 1,750 px down. Character's gear is about
     1,500 px down.
   - The Delve's gate is the seventh block, and the World Boss strike comes
     after eight lines of rules.
   - The Market's results are below about 650 px of filters.

   The cause is the same everywhere: a desktop grid collapses to one column
   in DOM order, and rules prose that is always expanded sits above the
   controls.
3. **Information exists only on hover.** 95 `title=` attributes, about 15 of
   them carrying something the player needs, such as why a Skill-tree node or
   a deposit is greyed out, or what the event chip does. A phone never shows
   them.
4. **Overlays and navigation are not coordinated.**
   - There is no z-index scale (16 values from 1 to 10000).
   - The chat dock's `backdrop-filter` traps the profile modal inside it.
   - Seven fixed notification layers are positioned on their own.
   - The back button misses What's New, the profile, the context menu and the
     shield wheel.
   - Scroll position is never reset or restored.
5. **States lie.** A failed query renders the empty branch: "Nothing here." in
   the Chest, "No ranked players yet." on the Leaderboards. Successes play the
   error tone. Gold prints as "150 kg". The achievement card shows the next
   tier's reward instead of the one just earned.
6. **The light (parchment) theme was never checked as a whole.** It shows:
   - a dark-on-dark profile modal;
   - a Delve screen in a hard-coded dark palette;
   - white-alpha card edges that vanish on parchment;
   - leaderboard tier colours of #ffd970 and #e2e8f0 on cream;
   - a red rarity glow that reads as an error.

   The screenshots in this audit are almost all of the parchment theme.

---

## 2. Method and evidence

**Five passes:**
1. **CSS and front-end architecture.** Read-only. A script tallied every
   `<style>` block and inline `style=` in the 91 `.svelte` files (about
   33.9k lines).
2. **Mobile and Android WebView.** Read the code, the Capacitor 8 sources in
   `node_modules`, the manifest, `styles.xml` and `capacitor.config.json`.
3. **Dynamic content and UI states.** Empty, error, loading, long text, big
   numbers, confirms.
4. **Mechanical evidence.** The four geometry checkers at widened widths,
   plus about 120 Playwright screenshots of the dev fixture (level 40, admin)
   and of fresh guests, plus long-name and long-message cases.
5. **Five visual reviews** of those screenshots, screen by screen (A1, A2, B1,
   B2, C).

**Checker runs** (local stack, signed in as the dev fixture):

| Checker | Widths | Result |
|---|---|---|
| `check:clipping` | 1500, 900, 390, 360, 412, 768, 1440 | 0 findings, 203/203 screen×width pairs, 29 destinations |
| `check:overlap` | 1500, 390, 360, 412, 768, 1440 | 0 findings, but **10 of 29 destinations are never measured** (see below) |
| `check:touch` (44 px) | 390, 360, 412, 768, 1440 | 0 at 360/390/412; **153 undersized controls at 768**, 158 at 1440 |
| `check:safearea` | 390×844 portrait + 844×390 landscape | 0 findings, 58 pairs |

**Limits of the evidence.**
- **The checkers have blind spots.**
  - `overlap-check` navigates with `navButton()`. So destinations that exist
    only in `OVERLAYS` are silently skipped:
    - Friends, Market, Guild and Leaderboards;
    - the shield wheel and Market cosmetics;
    - Supplies Boosts, and Bloodline Ancestors and Inheritance.
  - The checkers also exempt anything under a `position: fixed` element, so
    the "Do this next" coach bar never counts.
  - `check:clipping` never opens the chat dock, which is where the one
    measured zero-width collapse lives.
  - `check:safearea` judges the top band at rest, so it cannot see a sticky
    element that scrolls under the status bar.
- **768 px is real only if tablets are a target.** The 44 px floor applies
  only below `40rem` (640 px), so an APK on a portrait tablet (744-834 CSS px)
  gets desktop control sizes. This is decision 110a. The 1440 list is mouse
  desktop and only informational. The Settings sound-test rows are admin-only.
- **Full-page screenshots draw `position: fixed` layers at their viewport
  offset.** The tab bar, the chat pill and the coach can therefore appear
  mid-page. That is a capture artefact. It was verified on Community
  (`community-guest-390-scrolled-to-end-viewport.png`). Files ending
  `-viewport` and all `modal-*` files are viewport captures.
- **Lazy-loaded images** (`MonsterPortrait.svelte:43`, `loading="lazy"`)
  render as empty squares further down a full-page capture. This affects
  Combat portraits, Codex portraits and Wardrobe avatars. They are not
  reported as defects. Codex is worth one look on a phone (task 109).
- **Long names were tested only as the account username.** No screen except
  chat renders the username. Character and Map show *person* names, so a long
  person name on Character, in Orders headings and in Home's "Right now" rows
  is **untested**.
- **Not run:** `check:perf`, `npm run exercise`, and nothing on a physical
  phone.
- **Items still needing a phone (device):**
  - the exact rendering of the trapped chat profile;
  - the chat window and focused inputs with the soft keyboard up;
  - a status-bar scrim;
  - the splash-to-WebView white flash in the light system theme;
  - whether the shield wheel's top really shows the page underneath.
- **No console errors** appeared on any screenshot pass.

The raw reports and screenshots are session artefacts and are not committed.
Every finding below names the file and line, or the screenshot, it rests on.

---

## 3. Critical and high issues

**Critical:** the player is misled about loss, is stuck, or cannot do the
thing at all.

| # | Issue | Evidence | Task |
|---|---|---|---|
| 1 | **A failed load looks like an empty state.** On error the Chest says "Nothing here." The Leaderboards say "No ranked players yet." Social says "No guilds exist yet." Progression shimmers forever. 13 of 21 query-using routes never read `isError` | `Chest.svelte:751-754`, `Leaderboards.svelte:52-55,82-85,126-129`, `Social.svelte:249-252`, `Progression.svelte:92,119-121` | 93 (built) |
| 2 | **Leaving a guild is impossible.** Social tells players "Leave it first", but `LeaveGuildAsync` has no caller on either side | `Social.svelte:245-246`, `server/.../GuildManagementEngine.cs:331` | 94 |
| 3 | **A profile opened from chat is trapped inside the 356×414 chat window and clipped.** Measured, along with dark-brown text on near-black. The `backdrop-filter` makes the dock the containing block for fixed descendants | `lib/ui/ChatDock.svelte:156`, `Chat.svelte:409,424`, evidence item 2 | 90 |
| 4 | **A 20-character username crushes the chat message body to 0 px wide.** It wraps one character per line, 1,203 px tall | evidence item 1, `extreme-chat-longname-as-seen-by-fixture-390.png` | 90 |

**High.**

| # | Issue | Evidence | Task |
|---|---|---|---|
| 5 | Gold prints as "150 kg" / "1.24 Mg" at 27 `{formatNumber(x)}g` sites | `Market.svelte:363,440`, `Forge.svelte:497,556`, `Mailbox.svelte:91` and others | 91 |
| 6 | The Character equip picker renders every owned piece for a slot. One live account had 17,836 rows; the fixture has 7,550 axes | `Character.svelte:131-145,605-623` | 91 |
| 7 | One tap with no confirm kicks a guildmate or sends a villager away for good. Market Buy can double-submit | `GuildOps.svelte:136-148`, `VillageFolk.svelte:51-55`, `Market.svelte:177-183` | 92 |
| 8 | Changing screen keeps the old scroll position, and back does not restore it | `App.svelte:371-378,536-541` | 90 |
| 9 | Back does not close What's New, which appears after every OTA update | `backButton.ts:94-114`, `WhatsNew.svelte:46` | 90 |
| 10 | `overflow-x: hidden` on both `html` and `body` stops `position: sticky` working, so the disconnect banner scrolls away **(spec)** | `app.css:472-476`, `ConnectionNotice.svelte:153` | 89 (built) |
| 11 | A global `header` rule paints the app bar's wood beam on 17 in-panel `<header>`s. The Delve intro becomes illegible, and `header strong` turns body words into 1.15rem brass | `app.css:661,675,799,808` | 89 (built) |
| 12 | 15 undefined CSS variables. The Settings email error is not red, and the profile modal is dark-on-dark in the light theme | `Settings.svelte:613`, `PlayerProfileModal.svelte:199,216,247` | 89 (built) |
| 13 | On a phone the primary action sits below the first screen on Combat, Gathering, Forge, Character, Delve, World Boss and Market | section 4.H | 97, 98, 100-102, 105 |
| 14 | Chest rows carry no stats or affixes, so 5,638 pieces cannot be triaged. Gold is listed as a material with Sell all and Bin | `Chest.svelte:771-830,100-104` | 99 |
| 15 | The Ancestors page is 37,468 px tall on a phone: about 200 rows, most of them lost at the next rebirth | `Ancestors.svelte:182-247` | 104 |
| 16 | Settings is 7,417 px tall on a phone, and Sign out (the only one on a phone) is about 6,900 px down | `Settings.svelte:500-635`, `App.svelte:1063-1066` | 96 |
| 17 | On a phone, 21 of 26 destinations can only be reached from the top of a header that does not stick | `App.svelte:899-906`, `lib/ui/tabs.ts:6-12` | 95 |
| 18 | On desktop the tutorial coach ring outweighs the "you are here" state | `App.svelte:976-978,1146-1151` | 95 |
| 19 | The leaderboard's second line is truncated to "Mou…" at every width. Tier colours are invisible on parchment. Only the "Deepest" tab can open a profile | `Leaderboards.svelte:61,244-251`, `leaderboardTiers.ts:43-49` | 91 |
| 20 | Why a Skill-tree node, deposit or Sell is disabled lives only in `title` | `SkillsPanel.svelte:453-527`, `GreatWorks.svelte:98-99`, `Chest.svelte:413-416` | 92 |
| 21 | A new player's Crafting screen is 30 greyed cards (about 6,000 px) with no next step | `crafting-guest-390` | 101 |
| 22 | The World Boss has no name and no art. The plate selector looks primary but feeds only the secondary button | `WorldBoss.svelte:307-322,364-389,427-434` | 105 |
| 23 | The Delve is a hard-coded dark palette pasted on the parchment page | `Delve.svelte:527-781` | 105 |
| 24 | Market filters fill the first phone screen. The sell picker shows 4 of 5,625 items as grey slabs | `Market.svelte`, `ItemBrowser.svelte:157,258` | 102 |
| 25 | Community: the Friends tab is two-thirds guild browser. The Guild dashboard never shows the guild's name and opens on the locked Guild war | `Social.svelte:238-316`, `GuildOps.svelte:44,450-452` | 107 |
| 26 | "Welcome back" (OfflineSummary) shares z-index 50 with the TabBar, so the tab bar paints over it **(spec)**. It has no max-height, and any tap on the card dismisses it | `OfflineSummary.svelte:67-73,260,268-275`, `TabBar.svelte:100` | 90 |

---

## 4. Findings by root cause

Each finding appears once, with every location it was seen. The task number
is where it gets fixed.

### A. Overlays: stacking, containment and coordination

- **A1. The chat dock traps fixed descendants.** It has `backdrop-filter:
  blur(10px)` and `overflow: hidden` (`ChatDock.svelte:156,158,237`).
  - `PlayerProfileModal` (`Chat.svelte:424`) measures 356×414 at (17,299),
    clipped to the dock window.
  - `ContextMenu` (`Chat.svelte:409`) positions itself in viewport coordinates
    but lays out relative to the window, and clamps against the wrong box.
  - The dock is also a z-40 stacking context, so the modal's z-index 1000
    cannot rise above the TabBar.
  - Fix: portal both layers to `<body>` with PersonPicker's `portal` action
    (`PersonPicker.svelte:83`). → 90
- **A2. Zero-width chat body.** A 20-character name gives `button.who` 224 px
  of a 296 px row and leaves `span.text` 0 px wide (evidence item 1). Stacking
  the sender above the message removes the cause (the phone sketch is in
  section 8). → 90, with the redesign in 95
- **A3. PlayerProfileModal is a legacy look.**
  - It renders only 4 of the 11 equipment slots
    (`PlayerProfileModal.svelte:130-165`).
  - Item names are raw slugs (`name={item.BaseItemId}`, `:136`).
  - "Last Online" is shown even for a player who is online now (`:122`).
  - It has no `role=dialog` or `aria-modal`.
  - It uses a `#1e1e1e` surface over undefined `--bg-dark`/`--bg-surface`
    (`:199,216,247`), so it is dark-on-dark in the light theme.

  → 90
- **A4. There is no z-index scale.** 16 values run from 1 to 10000
  (audit-css B1 has the full table).
  - OfflineSummary's backdrop and the TabBar are both z 50, and the TabBar
    comes later in the DOM (`App.svelte:800` vs `:809`), so the tab bar
    paints over "Welcome back" **(spec)**.
  - GuidedOverlay (70-72) paints under PlayerProfileModal (1000) and
    PersonPicker (1400).
  - `.panel`'s `folk-rise` animation uses `fill-mode: both`, which keeps a
    stacking context alive for good (`app.css:702-703`). PersonPicker was
    already trapped by it once.

  → 90 for the bugs, 106 for the scale
- **A5. Seven fixed notification layers with no coordinator.** Toasts,
  AchievementToast, LootReveal, the WhatsNew chip, OnboardingCoach, the
  ChatDock and ConnectionNotice each place themselves.
  - On a phone (≤30rem), AchievementToast uses `bottom: 5rem` with no
    `--tabbar-h` or `--sa-bottom` (`AchievementToast.svelte:188-194`). It
    overlaps the tab bar on gesture-inset phones, and lands on the coach bar
    (`modal-achievement-toast-fixture-390.png`).
  - On a phone, two stacked Toasts reach about 5.4rem and cover LootReveal
    (top 4.5rem, z55 < z60).
  - The loot reveal plus the "New record" toast cover the whole header,
    including Menu (evidence item 5).
  - The coach bar sits over the second Chest row and its buttons at rest
    (evidence item 4). On desktop it covers Wiki and Community text.
  - Nothing is queued: `achievementToasts` shows every card at once
    (`stores/game.ts:683`).

  → 90
- **A6. OfflineSummary can be taller than the screen.** Its `.card` has no
  max-height or overflow (`OfflineSummary.svelte:268-275`). The worst case is
  about 650-750 px of content on a 640 px screen. The backdrop is
  `role=button` with `onclick=dismiss` and the card does not stop
  propagation, so a tap meant to read the table closes it. → 90
- **A7. Modals ignore safe areas and are sized in `vh`.**
  - DeathCard has no max-height or overflow (`DeathCard.svelte:141-159`).
  - VictoryCard, OfflineSummary, PlayerProfileModal and the exit confirm
    (`App.svelte:1168`) do not read `--sa-*`.
  - Three idioms for the same inset coexist.
  - `vh` caps appear at `VictoryCard:140`, `WhatsNew:111`,
    `PlayerProfileModal:204`, `PersonPicker:435`, `ChatDock:148` and
    `ShieldWheel:889`. There is no `dvh` anywhere, which is wrong on mobile
    web.

  → 90 for the inset and dvh, 106 for a shared Modal
- **A8. The shield wheel overlay.**
  - There is no way out during play. Close exists only in the result and
    error phases (`ShieldWheel.svelte:516,548,557`).
  - The legend shows only during the countdown (`:419`).
  - The header and part of the World Boss panel render above the
    `position: fixed; z-index: 60` overlay (cause not pinned down; may be
    partly a capture artefact) **(device)**.
  - About 180 px of empty space sits under the wheel, and the segment numbers
    are drawn upside down (`world-boss-shield-wheel-guest-390.png`).
  - "Tap to throw" reads as a file-drop zone.

  → 90 for back, 105 for the rest

### B. Back button, Escape, focus and scroll

- **B1. Back skips several layers.**
  - What's New: back acts behind it (`backButton.ts:94-114`). It shows after
    every OTA update (`capacitor.config.json`, `autoUpdate: "atInstall"`).
  - PlayerProfileModal is deliberately excluded (`App.svelte:491-497`).
  - The Chest/Chat ContextMenu has no back hook.
  - The shield wheel unmounts mid-run (`WorldBoss.svelte:510-522`).
  - Fix: turn `openSheetCloser` (`stores/sheet.ts`, one slot, used by
    PersonPicker) into a stack. → 90
- **B2. Only ContextMenu and PersonPicker answer Escape.** OfflineSummary
  does so nominally. Nothing moves focus into a modal, traps it or restores
  it, and `inert` is used nowhere. (`.focus(` appears only at
  `App.svelte:327,352` and `ContextMenu.svelte:49`.) → 90 for Escape through
  the closer stack, 106 for focus in the Modal
- **B3. Scroll is never reset or restored** (`App.svelte:371-378,536-541`).
  Revisited screens render synchronously from `loadedScreens`, so they keep
  the previous `scrollY`. → 90
- **B4. Tapping the TabBar leaves the phone Menu open** (`App.svelte:809`).
  → 90

### C. Viewport, keyboard and insets

- **C1. `overflow-x: hidden` on `html, body` disables sticky** **(spec)**.
  `app.css:472-476` affects ConnectionNotice and the Wiki sidebar. Also
  recorded in `client_web/CLAUDE.md`: "the Wiki's sidebar is declared sticky
  and at 390px never sticks". Fix: `overflow-x: clip`. → 89 (built)
- **C2. The ConnectionNotice sticks at `top: 0`.** Once it sticks, that is
  under the status bar (`ConnectionNotice.svelte:153-154`). → 90
- **C3. With the keyboard up, the chat window's header and close button slide
  under the status bar.** `.window { height: min(26rem, calc(100vh - 8rem)) }`
  (`ChatDock.svelte:148`). At a viewport of about 350 px the top lands at
  about -2 px **(device)**. Capacitor 8 `SystemBars` does shrink the WebView
  for the IME (`SystemBars.java:203-217`). → 90
- **C4. The tab bar, chat handle and coach stay pinned above the keyboard.**
  No `scroll-padding` exists, so a focused Market price or guild donation
  field can sit behind the tab bar **(device)**. Fix: an `html.typing` class
  that hides the bottom chrome, plus `scroll-padding-bottom`. → 90
- **C5. Mobile web and the APK disagree on keyboard resizing.** There is no
  `interactive-widget` in `index.html:5`. → 89 (built)
- **C6. There is no status-bar scrim**, so scrolled content passes under the
  system icons **(device)**. → 90
- **C7. Keyboard hints are missing.**
  - The chat input has no `enterkeyhint="send"` (`Chat.svelte:381-389`).
  - The whisper and login username fields have no `autocapitalize="none"`,
    `autocorrect="off"` or `spellcheck="false"` (`Chat.svelte:374`,
    `Login.svelte:141`).

  → 90

### D. Touch and WebView polish

- **D1. Sticky hover.** There are 25 `:hover` rules and no
  `(hover: hover)` guard (`app.css:244,585-590,918-922`, `Hub.svelte:161` and
  others). → 89 (built)
- **D2. WebView defaults are left on.** No `-webkit-tap-highlight-color`.
  Pull-to-refresh reloads the SPA on mobile web (no `overscroll-behavior`).
  iOS Safari zooms into 14 px inputs. → 89 (built)
- **D3. `background-attachment: fixed` with four layers, two of them SVG
  turbulence** (`app.css:530-537`). Chromium repaints it on every scroll
  frame, which is a plausible contributor to the Chest scroll long tasks. →
  89 (built)
- **D4. Android shell.**
  - Likely white flash between the splash and first paint
    (`styles.xml:12-16` has no `windowBackground`; there is no
    `backgroundColor` in `capacitor.config.json`) **(device)**.
  - theme-color is hard-coded dark (`index.html:24`).

  → 89 (built)
- **D5. Breakpoints are per file.** There are seven width breakpoints
  (`40rem` ×15, `52rem`, `30rem`, `46rem`, `64rem`, `560px`, `600px`), and
  `PersonPicker.svelte:63` re-declares `NARROW_QUERY`. Between 35 and 40rem,
  Chest and Settings keep desktop layout while the global phone rules are
  already on. → 89 (built)
- **D6. The 44 px floor exists only below 40rem.** At 768 px there are 153
  controls under 44 px:
  - header toggles 32 px tall;
  - Chest `More` 32×22;
  - Supplies `+`/`−` 24×26;
  - Ancestors trait buttons 18 px tall.

  → decision 110a
- **D7. TabBar `.tab` sets `box-shadow: none`** (`TabBar.svelte:129`), so the
  most-used control on a phone loses its pressed feedback. → 106

### E. Information that exists only on hover

- **E1. Disabled reasons and key facts are only in `title`:**
  - Skill-tree buy and respec (`SkillsPanel.svelte:453,479,501,526`).
  - Great Works deposit (`GreatWorks.svelte:98-99`, whose own comment says
    "never a silent grey").
  - Chest menu Sell (`Chest.svelte:413-416` via `ContextMenu.svelte:93`).
  - Inheritance "Needs N diamonds" (`Inheritance.svelte:111-112`).
  - The first-clear chip and hunting estimate (`Combat.svelte:674,680`).
  - SessionLoot Wear (`SessionLoot.svelte:232`).
  - The Village missing requirement (`Village.svelte:213`).
  - Gathering caveats (`Gathering.svelte:336-342`).
  - Chest "Locked" (`Chest.svelte:789`).
  - The non-interactive TraitBadge (`TraitBadge.svelte:33`).

  → 92
- **E2. The event chip on a phone shows only "Diamond Star".** The effect is
  hidden and left in `title` (`EventBanner.svelte:55,87-98`). On desktop it
  reads "Active Event: Diamond Star 5% off fusion fees". → 92
- **E3. Unlabelled numbers.** The four aptitude numbers in VillageFolk and
  Ancestors are labelled only by `title` (`VillageFolk.svelte:122-125`,
  `Ancestors.svelte:205`). Ancestors adds a fifth "16 / 200" total chip. →
  92 for the labels, with 103 and 104 for the row layouts
- **E4. Disabled with no reason anywhere.**
  - Crafting "Put to work" (`Crafting.svelte:227-233`).
  - Ancestors "One more slot" (`Ancestors.svelte:152`).
  - Social Join, full or already in a guild (`Social.svelte:271-275`).
  - Forge Fuse: 6 conditions, only the gold one shown
    (`Forge.svelte:504-511`).

  Social Join is also **enabled** for a level-1 guest on a "lv 20+" guild,
  because it ignores `MinApplicationLevel` (`Social.svelte:268-270`). The good
  patterns to copy are Village's relabelled button (`Village.svelte:210-226`)
  and LootReveal's inline requirement (`LootReveal.svelte:84-91`). → 92

### F. Feedback, confirmation and pending state

- **F1. Five confirmation styles, and none at all where it matters.**
  - Native `confirm()` for attribute respec and high-rarity reroll
    (`AttributePanel.svelte:54`, `Forge.svelte:403`).
  - Inline two-step for Rebirth, skill respec, sweep and bin.
  - A 5 s undo for Chest sell.
  - A typed phrase for account purge.
  - **None** for Guild Kick (`GuildOps.svelte:136-148,837`), villager
    "Send on" (`VillageFolk.svelte:51-55,131-137`), Ancestors "One more slot"
    (diamonds, `Ancestors.svelte:63-67,152`), and friend Remove and Block
    (`Social.svelte:222-225`).

  → 92
- **F2. WebSocket buttons have no in-flight state.** Market Buy, Mailbox
  Claim, Forge Fuse, Crafting Craft and Send on can double-submit, giving a
  success followed by "Target not found." Village's `pendingId`
  (`Village.svelte:212`) is the good example. → 92
- **F3. Successes in the error tone.** `pushLocalNotice` defaults to
  `'error'` and plays the error sound (`stores/game.ts:392,411`). Affected:
  - "Member kicked/promoted/demoted" (`GuildOps.svelte:141,155,169`);
  - "Friend request sent", "Player blocked" (`Chat.svelte:170,179`);
  - the Book of Deeds seals and diamonds (`BookOfDeeds.svelte:60,75-77`).

  → 91
- **F4. The achievement card shows the next tier's reward.**
  `reward: row.NextTierReward` is read from the snapshot after the crossing
  (`stores/game.ts:677`; the server's `AchievementMilestones.cs:239-254`). So
  the top tier shows no reward at all. → 91
- **F5. The loot reveal announces one drop twice.** The "New record" toast
  sits on top of the reveal on a phone, and in another corner on desktop. →
  109
- **F6. Loot reveal comparison.** "Same rarity as yours" sits next to a
  primary Wear, because `lootCompare.ts` ignores affixes (`:15,78-90`). → 109

### G. Money, numbers and pre-snapshot state

- **G1. `{formatNumber(x)}g` at 27 sites prints "150 kg".** Market 10, Forge
  7, Chest 2, Mailbox 2, Wiki 2, and one each in Breeding, VillageFolk and
  ChildPreview. `Money.svelte` is used at only 21 sites. Separately, 48 ad-hoc
  formats lose Money's colour and affordability styling. → 91
- **G2. Raw quantities and stale caps.**
  - `x{qty}` (`Boosts.svelte:152`, `Mailbox.svelte:118`,
    `SessionLoot.svelte:224`, `GuildOps.svelte:623`).
  - "131m of 120m" (`Boosts.svelte:191`; a second copy of `MAX_BUFF_TICKS`).
  - A stale "COMPACTED FROM A MILLION" comment (`Money.svelte:62`; the real
    threshold is 100,000).
  - Big have/need counts in Crafting read as two numbers ("0/1 226").

  → 91, with Crafting in 101
- **G3. Mailbox shows "Your backpack is full" before the first snapshot.**
  `?? 0` at `Mailbox.svelte:26-27`. It also disables claims, and the warning
  can never fire afterwards, because the server pins the value
  (`InventoryCensusTickCoordinator.cs:44-59`). → 91
- **G4. Pre-snapshot copy differs per screen.** It is "Waiting for state…" in
  three variants, and the header prints the raw connection phase
  (`App.svelte:729-733`). → 91
- **G5. Guild name: 1-100 characters, no charset rule, no client
  `maxlength`** (`Social.svelte:242`, `GuildManagementEngine.cs:89`). It is
  interpolated into toasts that do not wrap (`Toasts.svelte:54-66`). → 91

### H. On a phone, the action comes after the prose

The same shape on many screens: a desktop grid collapses to one column in DOM
order, and static rules text sits above the controls.

| Screen | First primary action at 390 px | Above it | Task |
|---|---|---|---|
| Combat | first Fight about y 915 (guest about y 1060) | "Not in combat." panel, empty Loot panel, 6-line rules | 98 |
| Gathering | first Gather about y 990 | Mastery table, Speed and yield table, empty haul card | 101 |
| Forge | Fuse form about 1,750 px down | about 75 "Ready to fuse" chips | 100 |
| Character | gear slots about 1,500 px down | 4 attribute cards (about 1,000 px) | 97 |
| Crafting (fixture) | recipes below about 850 px | Workshop commissions panel | 101 |
| Delve | "Pay and descend" about y 865, 7th block | stats grid, 2 banners | 105 |
| World Boss | strike about y 640 | 3 paragraphs incl. an 8-line armour rule | 105 |
| Market | results about y 1060 | about 650 px of checkbox filters | 102 |
| Village | buildings about 1,050 px down | gene-pool prose and list | 103 |
| Bloodline Breeding | Breed button two screens down | 5 blocks of rules prose | 109 |
| Progress (fixture) | first open deed about 900 px down | 12 struck-through finished deeds | 109 |
| Wiki | article about 1,300 px down | 16-entry table of contents | 109 |

### I. No shared primitives

- **I1. Buttons.**
  - `.tiny-btn` is defined 12 times in three variants. In
    `AutomationRulesPanel.svelte` and `Forge.svelte` it is used undefined.
  - "primary" has five meanings and is **dead** in `LootReveal.svelte:91` and
    `CosmeticMarket.svelte:212`, where it is the Wear call to action.
  - "danger" has three meanings (`RebirthPanel:196` uses `--warn`).
  - Primary looks like secondary on Combat Fight, World Boss Strike, Crafting
    Craft (`class="tiny-btn primary"`, `Crafting.svelte:220`), the Login
    buttons, the Register Back/Create pair and the promo's Not now/Download.

  → 106, with screen-level uses in their tasks
- **I2. Modals.** There are nine implementations: DeathCard, VictoryCard,
  WhatsNew, OfflineSummary, PlayerProfileModal, PersonPicker, the exit
  confirm, the Login promo and ShieldWheel. → 106
- **I3. Tabs and chips.**
  - 4 `role=tablist`s with 4 class names, plus non-ARIA tabs in Chat, HomeCards
    and Character, 2 chip groups and 3 `.filters` copies.
  - Four different "active" looks, one of them a leftover slate-blue tint
    (`Character.svelte:825`).
  - Nested tab rows look identical: Community/Market, and
    Leaderboards Standing/Deepest.
  - Progress tabs wrap to two rows at 390 px.

  → 106
- **I4. Item rows have six shapes:**
  - the Chest row;
  - the ItemBrowser grey slab (`ItemBrowser.svelte:258`; the fill comes from
    a global button style, to confirm in devtools);
  - the Mailbox "[Epic]" inline;
  - the fusion chip;
  - the Crafting card with no icon;
  - the Larder slot with no icon.

  → 106, adopted in 99-102
- **I5. Bars and panels.**
  - `Bar.svelte` is used 16 times, against 11 or more hand-rolled bars.
    GoldLedger's `.bar` collides with the global class, and Bar has no
    `aria-label`.
  - Progress and Codex meters have no visible track.
  - The `.panel` base is copied into 29 files. Two copies use an undefined
    `--panel`.
  - Panels with corner ornaments let footer text run onto the bracket
    (Village gene pool, Bloodline aptitude panel).

  → 106
- **I6. Headings.** SessionLoot's bare `<h2>` ("Loot drops") renders larger
  than its host card's title in Combat and Gathering
  (`SessionLoot.svelte:167`). → 106, and 98/101 when those screens are rebuilt
- **I7. Inner scrollers inside a scrolling page** catch the thumb on Android.
  Each is cut mid-row with no cue:
  - SessionLoot `16rem` ×2 (`SessionLoot.svelte:306-307`);
  - Chest materials `26rem` (`Chest.svelte:985-997`);
  - VillageFolk `28rem` (`VillageFolk.svelte:217-218`);
  - ItemBrowser compact `14rem` (`ItemBrowser.svelte:157`).

  → 98, 99, 100, 102, 103

### J. Theme and contrast (parchment)

- **J1. Hard-coded colours.** 254 outside `app.css`.
  - The Delve has a private dark palette (`Delve.svelte:529-781`, 34
    literals, its own `button.primary`).
  - 22 white-alpha borders vanish on parchment: Character, Forge, Market,
    ChatDock, Mailbox, OfflineSummary, Wiki.
  - The slate-blue selection tint is still live (`Character.svelte:825`).
  - Text on accent is coded three ways.
  - Three different "bad" reds (`Market.svelte:967,993`,
    `Character.svelte:790`, `ChatDock.svelte:264`).

  → 105 for the Delve, 106 for the rest
- **J2. Colours tuned for dark, used on cream.**
  - Leaderboard tiers #ffd970 and #e2e8f0 (`leaderboardTiers.ts:43-49`).
  - Guild "1st/2nd place" #f0c040 and #c0c0c0, about 1.6-1.8:1
    (`GuildOps.svelte:1077-1079`).
  - The World Boss HP label is dark on a dark orange fill.
  - Codex kill bars use `--rarity-6` purple with the label straddling the
    fill (`Codex.svelte:162`).
  - Region banner titles are dim gold on dark paint.
  - Locked Wardrobe rarity labels sit at 45-60 % opacity.

  → 91 (leaderboard tiers, guild medals), 105, 109
- **J3. The rarity glow reads as an error on parchment.** A red text halo on
  Ancient items appears in the Chest, Character, the loot reveal and
  SessionLoot. Animated `text-shadow` per row is also a GPU cost (section
  4.L). → 108 for the animation, 99 for the row look; the colour choice is
  decision 110f
- **J4. 14 rarities are hard to tell apart by colour.** Normal and Common are
  both grey; Rare and Ultra Rare both blue; Epic, Legendary and Mythic are
  three close purples (`app.css:141-154`). → 100

### K. Typography

- **K1. 37 font-size lengths on two bases.** Body is 14 px but rem is 16 px.
  23 declarations are below 0.7rem. The ones that matter:
  - item stack and tier badges at 9.3 px (`ItemIcon.svelte:124,151,164`);
  - tab-bar labels at 10.9 px (`TabBar.svelte:168`);
  - nav group labels at 0.6rem, 65 % opacity (`App.svelte:926-933`);
  - PersonPicker at 8.8 px (`PersonPicker.svelte:331`);
  - Map disc labels at a clamp floor of 0.42rem, about 6.7 px
    (`Hub.svelte:185`);
  - Skill-tree branch labels at about 6 px.

  → 109 for the visible ones, 106 for the sweep
- **K2. Unequal weights and line heights.** `650` ×2
  (`Inheritance.svelte:185,189`) and `line-height: 1.1rem` as a length. →
  106

### L. Phone GPU budget

- **L1. `.rarity-glow` animates `text-shadow` (2.2 s, infinite) on every
  tier ≥10 row** (`app.css:304`, `rarity.ts:100`): Chest VirtualList,
  Character, Mailbox, SessionLoot and Forge. → 108
- **L2. `CosmeticFrame` animates `filter: drop-shadow` on every avatar**
  (`CosmeticFrame.svelte:65`): every chat message, the leaderboards, GuildOps
  and WorldBoss. → 108
- **L3. The chat handle has `backdrop-filter: blur(8px)`, always on screen
  over scrolling content** (`ChatDock.svelte:249`). → 108
- **L4. A permanent `will-change: transform, filter` on the monster sprite**
  (`Combat.svelte:786`). → 108

### M. Naming and copy

- **M1. One concept, several names:**
  - Bloodline vs Breeding/Ancestors/Inheritance;
  - Community (group) vs Community (entry) vs the Friends tab;
  - Supplies vs Auto-Eat vs larder (the first tutorial step says "Go to
    Auto-Eat");
  - The Delve vs the Deep;
  - "midnight UTC" on World Boss vs "Monday, 02:00 CEST" on Delve, for the
    same instant;
  - Map vs Home.

  → 95
- **M2. Copy written for maintainers, shown to players:**
  - Codex: "Only the 25 canonical monsters appear…" (`Codex.svelte:77`).
  - Character Work: "…used to share one row" (`Character.svelte:656`).
  - Larder: "Running out no longer stops you" (`Larder.svelte:178`).
  - Boosts:
    - "backpack… bank… withdrawn" (`Boosts.svelte:125-127`), which the
      file's own comment at `:60-70` says is no longer true;
    - "refused by disconnecting you" (`:131-135`).
  - Mailbox: "the server filters out…" (`Mailbox.svelte:67-71`) and
    "Claim up to 10".
  - Market: "flushes your state to the database" (`Market.svelte:593`) and an
    empty-state sentence that blames filters when none are set
    (`:338-341`).
  - Settings: raw i18n keys, "Only 30 keys exist", "30/30", cue ids,
    "interlock…save generation", "Player #1 / Last save 0s ago".
  - Chat: "128 bytes".
  - Gathering: "No gathering nodes in the content files.".
  - Codex: "No region requirements are defined.".

  → each in its screen's task; the rest in 109
- **M3. Copy that contradicts itself or the game:**
  - Guild gold donation "raises your contribution ranking" vs "only material
    contributions count".
  - Delve: "earn Lamplighter" above an owned Lamplighter button.
  - Ancestors: "Kept" on rows that will be deleted.
  - The Breeding Inn roll cap "up to 9" vs newcomers at 10 **(fixture?)**.

  → 107, 105, 104, 109

### N. New-player experience (guest)

- **N1. The guest sees "you would die" on every Sunlit Plains monster and a
  red "Your larder is empty" banner** (`combat-guest-390.png`). Check whether
  that is honest for a level-1 character on the first monster. The
  2026-09-02 "entrance closed" incident is the precedent. → 109
- **N2. Locked features are offered.**
  - The Map shows Market and Guild hotspots, which unlock at level 10.
  - The Chest menu offers "Reroll in Forge" (the Forge unlocks at level 5).
  - Community shows enabled Join and Create under a "Guild · Level 10" tab
    lock.

  → 109 and 107
- **N3. The tutorial reappears after a skip on a new browser context**
  (evidence item 6). Possibly the skip is stored per browser. Not verified in
  code. The guided card and the coach pill also show at once, giving one
  instruction three ways. → 109
- **N4. Empty states that are dead ends:**
  - Crafting (30 grey cards);
  - Skill tree (only "Respec(free)" looks live);
  - Boosts ("You are not carrying any consumables.");
  - Wardrobe (four "0" chest cards);
  - Great Works (10 disabled "(0 held)" deposits);
  - Orders (12 dead controls);
  - Settings (27 explanations spoiled at once);
  - Home "Next unlock" (endgame gear wording at level 1).

  → 101, 109, 103, 97, 96
- **N5. World chat showed "Nothing in this channel yet"** to an account that
  signed in again about a minute after posting two messages (evidence item
  7). This may be by design (live-only chat). → decision 110e

---

## 5. Screen-by-screen verdict

**good** = leave it. **polish** = targeted fixes. **redesign** = change the
structure.

| Screen | Verdict | Top issues | Task |
|---|---|---|---|
| Map / Home | polish | disc labels about 7 px; "Next unlock" jargon on day one; who "Brennus" is; desktop card stretch | 109 |
| Combat | **redesign** (phone), polish (desktop) | Fight about 915 px down; drop table renders off-screen above the tapped card; card outweighs Fight; ragged rows; desktop third-width list | 98 |
| World Boss | **redesign** | no name or art; rules before strike; plate picker feeds only auto-strike; red "Active" | 105 |
| Shield wheel | polish | no leave during play; no legend; page shows above it **(device)**; upside-down numbers | 90, 105 |
| The Delve | **redesign** | dark palette on parchment; gate is the 7th block; Delve vs Deep; Titles contradiction | 105 |
| Skill tree | polish | "40 points" has no verb; guest dead screen; "Respec(free)" spacing; bare "—"; 6 px branch labels; desktop width | 109 |
| Progress | polish | sealed chapters expanded; tabs wrap; meters without track; desktop width | 109 |
| Codex | polish | developer note; what a level gives is never said; region block costs a phone screen; purple kill bar | 109 |
| Character | **redesign** (phone) | gear about 1,500 px down; no name or level; slot switcher only in Equipment; dead Orders; changelog copy | 97 |
| Wardrobe | polish | 4 empty chest cards first; locked tiles say nothing about how to get them; faded rarity labels | 109 |
| Login | redesign (cheap) | three identical buttons, no pitch; promo before first play, Download looks secondary | 109 |
| Register | good | Back weighs as much as Create; no username hint or show-password | 109 |
| Chest | **redesign** (rows) | no stats or affixes; Gold as a material; worn looks like loose; nested scroller; desktop 40 % empty | 99 |
| Chest item menu | polish | Sell shows no price; no Inspect; header lacks rarity | 99 |
| Crafting | **redesign** | new player: 30 grey cards; locked looks like missing; ragged Craft column; "Slot 1"; commissions first | 101 |
| Forge | **redesign** (Fusion) | a chip tap fills a form 1,750 px away; unbounded chip wall; explainer in the wrong panel; picker shows 4 of 8; unlabelled sort | 100 |
| Gathering | **redesign** (order) | Gather about 990 px down; status says where, not what; only slot 1; inverted headings; stray "·" | 101 |
| Supplies: Auto-Eat | polish | flow runs bottom to top; no heal amounts or icons; desktop slider browser-blue; "Applied (50)" looks disabled | 109 |
| Supplies: Boosts | polish | wrong backpack/bank copy; empty state is a dead end; threat copy | 109 |
| Mail | polish | developer explainer; empty state says nothing; false backpack-full; "Claim up to 10" | 91, 109 |
| Loot reveal | polish | double announcement; "same rarity" next to a primary Wear; glow | 109 |
| Achievement toast | polish | wrong tier reward; lands on the coach bar; bare "III"; not a link | 91, 90, 109 |
| Village | **redesign** (order) | two unlabelled population fractions; prose first; nested gene-pool scroller; half-width buttons; Great Works about 1,500 px | 103 |
| Market: Equipment | **redesign** | filters fill the first screen; sell picker 4 of 5,625 as slabs; List button does not name the item; no My orders; developer copy | 102 |
| Market: Cosmetics | polish | sub-tabs look like top tabs; sell tiles show only a name | 102 |
| Bloodline: Breeding | polish | five blocks of rules prose; "Choose up to 1" with checkboxes; unexplained mark tick | 109 |
| Bloodline: Ancestors | **redesign** | 37,468 px; carried mixed with lost; "Kept" on doomed rows; per-row buttons ×200; diamond button on top | 104 |
| Bloodline: Inheritance | good | no current or max effect; desktop orphan card | 109 |
| Community / Friends | **redesign** | two-thirds guild browser; Join ignores min level; own-guild Join; mixed lock signals | 107 |
| Guild | **redesign** (order) | no guild name; locked Guild war first; 1st/2nd place invisible; contradicting rank copy | 107, 91 |
| Leaderboards | polish | "Mou…" grid bug; tier colours; no profile from Standing; no own rank; error shown as empty | 91, 93 |
| Wiki | polish | phone TOC 1,300 px before the article; 150-character lines on desktop | 109 |
| Settings | **redesign** | 7,417 px; Sign out at the bottom; 27 open explanations; developer text | 96 |
| Navigation | **redesign** (phone), polish (desktop) | Menu only at the top; 5 of 26 in tabs; active group weaker than the coach ring; name mismatches; two chat entries | 95 |
| Chat dock | **redesign** (phone) | profile and menu trapped; zero-width body; about 130 px message area; Guild tab for the guildless | 90, 95 |
| Player profile | polish | 4 of 11 slots; slugs; no dialog role; dark-on-dark | 90 |
| Offline summary | polish | under TabBar **(spec)**; no max-height; tap anywhere dismisses | 90 |
| Tutorial overlays | polish | coach and guided card together; reappears after skip; coach covers rows | 90, 109 |

---

## 6. Mobile and Android specifics

**Shell (verified in code):**
- The viewport meta is `width=device-width, initial-scale=1.0,
  viewport-fit=cover`. That means no 300 ms delay and no double-tap zoom.
- `AndroidManifest.xml:28-35` sets portrait (on branch
  `claude/ui-audit-portrait`) with no `windowSoftInputMode`. targetSdk is 36.
- The launch theme is `Theme.SplashScreen`. After launch it switches to
  `AppTheme.NoActionBar` with no dark window background, which is the likely
  white flash **(device)**.
- **Capacitor 8 `SystemBars` injects `--safe-area-inset-*` and, with the IME
  up, pads the WebView by the IME height**. So the layout viewport shrinks
  with the keyboard, and no Keyboard plugin is needed for that.
- No status-bar, keyboard or splash-screen plugin is installed. Preferences is
  installed and unused.

**What breaks on a phone, and where it is fixed:**
- The chat dock traps its children (90). Its height with the keyboard is
  wrong (90).
- The bottom chrome sits over focused fields (90). The ConnectionNotice slides
  under the status bar when it sticks (90). There is no status-bar scrim (90).
- Back misses What's New, the profile, the context menu and the wheel (90).
  Scroll is not reset (90). The Menu is reachable only at the top (95).
- Hover info is invisible (92). There is sticky hover, tap highlight and
  pull-to-refresh (89, built).
- iOS input zoom and `interactive-widget` (89, built).
- A per-frame repaint from the fixed body background (89, built) and from
  animated glows and filters (108).
- Item badges at 9.3 px and tab labels at 10.9 px (109).

**Rotation.**
- `ShieldWheel.svelte:868` `(orientation: landscape) and (max-height: 540px)`
  stays, and so does the landscape half of `check:safearea`. The reasons are
  in the owner decision at the top.
- The iOS project still lists landscape (`ios/App/App/Info.plist:64-69`).
  Whether iOS should be portrait-only too is decision 110b.
- MOBILE.md's A2 checklist "Rotation" line needs rewording (110c).

**Mobile web only:**
- There is no `manifest.webmanifest` or apple-touch-icon (110d: only if web
  on a phone is a supported path).
- `vh` caps are wrong with the URL bar showing (90).
- There is no `interactive-widget` (89).

**Phone checks to add to MOBILE.md's A2 list:**
- the chat profile from a name tap;
- typing in a Market price field near the bottom;
- typing in chat;
- scrolling a long screen while disconnected;
- splash to first paint in the light system theme;
- the shield wheel's top edge.

---

## 7. Recommended design system

Task 89 (built, not merged) puts tokens in `:root`. Task 106 adopts them. The
values come from the most-used existing values, so adopting them moves any
element by 2 px or less. The full derivation is in the CSS pass; this is the
condensed contract.

**Type** (rem at a 16 px root; body stays 14 px = `--fs-base`):

| Token | Value | Use |
|---|---|---|
| `--fs-badge` | 0.68rem (10.9 px) | badges only, never body copy; absorbs 0.55-0.68 |
| `--fs-xs` | 0.72rem | the floor for any text |
| `--fs-sm` | 0.8rem | absorbs 0.75-0.83 |
| `--fs-base` | 0.875rem | equals body; absorbs 0.85-0.95 |
| `--fs-md` | 1.05rem | card titles |
| `--fs-lg` | 1.3rem | headings, big numbers |
| `--fs-display` | 3rem | ShieldWheel result |

- **Weights:** 400 / 600 / 700 / 800. `650` and `bold` fold into 700.
- **Line heights:** 1.15 / 1.35 / 1.5.
- **Tracking:** 0.06em for caps, 0.12em wide.
- **Families:** `--font-ui` (system), `--font-display` (the Georgia stack plus
  `'Book Antiqua'`), `--font-mono`.

**Spacing:** `--sp-0` to `--sp-9` = 0.1, 0.2, 0.35, 0.45, 0.5, 0.6, 0.75, 1,
1.5, 2 rem. This replaces 45 distinct lengths. The phone panel gap is
`--sp-5`.

**Semantic colours** (dark / light), in addition to the existing tokens:

| Token | Replaces |
|---|---|
| `--danger` / `--good` / `--warn` (existing) | `--bad`, `--err`, `--ok`, `--success`, and the stray Tailwind reds and greens |
| `--info` | `#7dd3fc` fallbacks |
| `--on-accent` | `#1a1510`, `#1b1712`, `#fff` on fills |
| `--bg-sunken` | `--bg-dark`, ad-hoc dark wells |
| `--line`, `--edge-soft` | white-alpha dividers and card edges |
| `--tint-hover`, `--tint-selected` | `--bg-hover`, the slate-blue tint, four active-tab looks |
| `--scrim` | 0.55 / 0.6 / 0.62 backdrops |
| `--opacity-disabled: .5` | .45 / .5 / .55 |

**Radii:** `--radius-xs` 3 px, `--radius-sm` 5 px, `--radius` 8 px (existing),
`--radius-lg` 12 px, `--radius-pill` 999 px. The dead `var(--radius, 6px)`
fallbacks become an explicit `--radius-sm`.

**Shadows:** `--shadow-sm`, `-md`, `-lg`, `-xl`, `--shadow-well` and
`--shadow-panel`, each with a light-theme variant.

**Z-index layers.** Every overlay mounts on `<body>`, never inside `.panel`.

```
--z-raised 1 · --z-sticky 30 · --z-float 40 (dock, coach, update chip)
--z-nav 50 (TabBar, desktop dropdown) · --z-overlay 55 (LootReveal)
--z-modal 60 (all modal cards incl. OfflineSummary, PlayerProfileModal, PersonPicker)
--z-tutorial 70 · --z-popover 80 (ContextMenu) · --z-toast 90 · --z-system 100
```

**Breakpoints:**
- `phone` is `(max-width: 40rem)` and absorbs 30rem, 560 px and 600 px.
- `tablet` is `(max-width: 52rem)` and absorbs 46rem.
- `wide` is `(min-width: 64rem)`.
- Named queries: `hover` = `(hover: hover) and (pointer: fine)`, and the one
  height query (ShieldWheel).
- They are mirrored in `lib/ui/media.ts` and guarded by a test.

**Motion:** 60 / 120 / 160 / 400 / 900 ms, plus a 1.4 s pulse and a 3 s
ambient loop (none in lists). The easings are `ease-out` and `ease-in-out`.

**Primitives** (task 106):
- `Button` (variant default | primary | danger | ghost | quiet; size sm | md);
- `Modal` (portal, dialog role, Escape and back through the closer stack,
  focus trap, safe-area padding, dvh cap, bottom-sheet variant on a phone);
- `Tabs` and `ChipGroup`;
- `ItemRow`;
- `Hint` (tap-to-toggle, the TraitBadge pattern);
- `QueryState` (task 93);
- `ConfirmButton` (task 92);
- `Bar` with size, tone and `ariaLabel`;
- one `.panel` base.

---

## 8. Redesign proposals

One sketch per redesigned screen, the best of the visual passes. Each is a
starting point for a screenshot the owner sees before the build is merged.

**Phone navigation (95).** The fifth tab becomes More, and the header is slim
and sticky.
```
+--------------------------------------+
| FolkIdle     2 000 g  0 ◆   [≡]      |  <- sticky, 48px; ≡ opens the same sheet
+--------------------------------------+
|  Community > Friends                 |  <- breadcrumb: family > tab
|  [Friends][Guild][Market][Ranks]     |  <- one scrollable row, no wrap
+--------------------------------------+
| Map  Combat  Gather  Hero•  More(3)  |  <- More = bottom sheet
+--------------------------------------+
More (bottom sheet, grouped tiles):
 PEOPLE  [Friends&Guild] [Market] [Mail 3] [Chat 2]
 MAKE    [Village] [Crafting] [Forge] [Bloodline] [Codex]
 HERO    [Skill Tree] [Wardrobe] [Chest] [Supplies]
 MORE    [Progress] [Wiki] [Settings]
```

**Chat on a phone (95, with 90 for the trap).** A full-height sheet with one
frame. The sender is stacked above the message, which also removes the
zero-width crush.
```
Chat · 2 online                     [✕]
[World][Guild][Whispers][News]  ->
 Owain · 00:33
 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa...
 dev · 00:34
 hello
[Say something...            ][Send]
```

**Settings (96).**
```
Settings
[Account · Sign out]   [Language: English ▾]
▸ Sound & music
▸ Notifications & email
▸ Gameplay (auto-salvage)
▸ Accessibility
▸ Tutorial & explanations (14/27 seen)
▸ Support · About · Delete account
```

**Character, phone (97).**
```
┌ [Cadoc ▾] Lv 40 Human · Fighting Thorny Vine ┐   <- slot/person switcher, sticky
│ HP ███████████ 1170   Acc 103 · Arm 923 · Blk 9% │
├ [Gear] [Attributes 25•] [Work & orders]      ┤   <- segmented tabs, badge = unspent
│  Weapon  Helmet  Chest  Legs                 │   4-col icon grid, 64px tiles
│  Gloves  Boots   Amulet Ring                 │
│  Axe     Pickaxe Rod                         │   (all eleven slots)
│  Set: Sentry 4/5 · +36% armour               │
└──────────────────────────────────────────────┘
```

**Combat, phone (98).** The first Fight lands at about y 250 instead of
y 915. Tapping a card expands its drop table inline.
```
+--------------------------------------+
| [sticky] Lv 40  ====XP====  HP ===== |
|  Fighting Field Mouse  [Stand down]  |   (or "Idle - pick a monster" / [Continue: X])
+--------------------------------------+
| Loot this session: 3 items, 41 mats >|
+--------------------------------------+
| SUNLIT PLAINS            (i) rules   |
| [img] Field Mouse    [safe]  [FIGHT] |
|  465 HP · 93 XP · 2 s · 255k XP/h    |
| [img] Alpha Wolf BOSS [safe] [FIGHT] |
|  Challenges 0/3 · Ascension 0/10   > |
+--------------------------------------+
```
On desktop: a sticky left column (status and loot) and a wide monster table
on the right.

**Chest row (99).**
```
[icon] Hunter Amulet        [Worn]   [Unequip][...]
 T2 Relic - +12% crit, +40 HP, 2 more
```
On desktop: list | detail pane (affixes, sell price, Reroll/Lock/Sell/Bin as
real buttons).

**Forge fusion (100).** One row per base item. Worn items come first, and
Fuse is in the row.
```
Fusion  - Forge lv 5 can make up to Ancient          [?]
[search item...]
Birch Wand         Normal 220  Common 132 ... Mythic 5
                   next: 3 Mythic -> 1 Relic, 12k gold   [Fuse] [Fuse stack v]
```

**Gathering (101).**
```
[ Mining Copper Ore - Sunlit Plains   ███████░░ 0.5s   [Stop] ]
Woodcutting  lv 0  ░░░░░  axe T0
  Sunlit Plains        3.0s 15xp  [Gather]
  Whispering Woods     locked - fight here first
Mining  lv 56 ...           (active row highlighted)
Hauled this session: Copper Ore 120, Birch Log 40
> How fast and why (tools, mastery, monoliths)
```

**Crafting card (101).** Groups: Ready / Missing materials / Locked
(collapsed). Lead with "Nothing craftable yet - the first tools need Birch
Log and Copper Ore. [Go gather]" when nothing is craftable.
```
[axe icon] Willow Axe              [Craft] [Put to work]
 Axe - lvl 20 - 10s - +x% wood speed
 Willow Log  ████░░ 7k/136   Iron Ore ██████ 52k/68
```

**Market (102).**
```
 Equipment | Cosmetics                        <- underline sub-tabs
 [Buy] [Sell] [My orders]                     <- segmented
 Search [_____________]  [Filters · 2 ▾]      <- filters in a bottom sheet
 ┌ icon Sentry Helm   Relic T2   12 400 gold [Buy] ┐
```
Sell: full-screen picker → summary card → "List Sentry Helm for 1 000 gold".

**Village (103).**
```
Village                       Villagers 184/35
Forge        Lv 5/5 cap  ...........  Maxed
Inn          Lv 5        100/100 Willow ✓ 2 690g ✓ [Upgrade]
---------------------------------------------------------
Gene pool  Newcomers 101/11 (full - arrivals stopped)  (i)
   S  K  E  F
Diarmait  ♂ 10 10 10 10  [Send on]
... top 5 by sum ...   [Show all 101]   Married in (12) ▸
---------------------------------------------------------
Great Works  +0% yield / +0 min offline
Birchwood Cairn ●○○○○ 0/50 000  [Deposit]
```

**Ancestors (104).**
```
Hall of Ancestors          Carried 10/10   [+ slot 250 dia]
What survives a rebirth >
-- Carried into next season ----------------------------
 [av] Cadoc     founder   In slot 1   4  4  4  4 | 16   *
 [av] Liadan    gen 1     Bench      10 11 11 10 | 42   *
-- Lost at rebirth (190) >   sort: total v ------------
 (collapsed; 20 at a time; tap a row -> sheet)
 [av] Morfudd   gen 1 child  10 10 10 10 | 40   * over cap
```

**World Boss (105).**
```
+--------------------------------------+
| [painted boss banner, ~120px]        |
|  WORLD BOSS . ends in 4d 1h  (live)  |
|  <Boss name>                         |
|  [=========== 75M / 75M ==========]  |
+--------------------------------------+
| Today's strike: READY                |
| Plates [1 ok][2 ok][3 ok][4 ok][5 ok]   (status only)
| [   STRIKE WITH THE SHIELD WHEEL   ] |  <- filled primary
|  Quick strike (no skill bonus) >     |  <- opens plate pick
+--------------------------------------+
| Damage this week        you: --      |
| (i) How the World Boss works      v  |
+--------------------------------------+
```

**The Delve (105), full dark "underground" mode** (if the owner picks dark
over parchment):
```
| THE DELVE                            |
| Pay, go down, choose each floor.     |
| THE GATE         run costs 17 000 g  |
| [        PAY AND DESCEND         ]   |
| Diamonds this week 60/60 - cap hit;  |
| runs still pay gold. Resets Mon 02:00|
| Records  best floor 8 · this wk 8  > |
| Titles  Wear: [Lamplighter v]        |
```

**Login (109).** Only if a guest can really upgrade to an account. Otherwise
the guest note says "Guest progress lives on this device only".
```
│  [map art banner, 160px]       │
│  FolkIdle                      │
│  Raise a village, breed a      │
│  bloodline, fight while away.  │
│ [      Play now (guest)      ] │  <- filled primary
│  [ Sign in ]  [ Create account ]│
│        Get the Android app     │
```

**Wiki on a phone (109).**
```
[Search every page...        ]
[Contents: Basics - The core loop  ▾]
...
[‹ Previous: —]      [Next: Combat & stats ›]
```

---

## 9. Quick wins

These cost under an hour each and are folded into the tasks shown.
- Exclude Gold from the Chest's materials (99).
- Leaderboard `.progress { grid-column: 2 / -1 }` and a light-theme tier
  palette (91).
- `'info'` tone on the success notices (91).
- Delete the Mailbox backpack gate (91).
- Back closes What's New (90).
- `TabBar` closes the Menu (90).
- OfflineSummary `max-height` and `stopPropagation` (90).
- ConnectionNotice `top: var(--sa-top)` (90).
- AchievementToast `bottom` reads `--tabbar-h` and `--sa-bottom` (90).
- Hide the coach while GuidedOverlay is up (109).
- Forge: scroll to the form and mark the chosen chip after `pickSet`; move
  the fusion explainer into the Fusion panel (100).
- Crafting: right-align the Craft/Put to work group; character names instead
  of "Slot 1" (101).
- SessionLoot heading level; Gathering's stray leading "·" (101).
- Supplies desktop slider out of the phone media query; "Eat below 50 %
  health" (109).
- Boosts copy (109).
- Codex developer note (109).
- "Respec (free)" spacing (109).
- Market empty-state split; "Sort:" label (102).
- Guild 1st/2nd place colours (91).
- Desktop active nav group with an accent underline; the coach becomes a dot
  (95).
- Social Join respects `MinApplicationLevel` (92).
- `enterkeyhint="send"` and `autocapitalize="none"` (90).

---

## 10. Roadmap

The work is **TASK_BOARD tasks 89-110**, in `docs/TASK_BOARD.md` under
"Tasks 89-110: the 2026-10-01 UI/UX audit". Its Order table gives the
sequence, size and owner input of each task. In short:
- **Foundations first.** 89 and 93 are built. 90, 91 and 92 are in progress.
- **Then the server gap:** 94, leave guild.
- **Then the shell:** 95 navigation, 96 Settings.
- **Then the phone screen redesigns:** 97-105.
- **Primitives (106) run alongside.** Each redesign adopts what it needs.
- **Then 107-109.**
- **110 is a list of owner decisions** to take whenever convenient.
