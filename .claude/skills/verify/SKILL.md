---
name: verify
description: Verify a FolkIdle change actually works, in the right order and with the right level of proof. Use after changing gameplay, an engine, a packet, content JSON, or a screen - and before claiming anything is done.
---

# Verifying a change

The rule this project learned the hard way: **a thing that renders is not a
thing that works.** Its worst shipped defects were all "the output side was
never wired" — crafting granted nothing, loot went dead after 20 kills, the
larder was empty. Every one of them passed a render check.

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

## 4. Balance and content

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

## Reporting

Say what you ran and what it printed. If a rung was skipped, say which and why.
"Tests pass" without a command and a count is not a result.
