---
name: verify
description: Verify a FolkIdle change actually works, in the right order and with the right level of proof. Use after changing gameplay, an engine, a packet, content JSON, or a screen - and before claiming anything is done.
---

# Verifying a change

The rule this project learned the hard way: **a thing that renders is not a
thing that works.** Its worst shipped defects were all "the output side was
never wired" — crafting granted nothing, loot went dead after 20 kills, the
larder was empty. Every one of them passed a render check.

The second rule, learned on 2026-09-02: **verify as the player you are worried
about.** Onboarding told a brand-new account to fight the first monster, which
killed it at 29 seconds, so the tutorial stalled on step one for ever — and
every test passed, because the fixture that ran them was level 40 with a full
larder. Ask who the change is for, then be that person.

Pick the weakest rung that actually covers what you changed, then run it.

## 1. It compiles

```powershell
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
cd client_web; npm run check
```

**Stop the running server first**, or the build succeeds against a locked
output directory and produces a stale DLL.

**Four pre-existing `svelte-check` errors are not a regression** — the hidden
Guild War handlers in `GuildOps.svelte`. Compare against that baseline, not
zero. The count is a weak guard: it sat at 9 for a long time and silently
turned over into a different 9, so read the error list, not just the total.
CI fails only when it grows.

## 2. The server suite

```powershell
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
```

**Requires a working Docker daemon** — tests run against a real Postgres via
Testcontainers. Without Docker the whole suite fails at once with
`PostgresTestFixture` NullReferenceExceptions, which resembles nothing in your
diff. Check Docker before debugging a mass failure.

Recorded pass counts in the docs disagree (182, 341 and 470 all appear).
Run it and report what you saw; never quote a number from a document.

## 3. The browser, asserting the world changed

```powershell
cd client_web; npm run exercise
```

**This is the one that counts for a gameplay change.** `exercise.mjs` signs in
as the dev fixture, clicks through every interactive feature and asserts an
*effect* — the inventory shrank, the affix value moved, the villager is spent.
It prints `ok`/`FAIL` per check and exits non-zero on any failure.

`npm run smoke:screens` is the weaker sibling: it proves a screen renders. A
screen of buttons that all silently do nothing renders perfectly.

If you added a feature, add a check to `exercise.mjs`. An unexercised feature
is the exact shape of every defect listed above.

**A check must not spend what a later check needs.** Three steps were red on a
working game for a long time because they marked a flag nothing clears,
re-equipped the item already worn, and sent on the last villager the breeding
step needed. Make a check round-trip and put back what it touched, or it passes
once and fails for ever after.

**The dev fixture cannot answer a new player's question.** It is level 40,
geared, stocked and an admin, so every onboarding predicate is already true for
it and `/api/v1/admin/status` never refuses it. Anything a new player meets has
to be checked by registering one — `exercise.mjs` does that in its own browser
context at the end.

## 4. Geometry: cut off, and buried

```powershell
cd client_web
npm run check:clipping   # content wider than its box, in a box that cannot scroll
npm run check:overlap    # a control that another control takes the click for
```

Both walk every screen (1500 / 900 / 390px for clipping) and both should read
**0**. Both sign in as the dev fixture, because geometry needs a screen with
content in it — a guest's empty Chest proves nothing — so unlike
`smoke:screens` these are dev-box tools and not production ones.

They exist because neither failure is visible to a type check or a smoke test:
every element renders, the DOM is well formed, and only the geometry is wrong —
a Village upgrade cost was once sliced through the middle of a word and found by
squinting at a screenshot.

What they deliberately do **not** report, because each was noise that drowned
the real findings:

- `overflow-x: auto` — a deliberate scroller, which is what wide content is
  supposed to be.
- `text-overflow: ellipsis` — truncation that *says* so with a visible "…".
  The Chest does it 724 times on a phone and is right every time.
- SVG — it reports `clientWidth` in its own coordinate system, which made the
  Skill Tree's labels look like 91px overflows in 29px boxes.
- A `position: fixed` overlay — ChatDock floats over the bottom-right corner, so
  at some scroll offset it covers *something*. `app.css` reserves the bottom
  padding that guarantees the covered control can always be scrolled clear.

## 5. Balance and content

Content JSON under `server/GameData/`:

```powershell
python ops/validate_content.py
```

Balance is pinned by tests that **print their tables** rather than asserting
authored numbers — `ProgressionRateTests`, `GatheringShareTests`,
`MonsterLadderTests`. Read the printed table; it is the measurement.
`MonsterLadderTests` pins the ladder's *shape*, not its values: retune freely,
but the ladder may never descend.

## Traps while verifying

- **`isDisabled()` does not work on an `<option>`.** Playwright's editability
  check answers "not disabled" whatever an option's attribute says, so a step
  once picked a greyed-out choice and reported a working feature as broken.
  Read `o.disabled` through `evaluate`.
- **Scope navigation lookups to the `header`.** The hub map's plates are
  buttons named "Combat", "Market" and "Guild" too.
- **Wait for content, not for a fixed delay.** A cold server answers the first
  query slowly enough that a fixed wait passes locally and fails on a fresh
  boot — a flaky test wearing a bug report's clothes.
- Some 404s are expected: optional per-weapon audio clips, and exactly one
  deliberate unknown-player lookup. `exercise.mjs` already separates them.
- **A 403 is expected too.** Every client asks `/api/v1/admin/status` whether
  this account may see the admin tools; an ordinary account is told no and the
  browser logs the refusal as a console error. It only appears once a checker
  stops using the admin fixture.
- **Below the mobile breakpoint the nav collapses behind a hamburger.** A nav
  button then exists but is never visible, so a direct click — or a
  `waitForSelector('text=Combat')` — burns its whole timeout and reports a
  locator, not a breakpoint. `screens.mjs`'s `go()` opens the menu first; use
  it rather than clicking the header yourself.

## Reporting

Say what you ran and what it printed. If a rung was skipped, say which and why.
"Tests pass" without a command and a count is not a result.
