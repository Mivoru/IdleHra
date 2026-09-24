# Task 37: honest income, then The Deep - implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to run this plan task by task. Steps use checkbox (`- [ ]`) syntax. The project skills used below are `run-stack`, `verify`, `deploy`, `code-review`. `add-command` is **not** expected, because everything is REST. If a phase finds it needs a wire field, stop and use the skill.

**Goal:**

- price every gold sink against the income the top of the game really earns (Phase 0);
- pin combat gold at 75% by decision, not by accident (Phase 0);
- give that income a bottomless, self-scaling, diamond-neutral and power-neutral place to go: the Deep, an endless gold-tolled continuation of the Delve past floor 8 that pays records, titles and a weekly board.

**Spec:** `docs/superpowers/specs/2026-09-24-the-deep-gold-sink-design.md`. **Every constant comes from it. Change the spec before the code.**
**Brief (the measured economy):** `docs/superpowers/plans/2026-09-23-task-37-late-game-gold-sink-design.md`.

**Architecture:**

```
Delve.svelte ── at the bottom: "Descend: toll X" ──> POST /api/v1/delve/deep/descend {QuotedStake}
                                                      DelveEngine.DescendAsync (one Serializable tx):
                                                        bank floors 1-8 (the BankAsync path) -> lock gold -> stake (frozen on the run row)
                                                        -> debit toll(d) -> floor d doors
             ── door ──> /api/v1/delve/door (existing; DeepSuccessChance when IsDeep; record + title in the same tx)
             ── 0 charges ──> POST /api/v1/delve/deep/lantern (debit stake x 2^k)
             ── walk out ──> /api/v1/delve/bank (in the Deep: ends the run, pays nothing)
             every gold-moving action -> ReloadState enqueued (the existing Delve pattern)
Leaderboards.svelte ── "Deepest this week" ──> GET /api/v1/leaderboard/deepest (SQL, not a ZSET; pays nothing)
Profile / Delve ── title ──> GET /api/v1/player/titles, POST /api/v1/player/title
```

## Global Constraints

- **CLAUDE.md's load-bearing rules**, the ones this task touches:
  - *Two gold paths*: every Deep price is a **DB debit** on the locked `CommodityRecords` gold row, in the same transaction as the roll, then `ReloadState`. **Never `RedisPendingGoldDelta` for a debit.**
  - *Silent rollback*: every refusal returns a `DelveResult` plus a view, and the screen shows a sentence.
  - *Every multiplier declares a cap or a curve*: the Deep adds no power lever. `DeepSuccessChance` is a stated diminishing curve.
  - *A number a test prints is not a number a test checks*: Phase 0's table asserts.
  - *A leaderboard that pays diamonds needs a population floor*: this board pays **nothing**, and tests pin that.
  - *A check that spends fixture state passes once and fails forever*: the exercise block round-trips.
  - *Suspect the fixture*: `DevFixtureInvariantTests`.
  - *Raw SQL must match the table name*: `player_titles` is snake_case, `"PlayerRecords"`/`"DelveRunRecords"`/`"CommodityRecords"` are PascalCase and quoted.
  - The Svelte traps.
- **Migrations run on the container ENTRYPOINT.** Apply locally by hand (`--migrate`) before starting the stack, or sign-in hangs.
- **Stop the server before `dotnet build`/`dotnet test`,** each as its own call. Docker must be up. Run one suite at a time.
- **Flag:** `FOLKIDLE_DELVE_DEEP` = `off` (default) | `on`. Production stays `off` until Phase 2 is verified.
- **Dev tools:** the Deep's exercise needs a run at the bottom of floor 8, which a fixture sheet cannot reach reliably. Add `POST /api/v1/dev/delve/at-bottom` behind the **existing** dev-tools gate (`NetworkBroadcastSystem.DevToolsEnabled`: `FOLKIDLE_DEV_TOOLS=1` **and** not `DOTNET_ENVIRONMENT=Production`), next to task 25's `/api/v1/dev/worldboss/window`.
- **Each task is one commit; each phase is one PR.** Write commit messages with the Write tool and `git commit -F`. End them with the `Co-Authored-By` / `Claude-Session` lines.

---

## Phase 0 - measure income honestly; combat gold becomes a decision (one PR, no gameplay change)

### Task 0.1: `CombatGoldPercent` is a named constant, and the audit cannot move it

**Files:**
- Create: `server/FolkIdle.Server/Engine/EconomyDecisions.cs` (`public const int CombatGoldPercent = 75;` with the `// Modul:` history: an accident of the audit since 2026-08-05, and kept deliberately by the owner on 2026-09-24)
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:~4608`, `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs:~717` (read the constant)
- Modify: `server/FolkIdle.Server/Engine/GlobalEngineState.cs` (delete `GlobalGoldDropMultiplier`)
- Modify: `server/FolkIdle.Server/Engine/EcoTelemetryEngine.cs:~152-167` (keep the ratio row and the `EventType 6 / Value1 44` event; delete both assignments)
- Create: `server/FolkIdle.Server.Tests/CombatGoldDecisionTests.cs`

- [ ] **Step 1: Failing tests:**
  - (a) run `ExecuteAuditAsync` against a Testcontainers DB seeded so the ratio lands **inside** `[0.85, 1.15]`, then assert the per-kill gold of a fixed monster (computed through the same helper both kill paths use; extract `GoldPerKill(monster, payload)` if the two copies are still inline, and make the offline path call it) is still `BaseGoldReward x 75 / 100` times the other multipliers;
  - (b) the same **outside** the band;
  - (c) a source grep over `server/FolkIdle.Server` finds no `GoldDropMultiplier =` assignment, and no identifier `GlobalGoldDropMultiplier` at all.
- [ ] **Step 2: Implement.** Record the finding in the constant's comment: the old field defaulted to **100** until the first audit pass, so each restart paid full combat gold for a while.
- [ ] **Step 3:** Stop the server, then run `dotnet build` and `dotnet test ... --filter "FullyQualifiedName~CombatGoldDecision|FullyQualifiedName~EcoTelemetry|FullyQualifiedName~Offline"`. Expected: PASS.
- [ ] **Step 4: Commit** `refactor(economy): combat gold is a named 75% decision the audit cannot move`.

### Task 0.2: the top-of-game income profile and the asserted sink table

**Files:**
- Modify: `server/FolkIdle.Server.Tests/GoldSinkAffordabilityTests.cs`
- Modify: `docs/TASK_BOARD.md` (task 37: link the spec, this plan and the measured numbers)

- [ ] **Step 1: The profile.** Add `TopOfGameGoldPerHour()`:
  - `3600 / CombatDamageModel.ExpectedSecondsPerKill(...)` for `ProgressionRateTests`' geared level-94 reference sheet (reuse its builder, and make it `internal static` if needed) against the strongest region-5 regular;
  - multiplied by `GoldPerKill` at `EconomyDecisions.CombatGoldPercent`.

  **Assert** it is within 3x of `MeasuredTopIncomePerHour = 10_000_000`, with a `// Modul:` pointing at the brief's §1.2 and its date. Keep `KillsPerHour = 180` for the early-game facts, and rename it `EarlyGameKillsPerHour` so nobody mistakes it for the top.
- [ ] **Step 2: The table.** `TheGoldSinkTableAtTheTop` prints, and asserts per row, the minutes of top income for:
  - a reroll (r5);
  - a fusion (tier 14);
  - village L20;
  - feast n = 16/20/25;
  - the Delve gate (r5).

  Its **one hard assertion** is that at least one *repeatable* sink absorbs >= 30% of an hour. Mark it `Skip = "task 37 phase 1 turns this green"` **only** if Phase 1 is not in the same PR, and say so in the PR body.
- [ ] **Step 3:** Stop the server and run `dotnet test ... --filter "FullyQualifiedName~GoldSink"`.
- [ ] **Step 4: Commit** `test(economy): measure top-of-game gold income and assert the sink table`.

**PR "task 37 phase 0". Run `code-review`, merge, and deploy (the `deploy` skill; there is no migration). Nothing visible changes except that a restart no longer pays 100% combat gold for a few minutes.**

---

## Phase 1 - the smallest playable Deep, behind the flag: descend, tolls, records (one PR)

No lanterns, titles or board yet. A run at the bottom can descend, pay tolls, go as deep as its charges allow, and leave a record.

### Task 1.1: the rules, pure

**Files:**
- Modify: `server/FolkIdle.Server/Engine/DelveRegistry.cs`:
  - `StakeFraction = 0.005`, `TollGrowth = 1.25`, `DeepDecay = 0.97`, `MaxLanternRefills = 8`, `PriceCeiling = long.MaxValue / 4`;
  - `Stake(regionFee, goldHeld)`, `TollForFloor(stake, floor)`, `LanternRefillPrice(stake, bought)`, `DeepSuccessChance(value, floor)`;
  - each with a `// Modul:` comment giving the measured number it was priced against.
- Create: `server/FolkIdle.Server.Tests/DelveDeepRulesTests.cs`

- [ ] **Step 1: Failing tests:**
  - the stake is the region fee for a holder of 1M and `0.005 x held` for 492M (2,460,000);
  - `TollForFloor(stake, 9) == stake`, and the toll strictly increases to floor 60;
  - floor 500 saturates at `PriceCeiling` without overflow or exception;
  - refills are `stake x 2^k` for k 0..7;
  - `DeepSuccessChance` stays at or below 0.90, strictly decreases while above 0.25, and never goes below 0.25 (sample floors 9..200 for sheets 0, 50, 269, 1000);
  - the requirement used is `RequirementForFloor(8)` for every d > 8.
- [ ] **Step 2: Implement, run, commit** `feat(delve): the Deep's rules, a stake on holdings and a stated curve`.

### Task 1.2: migration, engine, REST

**Files:**
- Create: migration `AddTheDeep` (spec §5: `DelveRunRecords.IsDeep/StakeGold/LanternsBought`; `PlayerRecords.DelveDeepestFloor/DelveDeepestThisWeek/DelveDeepestThisWeekAtUtc/ActiveTitleSlug`; the `player_titles` table, created now so Phase 2 needs no second migration). Additive only. Check the generated SQL for `defaultValue:` traps: the memory note says EF backfilled 0 over a C# default of 4 once, so the defaults must match the C# model.
- Modify: `server/FolkIdle.Server/Models/DelveRunRecord.cs`, `Models/PlayerRecord.cs`, create `Models/PlayerTitle.cs` (`[Table("player_titles")]`), `Models/FolkIdleDbContext.cs`
- Modify: `server/FolkIdle.Server/Domain/Economy/DelveEngine.cs`:
  - `DescendAsync(playerId, quotedStake)`;
  - `ChooseDoorAsync` uses `DeepSuccessChance` when `IsDeep`, updates the records (with the week-key rule from spec §4) in the same transaction, and never grants embers or diamonds in the Deep;
  - `BankAsync` in the Deep ends the run and pays nothing;
  - `GetViewAsync` fills the Deep fields.
  - **Extract the floors-1-8 banking into one private method that both `BankAsync` and `DescendAsync` call.** Do not copy it.
- Modify: `DelveResult` (`PriceChanged`, `NoMoreLanterns`, `NotAtTheBottom`, `DeepDisabled`), `DelveRunView` (spec §5)
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (`HandleDelveAction`, ~`:2779`: route `/api/v1/delve/deep/descend`; extend the `changedBalances` condition so descend enqueues `ReloadState`; add `POST /api/v1/dev/delve/at-bottom` behind `DevToolsEnabled()`)
- Modify: `server/FolkIdle.Server/Program.cs` (read `FOLKIDLE_DELVE_DEEP`)
- Create: `server/FolkIdle.Server.Tests/DelveDeepTests.cs` (Testcontainers)

- [ ] **Step 1: Failing engine tests:**
  - descending from the bottom banks exactly what `BankAsync` would have paid (diamonds and consolation) **and** debits exactly `toll(9)`, in one transaction;
  - with gold after banking below the toll, the answer is `NotEnoughGold` and **nothing** changes: no bank, no floor, no gold;
  - `QuotedStake` below the server's stake gives `PriceChanged` and nothing changes, while one at or above it proceeds and charges the **server's** number;
  - descending when not at the bottom gives `NotAtTheBottom`;
  - with the flag off, the answer is `DeepDisabled` and the view offers no descent;
  - **the stake is frozen**: add 100M gold mid-run, and the next toll is unchanged;
  - a door past 8 uses `DeepSuccessChance` (injected `Random`, as the existing tests do);
  - a clear raises `DelveDeepestFloor` and `DelveDeepestThisWeek`;
  - a last-week `DelveWeekKey` is reset before the weekly record is written;
  - `ADoorOutsideTheOfferedRangeIsRefusedAndChangesNothing` holds in the Deep;
  - the run survives a relogin (the row-backed view).
- [ ] **Step 2: Failing guard-rail tests** (spec §6):
  - a whole Deep session (descend, 3 doors, walk out; and descend, fail to 0 charges, `RunLost`) mints **zero** diamonds, with `DelveDiamondsThisWeek` moving only in the banking of floors 1-8;
  - a whole-row diff of `PlayerRecords` shows only the allowed columns changing;
  - a source grep finds no `RedisPendingGoldDelta` or `ChestSaleGoldQueue` in `DelveEngine.cs`/`DelveRegistry.cs`.
- [ ] **Step 3: Implement.** Apply the migration locally (`$env:FOLKIDLE_DB_CONN=...; dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --migrate`, with the server stopped).
- [ ] **Step 4:** Run the filter `~Delve|~GoldSink`. Phase 0's 30% row turns green now: remove its `Skip`.
- [ ] **Step 5: Commit** `feat(delve): the Deep - descend on a frozen stake, tolls, records`.

### Task 1.3: the screen and the exercise

**Files:**
- Modify: `client_web/src/routes/Delve.svelte`:
  - at the bottom: **Walk out** and **Descend into the Deep: toll X** (the quoted stake is sent back as `QuotedStake`);
  - in the Deep: floor number, next toll, "walk out", personal record, weekly record;
  - every `Result` shown as a sentence.
- Modify: `client_web/src/lib/net/rest.ts` (Delve DTO fields and `descendDeep`)
- Modify: `client_web/scripts/exercise.mjs` (the Delve block, ~`:1208`)
- Modify: `server/FolkIdle.Server/Models/DevFixtureSeeder.cs` and `server/FolkIdle.Server.Tests/DevFixtureInvariantTests.cs` (the fixture holds at least 10x the r5 gate in gold, asserted)

- [ ] **Step 1: The UI.**
  - Use `{#if}`, not `<details>`, and no `<select>`.
  - Nothing that ticks sits inside a control.
  - Buttons are 44 px or more with `flex-shrink: 0` in flex rows.
  - Prices are formatted with the existing gold formatter.
  - Run `npm run check:ratchet`, `check:touch`, `check:clipping`, `check:overlap` with a Deep run showing. Add a `screens.mjs` state for it if the geometry checks need one.
- [ ] **Step 2: The exercise.** It spends and round-trips:
  1. `POST /api/v1/dev/delve/at-bottom`. On a 404, **record a failure naming `FOLKIDLE_DEV_TOOLS`**; do not skip.
  2. Read `/api/v1/delve`, then record the gold `G0`, the quoted stake `S` and the toll `T`.
  3. Descend and re-read. Assert the gold equals `G0 + bankPayout - T` **exactly**, where `bankPayout` is the consolation gold from the view (0 when diamonds were paid), and the view shows floor 9.
  4. Walk out and **reload the page**. Assert the run is closed, the gold is unchanged since step 3, `DelveDiamondsThisWeek` moved only by the floors-1-8 bank, and the record is 8 or more (9+ if a door was cleared).

  The stake is a percentage of holdings, so the fixture never runs dry. Re-seed if needed.
- [ ] **Step 3:** Load the page and play one Deep run by hand at 390 px.
- [ ] **Step 4: Commit** `feat(delve): descend into the Deep on the Delve screen; exercise round-trips it`.

**PR "task 37 phase 1: the Deep (flag off in prod)". Run `code-review`. After the merge, deploy with `FOLKIDLE_DELVE_DEEP=off`, confirm the migration ran, then set `on` for the owner to play. Record anything retuned in the spec.**

---

## Phase 2 - lanterns, titles, the Deepest board (one PR)

### Task 2.1: lanterns

**Files:** `DelveEngine.cs` (`BuyLanternAsync`), `NetworkBroadcastSystem.cs` (`/api/v1/delve/deep/lantern`, plus `ReloadState`), `Delve.svelte`, `rest.ts`, `DelveDeepTests.cs`

- [ ] **Step 1: Failing tests:**
  - a purchase debits exactly `stake x 2^k` and adds one charge;
  - buying with charges above 0, or with no Deep run, is refused visibly (`NoRunInProgress` or a new `ChargesRemain`);
  - the 9th purchase gives `NoMoreLanterns`;
  - not enough gold gives `NotEnoughGold`, and nothing changes;
  - **the client never sends a price**, only the action.
- [ ] **Step 2: Implement, UI** ("Your lantern is out. Light another: X gold", or walk out), **commit** `feat(delve): lantern refills at a doubling price`.

### Task 2.2: titles, a minimal display

**Files:**
- Create: `server/FolkIdle.Server/Domain/Progression/TitleRegistry.cs` (spec §4 slugs; `ForDeepFloor(int)`)
- Create: `server/FolkIdle.Server/Domain/Progression/TitleEngine.cs` (`GrantAsync(db, playerId, slug)`, idempotent through `ON CONFLICT DO NOTHING` on `player_titles`; `SetActiveAsync`)
- Modify: `DelveEngine.ChooseDoorAsync` (grant the milestone titles in the same transaction as the record)
- Modify: `NetworkBroadcastSystem.cs` (`GET /api/v1/player/titles`, `POST /api/v1/player/title`; `/api/v1/players/profile` gains `ActiveTitle`)
- Modify: `client_web/src/lib/net/rest.ts`, the profile modal (`PlayerProfileModal.svelte`, rendered from `App.svelte`), `Delve.svelte` (a title picker: **buttons, not a `<select>`**)
- Create: `server/FolkIdle.Server.Tests/TitleTests.cs`, `client_web/tests/titles.test.ts`

- [ ] **Step 1: Failing tests:**
  - crossing floor 10 grants `deep_10` exactly once, even when replayed;
  - setting an unearned slug gives `NotEarned`, and nothing changes;
  - clearing the title gives `null`;
  - the profile answer carries the active title's display name;
  - the slugs are unique and match `^[a-z0-9_]{1,32}$` (a registry test);
  - the client's copy of the display names, **if one exists**, matches the server's list element by element (`serverMirrors.test.ts` style). Better: the client renders the name the server sends and holds no list at all.
- [ ] **Step 2: Implement, check `runesMode.test.ts` stays green, and commit** `feat(titles): a minimal title system, first earned in the Deep`.

### Task 2.3: the Deepest board

**Files:** `NetworkBroadcastSystem.cs` (`GET /api/v1/leaderboard/deepest`, a read-only SQL query per spec §4), `client_web/src/routes/Leaderboards.svelte` (a tab), `rest.ts`, `server/FolkIdle.Server.Tests/DeepestBoardTests.cs`

- [ ] **Step 1: Failing tests:**
  - the board orders by `DelveDeepestThisWeek` descending, then by the earlier `DelveDeepestThisWeekAtUtc`;
  - last week's rows are excluded;
  - players under `MinimumRankedLevel` are excluded;
  - **`LeaderboardPayoutEngine` reads only `leaderboard:mastery`** (grep its source for other key literals);
  - no file under the Deep and board code assigns `PremiumDiamonds`.
- [ ] **Step 2: Implement, UI tab** (a windowed list is not needed at 100 rows; check `check:clipping` at 360 px), **commit** `feat(delve): a weekly Deepest board that pays nothing`.

### Task 2.4: verify, docs, PR

- [ ] **Step 1:** Extend the exercise: in the Deep at 0 charges, buy one lantern at the quoted price `P` and assert the gold fell by exactly `P`. Open the Deepest tab and assert the fixture's row shows its record. Set a title and assert it shows in the profile modal, then clear it (round-trip).
- [ ] **Step 2:** Full verification in the `verify` skill's order: `dotnet build`, the full `dotnet test` (run alone), `npm run check:ratchet`, `npm test`, `npm run build`, `npm run exercise` twice, and the geometry checks.
- [ ] **Step 3: Docs:**
  - `docs/TASK_BOARD.md` task 37 marked DONE, with the before/after sink table;
  - `CURRENT_IMPLEMENTATION_STATE.md` (the new columns, `player_titles` in §3's snake_case table, the routes, `EconomyDecisions`);
  - `NEXT_STEPS_BACKLOG.md` (the top section);
  - the in-game Wiki (`wikiData.ts` + `tests/wiki.test.ts`).
- [ ] **Step 4:** PR "task 37 phase 2". Run `code-review`, merge, and deploy (the `deploy` skill).

## Phase 3 - measure (no code)

- [ ] **Step 1:** Run `npm run smoke:screens` against production, which is the only check safe there.
- [ ] **Step 2:** One week later, run read-only SELECTs over `EcoTelemetryLedgers` daily deltas, `"PlayerRecords"."DelveDeepestFloor"` for the top account, and the count of `player_titles`. Compare the share of gross income absorbed against the Phase 0 baseline (a lower bound of 19% or more today). Record the results in TASK_BOARD task 37.

## Phase 4 (later, after the Deep ships) - Patronage

Spec §7. Its own plan when started. The outline:

- a migration (`PlayerRecords.PatronageLevel int`);
- `PatronageRegistry.Price(L) = C0 x 1.2^L` (`C0 = 100_000`, saturating);
- `POST /api/v1/patronage/pledge {QuotedPrice}` (a DB debit, `ReloadState`);
- `patron_*` titles through `TitleRegistry`;
- a Great Hall panel in the Village;
- titles and banners in chat and on the main leaderboard row;
- tests: no stat or diamond write (a row diff), and nothing outside display code reads `PatronageLevel` (a grep).

## Risks

- **Gold parked with an alt** lowers the stake (spec §8 question 2). It is accepted for v1 and written in a `// Modul:` on `Stake`.
- **The income profile may not land within 3x of 10M/h.** Then the model is wrong. Find out why (codex damage, crit, attack speed) before pricing anything off it. That investigation is the point of Phase 0.
- **The `DelveWeekKey` sharing** between the diamonds and the Deep records is the one subtle state rule. Test both orders: record first, then bank; and bank first, then record.
