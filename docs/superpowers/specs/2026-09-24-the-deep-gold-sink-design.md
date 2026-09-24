# The Deep: a late-game gold sink (task 37), design

Date: 2026-09-24
Status: SPEC. The owner decided this on 2026-09-24. Plan:
`docs/superpowers/plans/2026-09-24-task-37-the-deep.md`.
Brief, evidence and option menu: `docs/superpowers/plans/2026-09-23-task-37-late-game-gold-sink-design.md`
(the measured economy is in its §1 and is not repeated here).

---

## 1. Decisions (owner, final, 2026-09-24)

| # | Question | Answer |
|---|---|---|
| 1 | Option | **B, "The Deep"**, now: an endless, gold-tolled continuation of the Delve past floor 8 |
| 2 | Before it | **Phase 0: measure income honestly.** Fix the test income model, which is 13-38x wrong |
| 3 | Pricing | `stake = max(region fee, StakeFraction x max(gold held, 7-day high-water mark))`, frozen when the descent begins. Tolls grow about 1.25x a floor. Lantern refills double. The pass requirement is capped at floor 8, with a `DeepDecay` on the pass chance |
| 4 | What it pays | **Records + titles** (a minimal title display) + a **weekly "Deepest" board** that pays **no diamonds and nothing convertible** |
| 5 | `GlobalGoldDropMultiplier` | **Stays 75**, but becomes an explicit, named constant and decision, so the economy audit can never silently move it again |
| 6 | Patronage | A **later phase**: a cosmetic monument track at `C0 x 1.2^L`, no stats, reusing the Deep's title display. Specced briefly in §7 |
| - | Not now | A (a wealth-scaled Delve gate), E (Masterwork forge), F (shrine), and chest-sale pricing. Guild Great Works go to task 38 |

## 2. Phase 0: measure income honestly

**The fault:** `GoldSinkAffordabilityTests.KillsPerHour = 180` models about 369k gold an hour in region 5. The one top account measures **5-14M/h, with a working figure of about 10M/h** (brief §1.2). Every "minutes of play" assertion passes against a player who does not exist.

**The fix:**

1. **A top-of-game income profile** beside the old model, not replacing it (the old one still prices early-game sinks):
   - kills per hour = `3600 / CombatDamageModel.ExpectedSecondsPerKill(...)` for the geared level-94 reference sheet that `ProgressionRateTests` already builds, against the strongest region-5 regular (reuse its profile; do not invent one);
   - multiplied by the full per-kill gold formula (`SimulationEngine.cs:~4608`, mirrored at `OfflineSimulationEngine.cs:~717`) at the constant from point 3.

   The test **asserts** the result lands within 3x of the measured 10M/h. If it does not, the model is wrong, not the player.
2. **The gold sinks as an asserted table.** One row per sink: reroll, fusion, village level, feast n = 16/20/25, Delve gate, and (after Phase 1) the Deep. Each is priced in minutes of top income. The one assertion that matters: **at least one repeatable sink can absorb 30% or more of an hour of top income.** It fails today, which is the point of the task, and turns green with the Deep.
3. **`GlobalGoldDropMultiplier` becomes a decision:**
   - `EconomyDecisions.CombatGoldPercent = 75` (a new `server/FolkIdle.Server/Engine/EconomyDecisions.cs`), with a `// Modul:` paragraph recording that 75 was an accident of the audit (brief §1.4) and was kept deliberately on 2026-09-24;
   - both kill formulas read the constant;
   - **the mutable field `GlobalEngineState.GlobalGoldDropMultiplier` is deleted**;
   - `EcoTelemetryEngine.ExecuteAuditAsync` keeps computing and recording the ratio and its telemetry event (`EventType 6, Value1 44`), but **no longer writes any multiplier**.

   **A finding on the way (standalone write-up in §9):** the field defaults to **100** and drops to 75 only when an audit pass succeeds, so every restart pays **full** combat gold until then, which is 10 minutes or more if the first pass throws. The constant removes that window too.

   A test pins it three ways:
   - an audit run whose ratio is inside `[0.85, 1.15]` leaves the per-kill gold unchanged;
   - an audit run outside the band leaves it unchanged;
   - a source grep finds no assignment to anything named `GoldDropMultiplier`.

## 3. The Deep: rules

### 3.1 Getting in

- **Only from the bottom of a live Delve run**: `FloorsCleared == FloorCount (8)`, where today the only action is Bank. The view offers **Walk out** (today's bank) and **Descend into the Deep: toll X**.
- **Descending banks floors 1-8 first**, in the same transaction and by the exact code path `BankAsync` uses: diamonds within `MaxDiamondsPerWeek`, consolation gold past it. The Deep therefore can never put a diamond at risk nor add one. It is **diamond-neutral by construction**, not by a rule someone has to remember.
- **Stake**, computed at the moment of descent:
  `stake = max(DelveRegistry.EntryFeeForRegion(highestRegion), floor(StakeFraction x wealth))`, with `StakeFraction = 0.005`, where
  `wealth = max(gold on the locked row after banking, the player's 7-day gold high-water mark)`.
  **Owner decision, final:** the stake is priced on the **maximum gold held over the last 7 days**. Mailing gold to an alt before descending does not lower it. The high-water mark is specced in §3.4.
  At 492M held, the stake is 2.46M. A small holder pays the region fee floor (7k-250k).
- **The stake is frozen on the run row** (`StakeGold`). Spending or earning mid-run changes no later price.
- **Quote guard:** the descend request carries `QuotedStake` from the view. If the server's own stake is **higher**, it answers `PriceChanged` with a fresh view and changes nothing. The server never charges the client's number. It only refuses to charge more than the player was shown, because held gold, and so the stake, rises with income between view and press.
- **Toll to enter floor d** (d >= 9): `toll(d) = saturating(stake x TollGrowth^(d - 9))`, with `TollGrowth = 1.25`. Descending to floor 9 costs `toll(9) = stake`. If gold after banking is below the toll, the whole transaction rolls back and answers `NotEnoughGold` with the quoted toll. The run stays at the bottom, and the player can still walk out.

### 3.2 Inside

- **The doors:** three doors, attribute demands as today, and the Fortune reveal as today.
- **The pass chance:**
  `DeepSuccessChance(v, d) = max(MinSuccessChance, SuccessChance(v, FloorCount) x DeepDecay^(d - 8))`, with `DeepDecay = 0.97`.
  The requirement is **capped at floor 8's value** (`RequirementForFloor(8)` = 269), so depth stays sheet-driven. It **strictly decreases** with depth until it reaches `MinSuccessChance` (0.25), and it never exceeds `MaxSuccessChance` (0.90). It is a stated, diminishing curve (the `EveryUnboundedMultiplierIsAStatedCurve` style).
- **After a clear:**
  - the record updates (§4);
  - the player chooses **Walk out** or **Descend to d+1** for `toll(d+1)`.

  There is **no ember bank in the Deep**. Walking out ends the run, and nothing is paid.
- **After a fail:**
  - a lantern charge is lost;
  - charges carry over from the 1-8 run;
  - at 0 charges the player may **buy a lantern charge** at `lanternRefill(k) = saturating(stake x 2^k)`, where k is the number already bought this run (the first costs one stake);
  - **`MaxLanternRefills = 8`** (the 8th costs 128 stakes), after which `NoMoreLanterns`;
  - declining ends the run as `RunLost`. The record stands; only gold was spent.
- **Saturation:** every price is computed in `double` and clamped to `long.MaxValue / 4` before any sum, so no depth can overflow. A test drives floor 500.

### 3.3 Worked prices (the top account, 492M held, stake 2.46M)

| Push | Tolls | + 3 refills | Total | At 10M/h |
|---|---|---|---|---|
| to floor 12 | 14.2M | 17.2M | 31.4M | about 3 h |
| to floor 16 | 48.8M | 17.2M | 66.0M | about 6.6 h |
| to floor 20 | 133M | 17.2M | 150M | about 15 h, a night's income |

A small holder (1M gold, r5): the stake is 250k and floor 12 costs about 1.25M, which is not reachable without income. The region floor keeps the Deep from being free, and holdings keep it from being trivial.

Phase 2 asserts these bands (§6).

### 3.4 The 7-day gold high-water mark (owner decision, final)

This is the persistence spec the stake depends on.

**Table:** `player_gold_daily_high` (snake_case `[Table]`; add it to `CURRENT_IMPLEMENTATION_STATE.md` §3):

- `PlayerId bigint`, `DayUtc date`, `MaxGold bigint`;
- PK `(PlayerId, DayUtc)`.

It holds **one row per player per UTC day**, and at most 8 rows per player.

**Written by the durable checkpoint path, never by the Redis frame.**

- `StateCheckpointManager.FlushState` (the Postgres checkpoint; `TrackState`'s Redis frame does **not** write it) upserts
  `INSERT ... ON CONFLICT ("PlayerId","DayUtc") DO UPDATE SET "MaxGold" = GREATEST(player_gold_daily_high."MaxGold", EXCLUDED."MaxGold")`
  with `MaxGold = state.CurrentGold`, the payload's live balance, which already includes gold not yet banked through `RedisPendingGoldDelta`. It does this **inside the checkpoint's own transaction**, so a failed flush writes nothing.
- The same statement deletes that player's rows with `DayUtc < today - 7`, lazily.
- CLAUDE.md: "A Redis frame is not a checkpoint". The frame is twelve fields and this is not one of them, deliberately.

**Also sampled:**

- **(a) at login hydration**, from the gold row that was just read, which covers a player whose wealth arrived offline;
- **(b) inside `DescendAsync`**, from the locked gold row before the stake is computed. This path is authoritative even when no checkpoint has run today.

**Read:** `wealth = max(lockedGold, SELECT max("MaxGold") FROM player_gold_daily_high WHERE "PlayerId" = @p AND "DayUtc" >= today - 6)`. That covers 7 UTC days including today.

**Why daily rows and not one column:**

- a single "max ever" never decays, so a player who really did spend down would pay on old wealth for ever;
- a single "max since N" cannot roll.

Eight small rows per player is the cheapest honest rolling window.

**Cost:** one upsert per checkpoint per online player. That is the same order of work as the checkpoint's existing `PlayerRecords` write, and adds no new connection or loop, so no `StartCron`.

**Tests:**

- a flush at 10M then a mail-out to 1M still prices the stake on 10M for 7 days, and on the lower value from day 8;
- a Redis-frame-only tick writes no row;
- a failed flush (rolled back) writes no row;
- the week's rows cap at 8;
- login hydration records offline wealth.

## 4. Rewards: records, titles, the board

**Records:**

- `PlayerRecords.DelveDeepestFloor` (all-time, the deepest floor **cleared**);
- `PlayerRecords.DelveDeepestThisWeek`, which shares `DelveWeekKey`.

**The week-key trap:** today only the bank path resets the week (it resets `DelveDiamondsThisWeek`). Every write of `DelveDeepestThisWeek` must apply the same rule first: if `DelveWeekKey != CurrentWeekKey(now)`, reset both weekly columns and set the key. The board filters `DelveWeekKey = current`, so a stale week never shows.

**Titles, a minimal system** (none exists today; grep finds no title, frame or cosmetic code):

- Table `player_titles` (snake_case `[Table]`, recorded in `CURRENT_IMPLEMENTATION_STATE.md` §3):
  - `PlayerId`, `TitleSlug` (varchar 32), `EarnedAtUtc`;
  - PK `(PlayerId, TitleSlug)`;
  - insert-only, idempotent.
- `PlayerRecords.ActiveTitleSlug` (nullable varchar 32).
- `TitleRegistry`, static in code: `{ Slug, DisplayName, Source }`.
  - **Slugs are strings, never positions.** CLAUDE.md: an index on the wire is a two-sources-of-truth surface.
  - v1 titles, granted when `DelveDeepestFloor` crosses a milestone:

    | Slug | Floor | Display name |
    |---|---|---|
    | `deep_10` | 10 | "Lamplighter" |
    | `deep_15` | 15 | "Deepwalker" |
    | `deep_20` | 20 | "Of the Dark Water" |
    | `deep_30` | 30 | "Lantern-Eater" |
    | `deep_40` | 40 | "Where No Bell Rings" |
    | `deep_50` | 50 | "The Bottomless" |

  - The names are final (owner, 2026-09-24). A later rename touches only `TitleRegistry`.
  - A title is granted in the same transaction as the record that earned it.
- **REST, not the wire:**
  - `GET /api/v1/player/titles` returns the earned titles and the active one;
  - `POST /api/v1/player/title { "Slug": "deep_15" | null }` sets it. An unearned slug answers `NotEarned`, visibly.
- **Where a title shows in v1:**
  - the profile modal (`/api/v1/players/profile` gains `ActiveTitle`);
  - the Deepest board rows;
  - the player's own Delve screen.

  Chat and the main leaderboard wait for Patronage (§7). **No `StateUpdatePacket` field**, so there is no hydration question.

**The weekly Deepest board:**

- `GET /api/v1/leaderboard/deepest` returns the top 100 by `DelveDeepestThisWeek` where `DelveWeekKey = current` **and** `CurrentLevel >= LeaderboardTierRegistry.MinimumRankedLevel`, which keeps the `exercise######` throwaways off it, as on the mastery board. Ties go to the earlier record time (add `DelveDeepestThisWeekAtUtc`).
- **It pays nothing:**
  - no diamonds, gold, items, or anything convertible;
  - no payout cron, so no `StartCron` and no `CronWorkerGuardTests` change;
  - it is **not a Redis ZSET**. `LeaderboardPayoutEngine` reads only `leaderboard:mastery`, and a test pins that it reads no other key and that no Deep code path writes `PremiumDiamonds`.
- The Leaderboards screen gets a "Deepest this week" tab.

## 5. Gold path, persistence, wire

- **DB debit only.** Every toll and lantern is debited from the `CommodityRecords` gold row, `FOR UPDATE`, inside `DelveEngine`'s existing one-Serializable-transaction-per-action shape, **in the same transaction as the roll or the floor change**. Never `RedisPendingGoldDelta`. The checkpoint applies that as an increment, so a debit written there does not stay debited.
- **After every gold-moving Deep action** (descend, lantern), the REST handler enqueues `ReloadState`, as the Delve already does for start and bank (`NetworkBroadcastSystem.cs:~2840`). Otherwise the header keeps the old balance.
- **Migration `AddTheDeep`** (additive):
  - `DelveRunRecords`: `IsDeep bool default false`, `StakeGold bigint default 0`, `LanternsBought int default 0`;
  - `PlayerRecords`: `DelveDeepestFloor int default 0`, `DelveDeepestThisWeek int default 0`, `DelveDeepestThisWeekAtUtc timestamptz null`, `ActiveTitleSlug varchar(32) null`;
  - new tables `player_titles` and `player_gold_daily_high` (§3.4).
  - Migrations run on the container ENTRYPOINT. Apply locally with `--migrate` before signing in.
- **REST:**
  - `POST /api/v1/delve/deep/descend { QuotedStake }` (from the bottom: bank + stake + toll(9); from a cleared Deep floor: toll(d+1));
  - `POST /api/v1/delve/deep/lantern`;
  - the existing `/api/v1/delve/door` and `/bank` work in the Deep, and `/bank` in the Deep is "walk out" (pays nothing, ends the run).
- **Results:** `DelveResult` gains `PriceChanged`, `NoMoreLanterns`, `NotAtTheBottom`, `DeepDisabled`, each with a client sentence. `NotEnoughGold` and `RunLost` are reused.
- **`DelveRunView` gains:** `IsDeep`, `StakeGold`, `DescendQuote` (the stake and toll(9) at the bottom, or toll(d+1) in the Deep), `LanternPrice` (when charges are 0), `LanternsBought`, `DeepestFloor`, `DeepestThisWeek`, `NextTitle { Slug, Name, Floor }`.
- **No `ClientCommandPacket` or `StateUpdatePacket` change.**
- **Flag:** `FOLKIDLE_DELVE_DEEP` = `off` (default) | `on`. With `off`, the view never offers Descend and the Deep routes answer `DeepDisabled`.

## 6. Guard rails, asserted

- **Diamonds:** descending, clearing, walking out, dying and buying lanterns in the Deep mint **zero** diamonds, before and after the weekly ceiling, and `DelveDiamondsThisWeek` moves only in the banking of floors 1-8.
- **Power:** the Deep writes **no** stat, attribute, codex, inheritance, skill or combat column. The test is a whole-row diff of `PlayerRecords` before and after a Deep session: only gold (the commodity row), the three Deep record columns, `DelveWeekKey` and `ActiveTitleSlug` may differ. No `PowerCeilingTests` ledger gains an entry.
- **The curve:** `DeepSuccessChance` never exceeds 0.90, strictly decreases until 0.25, and never goes below 0.25. Tolls strictly increase. Refills double, capped at 8. Prices are at least the region fee.
- **Affordability (Phase 0's table):**
  - at the top-income profile, stake + tolls to floor 12 + 3 refills is **0.5-4 h** of top income;
  - to floor 20 it is **8-24 h**;
  - the "30% of an hour is sinkable" row turns green.
- **Two gold paths:** a test asserts that no Deep code path references `RedisPendingGoldDelta` or `ChestSaleGoldQueue` (a source grep, as in `CronWorkerGuardTests`).

## 7. Later phase: Patronage (spec only; built after the Deep ships)

- **What it is:** a cosmetic monument track. "You have poured X into the Great Hall."
- **Price:** level L costs `C0 x 1.2^L`, with `C0 = 100,000`. It is unbounded: every level is 20% dearer, and L = 50 is about 910M cumulative. Paid by DB debit plus `ReloadState`.
- **What it pays:** titles and banners at milestone levels (`patron_5`, `patron_10`, `patron_25`, `patron_50`), through the **same `TitleRegistry` and `player_titles`**, and a banner tier shown beside the name.
- **What it must not do:** **no stat, no diamonds.** A "+1% per level" would be linear-and-uncapped, the one forbidden shape. A test asserts that nothing outside display code reads `PatronageLevel` (a source grep), and that its whole-row diff touches only gold, `PatronageLevel` and titles.
- **Display:** Patronage is where titles and banners reach **chat** and the **main leaderboard row**. That needs either a REST lookup cached per name, or a field on an existing roster DTO. If it ever travels on `StateUpdatePacket`, use the `add-command` skill and satisfy `StateUpdatePacketFieldCoverageTests`.
- **Surface:** `POST /api/v1/patronage/pledge { Levels: 1 }` (the server prices it; a `QuotedPrice` guard as in the Deep) and a panel in the Village (Great Hall).
- **Not a sole source of cosmetics:** Deep titles exist first, so Patronage is not the only way to look distinguished (the pay-to-look risk in the brief).

## 8. Owner answers (2026-09-24, final)

1. **Title names:** use the six proposed Deep titles (§4). The owner may rename them later. Names live only in `TitleRegistry`, keyed by slug, so a rename is a one-line change that touches no earned row.
2. **The stake is priced on the maximum gold held over the last 7 days** (§3.1, §3.4).

## 9. Standalone defect: combat gold pays 100% after every restart (may be pulled forward as its own fix)

**Files:**

| File | What |
|---|---|
| `server/FolkIdle.Server/Engine/GlobalEngineState.cs:11` | `public static volatile int GlobalGoldDropMultiplier = 100;` |
| `server/FolkIdle.Server/Engine/EcoTelemetryEngine.cs:~36-55` (loop), `~152-167` (the assignments) | the only writer |
| `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:~4608` | reader, live kills |
| `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs:~717` | reader, offline catch-up |

**What happens:**

- The multiplier starts at **100** on every process start.
- The only thing that lowers it to the value production has run on since 2026-08-05 (75) is `EcoTelemetryEngine.ExecuteAuditAsync`, when the audit ratio falls outside `[0.85, 1.15]`. It always does today (about 761).
- The audit loop runs a pass at start and then every 10 minutes. Its `catch (Exception)` only logs, so **if the first pass throws** (for example a pooler refusal, `EMAXCONNSESSION`, the documented failure mode on Supabase), the multiplier stays at 100 **until the next successful pass, 10 minutes or more later**.
- During that window every live kill and every offline catch-up computed at login pays **4/3 of normal combat gold**. After a deploy, every returning player's offline catch-up is computed in exactly that window.

**Fix:** §2 point 3. Make it a named constant (`EconomyDecisions.CombatGoldPercent = 75`), delete the mutable field, and stop the audit from writing it.

**The test pins it:**

- the per-kill gold on a freshly constructed engine, before any audit, is the 75% value;
- the audit, in or out of band, leaves it unchanged;
- no assignment to `GoldDropMultiplier` exists in the tree.
