# Task 37: a late-game gold sink (design brief and phased plan)

> **Status: DESIGN BRIEF. Talk it through with the owner before building anything.**
> Nothing below is decided. Sections 1 and 2 are the evidence and the options,
> section 3 is a recommendation and the questions the owner has to answer, and
> section 4 is a phased plan for the recommended option. Rewrite section 4 if the
> owner picks something else.
>
> **For agentic workers:** once the owner has answered section 3, use
> superpowers:subagent-driven-development or superpowers:executing-plans and work
> task by task. Steps use checkbox (`- [ ]`) syntax.

**Spec:** `docs/TASK_BOARD.md` "## 37. A late-game gold sink" (~line 3571). Task 11 ("DONE 2026-09-09 — 11. Gold has nowhere to go", ~line 1763) is the Delve design this builds on.

**Evidence gathered 2026-09-23:** read-only SELECTs on production (Supabase), plus the server source at `219d2bf`. No builds, tests or servers were run.

---

## 0. Summary

- **The owner is right, and the gap is bigger than it looks.** Every price in the
  game was set against `GoldSinkAffordabilityTests`' income model, which assumes
  **one kill every 20 seconds, or 369,000 gold an hour in region 5**. The one
  account at the top (level 94, Death Knight) earns **about 5 to 14 million gold
  an hour**, measured from its own offline catch-ups. That is **13 to 38 times
  the model**. It holds **491.9M gold**, plus materials worth about **620M more**
  at chest-sale prices.
- **The Delve's 250k gate costs 1 to 3 minutes of that income, not the designed
  40.** Hitting the diamond ceiling takes about three full clears a week, which
  is under 1M gold a week against roughly 100M+ earned per overnight session.
- **Only one sink has kept any weight: the villager feast**, because it is the
  only price that climbs with use (`2,500 × 1.6^n` per season). The next feast
  costs 4.6M. The reroll gets its weight from sheer volume (26,576 rerolls
  lifetime, up to 266M). Everything else is a flat price, and at the top of the
  game a flat price does nothing.
- **Two latent faults the brief has to account for** (section 1.4): the economy
  audit has pinned combat gold to **75%** since the day it went live, and the
  income model in the tests is wrong by more than an order of magnitude.
- **Recommendation (for the owner to confirm): "The Deep"**, an endless,
  gold-tolled continuation past Delve floor 8. Tolls and lantern refills are
  priced as a **stake: a percentage of the gold the player holds, frozen when the
  descent starts**, and climb with every floor. It pays **records and cosmetics,
  not diamonds and not power**. Before it, a **Phase 0** fixes the income
  measurement so this sink and every later one are priced against real numbers.

---

## 1. The measured economy

### 1.1 Who holds the gold (production, 2026-09-23)

| Measure | Value |
|---|---|
| Accounts | 50 |
| Accounts at level 5 or above | **1** (`Mivoru`, level 94, the owner) |
| Total gold in `CommodityRecords` (`ItemId = 'gold'`) | 492,013,590 |
| Held by the one level-94 account | **491,883,676 (99.97%)** |
| Median gold per account | 2,000 |
| That account's `TotalPlayTimeSeconds` | 62 h |
| `AffixRerollsPerformed` | **26,576** |
| `ForgeFusionsCompleted` | 0 |
| `VillagerRecruitmentsThisSeason` | 16 (next feast: 4.6M) |
| `DelveDiamondsThisWeek` | 60 (ceiling reached) |
| `AutoSalvageBelowTier` | 0 (auto-salvage is off, so none of its gold is from salvage) |
| Guild `TJ Jiskra NB` | tier 13, treasury 5.5M |
| Market orders ever | 1 (0 filled) |

The top account's material stock, valued at the chest's vendor price
(`VillageChestEngine.ValueMaterial` = `BaseValueGold × 0.40`):

| Material | Held | Vendor/unit | Worth |
|---|---|---|---|
| frostpine_log | 1,831,272 | 128 | 234M |
| silver_ore | 1,670,862 | 128 | 214M |
| darksteel_ore | 83,080 | 512 | 43M |
| cobalt_ore | 275,937 | 128 | 35M |
| golden_frostpine_log | 267,360 | 128 | 34M |
| acacia_log + sulfur_ore | 1.6M | 32 | 52M |
| others | | | ~12M |
| **Total** | | | **about 620M** |

The account can therefore **more than double its gold** by selling out of the chest, at any time.

### 1.2 Income at the top, measured

`EcoTelemetryLedgers` writes a snapshot of total gold every 10 minutes (7,209
rows since 2026-08-05). With one real holder, that series is effectively that
account's durable balance. The balance moves only at a checkpoint or a login, so
the big steps are offline catch-ups:

| Window | Balance step | Elapsed | Implied rate |
|---|---|---|---|
| 2026-09-18 10:00 → 09-19 07:25 | **+170.9M** in one snapshot | ~21 h (capped at 12 h, or 18 h with Vodnik mastery ≥25) | **9.5 to 14M/h** |
| 2026-09-22 16:00 → 09-23 12:26 | **+81.3M** in one snapshot | ~20 h (same cap) | **4.5 to 6.8M/h** |
| 2026-09-01 → 09-23, all positive steps | +633.8M | 22 days | about 29M/day |
| 2026-09-01 → 09-23, all negative steps | **−152.1M** (lower bound; netted per 10 min) | | at least 19% of gross |

The kill formula (`SimulationEngine.cs:4608`, mirrored offline at
`OfflineSimulationEngine.cs:717`) is `BaseGoldReward × GlobalGoldDropMultiplier/100`
× (1 + Human 5%) × legacy perk × (1 + 2% per guild Gold buff tier) × inheritance
× Trophy Hunter on bosses. For the Death Knight (2,050 base) at the live 75% that
is about **1.6k a kill**. 14M/h would mean roughly 2.4 kills a second, which a
level-94 sheet with a 5.76x codex damage multiplier can plausibly reach against
69,700 HP. The telemetry cannot separate combat gold from manual chest sales, so
treat **~10M/h as the working figure and 5M/h as the floor**.

### 1.3 Every source and sink, at the top of the game

Assumed: **~10M/h measured income** (section 1.2) against the tests' **369k/h model**.

**Sources**

| Source | Formula / where | Scales with | ~gold/h at lvl 94, region 5 | Gold path |
|---|---|---|---|---|
| Combat kill (live) | `BaseGoldReward × GGDM/100 × multipliers`, `SimulationEngine.cs:4608` | monster (geometric per region) × kill rate | **5 to 14M** (measured) | payload: `AddGold` + `RedisPendingGoldDelta` |
| Combat (offline) | same, `OfflineSimulationEngine.cs:717`, 12 h cap (18 h Vodnik) | same | same rate, **~60 to 170M per night** | payload, same |
| Chest sale, materials | `BaseValueGold × 0.40 × qty`, `VillageChestEngine.cs:55` | gathering rate × region value | latent **~620M** stock; one geared r5 gatherer ≈ 9k units/h ≈ **1.1M/h at 128/unit** (formula estimate, `GatheringToolEngine`) | DB credit + `ChestSaleGoldQueue` (CurrentGold only) |
| Chest sale, equipment | `BaseValueGold × (1 + 0.5·tier) × 0.40` | rarity × region | small next to materials | same |
| Auto-salvage | same value as a sale, `CombatLootEngine.cs:984` | drop rate 5% | 0 on this account (off) | payload: `AutoSalvageQueue` |
| Town Hall | 50 → 3,000/h by level, `VillageManagementEngine.cs:60` | flat | ≤3k | payload |
| Daily login | 500 → 10,000/day | flat | ~0 | |
| Delve consolation | `fee × 0.5 × depth/8` once past the ceiling | region fee | ≤125k per run | DB path |
| Market / mail | transfers between players, not minting | | ~0 (1 order ever) | |

**Sinks**

| Sink | Formula / where | Scales with | Max realistic spend/h at the top | Share of 10M/h |
|---|---|---|---|---|
| Affix reroll | flat 1k/2k/4k/5k/10k by region; streak growth deliberately 1.0, `AffixRegistry.cs:614` | region only | volume-driven: 26,576 lifetime (≤266M); one reroll = **0.1%** of an hour | tiny per click, large only by volume |
| Fusion fee | `200 × 1.35^tier`, max 4,022, `ForgeSplicingEngine.cs:275` | item tier | unused (0 fusions) | 0.04% |
| Village upgrade | `500 × 1.4^level` (L12 = 28k, L20 = 419k), one-off | level | one-off | ~0 |
| Villager feast | `2,500 × 1.6^n` per season, `VillagerArrivalRules.cs:142` | **climbs with use** | next is 4.6M, 20th 18.9M, 25th 198M, bounded by the Inn's population cap | **the only sink with weight** |
| Breeding | `500 × (gen + 1)` | generation | ~0 | ~0 |
| Delve gate | 7k/17k/42k/100k/250k by region, `DelveRegistry.cs` | region only | a few runs a week (3 clears reach the cap) → **<1M/week** | 2.5% per run; ~0.1% of weekly income |
| Guild gold donation | uncapped; 10 gold = 1 guild exp; tier T→T+1 needs `1000·(T+1)` exp, **one tier per call**, `GuildContributionEngine.cs:64/130` | linear | unbounded, but buys only guild rank + war defender HP (`GuildMatchmakingEngine.cs:215`) | whatever the player chooses |
| Guild raid restart | `5,000 × tier`, `GuildRaidEngine.cs:147` | tier | ~0 | ~0 |
| Market fee | 5% of a fill | price | ~0 (no market) | ~0 |

**Conclusion.** Recurring income at the top is ~10M/h. Every recurring sink is
either flat and priced for a 369k/h world (reroll, Delve, fusion) or climbs but is
capped by something else (feast → Inn population). The only unbounded sink,
guild donation, gives nothing a solo player wants. That is the "100M is easy"
report, stated in numbers.

### 1.4 Two latent faults found on the way (must go into the brainstorm)

1. **`GlobalGoldDropMultiplier` has been 75 in production for its whole life.**
   `EcoTelemetryEngine.ExecuteAuditAsync` counts as "consumed" only guild gold
   donations plus an estimated 5% market fee (571,000 lifetime). The reroll, the
   Delve, the feast, the village and fusion are all invisible to it. The ratio
   has sat far outside `[0.85, 1.15]` since 2026-08-05 (761 today), so the
   throttle at line 152 fires on every run and **every kill pays 75%**. Nobody
   decided that; it is a side effect. **Danger:** if a new sink is written to
   `GuildMaterialSinkLedgers` or the audit is "fixed" to count real sinks, the
   ratio could fall into the band and **combat gold jumps by a third overnight**.
   The owner should choose explicitly: keep 75 as a constant, go to 100 and
   rebalance, or retire the throttle.
2. **The income model in the tests is wrong by 13 to 38 times.**
   `GoldSinkAffordabilityTests.KillsPerHour = 180`, and `AffixRegistry`'s comment
   says "roughly 564,000 earned across an entire levels 1-100 playthrough". The
   live account holds 870 times that. Every "minutes of play" assertion in that
   file passes against a player who does not exist. This is the *number a test
   prints is not a number a test checks* rule one level down: the test asserts,
   but against a constant nobody measured. Any sink priced from a fixed table
   will be stale again after the next balance pass.

---

## 2. Candidate sinks

Hard constraints on every option:

- **Diamonds:** no new diamond tap past `DelveRegistry.MaxDiamondsPerWeek` (60).
  Note that `LeaderboardRewardTests.First_place_does_not_out_earn_the_delve` pins
  first place at ≤60 on its own, so the two taps already stack to 120 a week.
  A new board must not add a third.
- **Power:** nothing gold buys may be a new multiplier in
  `PowerCeilingTests.TheDamageLedgerStaysInsideItsBand` /
  `TheYieldLedgerStaysInsideItsBand`, or a linear-and-uncapped curve
  (`EveryUnboundedMultiplierIsAStatedCurve`). Gold turning into power turns the
  sink into a new faucet: more power means faster kills means more gold.
- **Gold path:** off-tick, DB-debit path only. That means a Serializable
  transaction, `CommodityRecords` gold row `FOR UPDATE`, then a `ReloadState` (or
  a `…Notification` with `GoldSpent`, as `VillageTickCoordinator` does), exactly
  as `DelveEngine` and `BreedingEngine` do. **Never** touch
  `RedisPendingGoldDelta` for a debit. The checkpoint applies that delta as an
  increment, so a debit written there does not stay debited.
- **Scaling:** a percentage of wealth, or a price that climbs with use. Never a
  flat table (task 37's own words, and section 1.4 point 2).
- **Held gold also feeds legacy shards** at season end
  (`SeasonalRotationEngine.CalculateLegacyShards`: `12.5 × log10(gold)`, so 492M
  gives ~108 shards and 10M gives ~87). Draining a hoard costs only a few shards,
  and that is acceptable.

### A. Rescale the Delve gate: priced against wealth or income

- **Fun hook:** none new. It makes the existing run feel like a stake again.
- **Formula:** `fee = max(RegionFee(region), StakeFraction × goldHeld)`, with
  `StakeFraction` of about 0.5%. At 492M that is 2.46M, about 15 minutes of
  measured income. The alternative, `fee = 40 min × personalIncomePerHour`, needs
  a durable per-player income estimate (section 3, question 3).
- **Must NOT:** raise the diamond payout (the ceiling does not move), or refuse
  entry at the ceiling (the consolation still pays 50%). It must not punish a new
  player either, and the region floor ensures that.
- **Gold path:** DB (unchanged `DelveEngine.StartRunAsync`).
- **Work:** `DelveRegistry.EntryFeeFor(region, goldHeld)`, one call site, the
  view's quoted fee, a test. No wire change (the Delve is REST, `/api/v1/delve`).
- **Effort: S.** It is weak on its own: at the cap the run is played three
  times a week, so even a 2.5M fee is under 10M/week against ~700M earned.
- **Inspiration:** Old School RuneScape's Death's Coffer / death fees
  (a percentage of risked value).

### B. "The Deep": endless, gold-tolled depth past floor 8 (recommended)

- **Fun hook:** the Delve's push-your-luck, with no floor at the bottom. Past
  floor 8 each floor charges a **toll**, and when the lantern runs out the player
  may **buy another charge at a doubling price**. That is the arcade "continue?"
  moment, Farkle's hot dice, and Diablo III's Greater Rifts: the depth record is
  the trophy. Every descent sets a personal record that shows on the profile and
  on a "Deepest this week" board.
- **Formula:**
  - `stake = max(DelveRegistry.EntryFeeForRegion(region), StakeFraction × goldHeldAtDescent)`,
    frozen on the run row when the descent begins (spending mid-run cannot game it).
  - `toll(d) = stake × TollGrowth^(d − 9)` for d ≥ 9, with `TollGrowth` ≈ 1.25.
  - `lanternRefill(k) = stake × 2^k` for the k-th charge bought this run.
  - The check past floor 8 **must not** use `RequirementForFloor` as it stands:
    requirements past 269 exceed every sheet, the chance pins to
    `MinSuccessChance` 0.25, and with 3 charges the expected depth past 8 is about
    one floor, which makes it a lottery. Proposal: requirement caps at floor 8's
    value, and the pass chance decays by `DeepDecay^(d − 8)` (≈0.97) on top of
    `SuccessChance`. Depth stays sheet-driven and diminishing, and it is never
    certain.
  - Worked number at 492M held, StakeFraction 0.5%: stake 2.46M, floors 9–20 in
    tolls ≈ 2.46M × (1.25^12 − 1)/0.25 ≈ 136M; three refills ≈ 17M. A deep push is
    **about a night's income**, and it scales itself as income changes.
- **Must NOT:** pay diamonds (the Deep pays **nothing convertible**: records,
  titles/frames at depth milestones, and the board), grant attributes/stats, or
  feed any ledger in `PowerCeilingTests`. The weekly board pays no diamonds, or it
  pays inside the existing leaderboard cap (owner's choice).
- **Gold path:** DB debit per toll/refill inside `DelveEngine`'s existing
  one-transaction-per-action pattern; `ReloadState` after.
- **Work:** `DelveRunRecords` gains `IsDeep`, `StakeGold`, `LanternsBought`,
  `DeepestFloor`; `PlayerRecords` gains `DelveDeepestFloor` (+ week best).
  Migration (additive). `DelveRegistry` rules. Two REST verbs
  (`/api/v1/delve/deep/descend`, `/api/v1/delve/deep/lantern`). `Delve.svelte`
  shows the toll/refill quote and the record. Cosmetic display needs a small
  title/frame system, because **none exists today** (grep finds no
  title/frame/cosmetic code). v1 can render the record as a title string.
- **Effort: M** (plus S for a minimal title display).

### C. Patronage: a prestige/cosmetic monument track

- **Fun hook:** "you have poured X into the Great Hall". An unbounded level
  shown next to the name in chat, the leaderboard and the profile, with visual
  tiers (bronze→legendary banners). It is Cookie Clicker's building curve
  (`base × 1.15^n`) aimed at status rather than production, plus the RuneScape
  Construction feel of spending on something you can see.
- **Formula:** level L costs `C0 × 1.2^L`, where C0 ≈ 100k. L = 50 is ~910M
  cumulative, and every level is a fixed percentage dearer than the last.
- **Must NOT:** give any stat (a "+1% per level" would be linear-and-uncapped,
  the one forbidden shape), or pay diamonds. It must not be the only place a
  cosmetic can be earned either, or it reads as pay-to-look.
- **Gold path:** DB.
- **Work:** a column on `PlayerRecords`, a REST verb, a village or profile
  panel, and **the cosmetic rendering system that does not exist yet** (name
  badge in chat, leaderboard row, profile modal). If a status field travels in
  `StateUpdatePacket` it needs `generate:protocol` and hydration at login
  (`StateUpdatePacketFieldCoverageTests`).
- **Effort: M-L**, most of it in the cosmetics.

### D. Guild Great Works (ties into task 38)

- **Fun hook:** the guild builds something together: a keep, a siege engine, a
  war banner. Weekly goals climb with the guild's own past contributions, and a
  finished Work unlocks a Guild-War-only perk (a fortification, an extra attack
  turn) or a cosmetic hall. It is IdleOn's guild bonuses and Melvor's
  Township-style shared build, placed where the multiplayer game is.
- **Formula:** `goal(week) = max(floor, 1.1 × lastWeekContributed)`, so it
  follows the guild's real income.
- **Must NOT:** pay into the PvE power ledger (guild buff tiers already feed
  damage/xp/gold/drop at 2% a tier, and **the Gold buff is a gold multiplier, so
  gold→more gold is a loop**). War perks must be bounded within the war system
  that task 38 designs. The existing guild gold donation is already uncapped, is
  one tier per call, and feeds war defender HP linearly; a Work must fix or
  replace that, not stack on it.
- **Gold path:** DB (`GuildContributionEngine.ContributeGoldAsync` shape).
- **Work:** depends on task 38's format. **Effort: L**, and **blocked on 38**.
  With one active guild today it would absorb only what one player puts in.

### E. High-roll forge services

- **Fun hook:** a "Masterwork" service. It pays to re-roll only the **magnitude**
  of one affix inside its current rarity band, with the price climbing per attempt
  on the same item and resetting weekly. It is the chase that 26,576 rerolls show
  the owner already enjoys, with a price that follows his wealth.
- **Formula:** `price(k) = max(RerollRegionPrice, 0.02% × goldHeld) × 1.15^k`
  for the k-th attempt on this item this week.
- **Must NOT:** exceed an affix's authored max (the `PowerCeilingTests` damage
  ledger already prices "weapon damage affixes" at their max, so moving *toward*
  the max adds no ceiling). It must not touch the diamond-priced rarity upgrade
  (`CalculateRarityUpgradeDiamondCost`), and it must not bring back the streak
  multiplier on the ordinary reroll. `AffixRegistry.cs:592` records why that was
  removed: it priced the Legendary chase out of the game.
- **Gold path:** DB (`AffixRerollEngine` pattern).
- **Work:** a reroll operation variant. `RerollOperation` exists, but
  `ClientCommandPacket` is fixed-layout (`add-command` skill), so REST is
  cheaper. Forge UI. **Effort: M.** Risk: it does add effective power to the top
  account (more max-rolled affixes), just not past the ceiling.

### F. Escalating-price consumables and offerings

- **Fun hook:** a shrine where gold buys a timed blessing, with each purchase
  this week dearer than the last (Melvor's potions, Clicker Heroes' clickables).
- **Must NOT:** buy XP, damage, drop or gold rate. Every one of those is a
  lever, and gold-rate is a loop. What remains are QoL effects (auto-eat food
  efficiency, a longer offline cap), and **the offline cap is income**: 12 h to
  18 h is +50% gold. **Not recommended.** It is listed so the owner sees why.
- **Effort: S-M**, but the risk is high.

### Also considered

- **Material buy-back ("the Quartermaster")**: gold for rare crafting
  materials at climbing prices. Rejected, because materials are already in
  massive surplus (section 1.1).
- **Bank/storage slots** (Melvor's escalating bank-slot price): no bank limit
  exists (the chest is unbounded by design), so there is nothing to sell.

---

## 3. Recommendation and questions for the owner

**Recommendation: Phase 0 (measure) + B (The Deep), then optionally A.**
Rationale:

- B is the only option that is **fun on its own terms** (it extends the one
  minigame the game has, which the owner plays to the cap every week),
  **self-scaling** (stake = % of holdings), **bottomless** (a record never
  finishes), and **power-neutral and diamond-neutral by construction**.
- A alone is too weak (3 runs a week). C needs a cosmetics system first. D waits
  on 38 and a second guild. E adds power at the top. F is a loop.
- **C is the natural follow-up.** The Deep's depth titles are the first
  cosmetics, and a Patronage track can reuse that display.
- Phase 0 comes first regardless of the option chosen. A sink priced against a
  369k/h model that is really 10M/h is the reason this task exists.

**Questions for the owner:**

1. **Is the target the top account's hoard, or the rate?** Draining 492M once is a
   different design from absorbing ~10M/h forever. B does the rate; a one-off
   monument (C) does the hoard.
2. **What share of income should be sinkable at the top?** Proposal: a committed
   player can put **30 to 60%** of an evening's income into sinks; nothing forces
   it.
3. **Price by wealth held, or by measured income?** Wealth needs no new
   persistence, is honest to tax a hoard, and a player can game it by spending
   first (which is fine, because spending is the goal). Income is fairer but
   needs a durable per-player estimate (e.g. written from the offline
   catch-up's `OfflineGoldEarned / elapsed`).
4. **What should the Deep pay?** Records + titles only? A weekly "Deepest" board,
   and if so, diamonds inside the existing leaderboard cap or none? A one-off
   trophy item at milestones (like the boss first-clear trophy)? Materials are out
   (surplus).
5. **Lantern refills: is a doubling "continue" fun, or does it feel like a
   slot machine?** An alternative is a fixed three charges and a deeper toll curve.
6. **`GlobalGoldDropMultiplier`**: keep 75 as an explicit constant, move to 100
   (+33% combat gold), or retire the throttle? It must be decided before any
   sink is recorded in the audit (section 1.4).
7. **Should chest sales stay at 40% of `BaseValueGold`?** 620M of latent gold sits
   in materials; a sink can be outrun by one sale. (Out of scope here, but it
   bounds what any sink can achieve.)
8. **Guild Works (D)**: bring into task 38's brainstorm as the war-economy sink?

---

## 4. How to approach this (for the implementing agent)

- **Read first:** `CLAUDE.md` (two gold paths; every multiplier declares a cap;
  a number a test prints is not a number a test checks; silent rollback),
  `DelveRegistry.cs`, `DelveEngine.cs` (all of it: this plan extends it, it does
  not write a second engine), `DelveTests.cs`, `GoldSinkAffordabilityTests.cs`,
  `GatheringEconomyTests.cs`, `PowerCeilingTests.cs`, `LeaderboardRewardTests.cs`,
  `client_web/src/routes/Delve.svelte`, and the Delve block of
  `client_web/scripts/exercise.mjs` (~line 1208).
- **Do not start before section 3 is answered.** Constants below
  (`StakeFraction`, `TollGrowth`, `DeepDecay`) are proposals.
- **Every rejection must be visible.** Not enough gold, no run, lantern price
  changed: return a result code and a view, as `DelveEngine` already does. A
  rollback with a dead button is the bug to avoid.
- **One gold path.** DB debit + `ReloadState`. If you find yourself writing
  `RedisPendingGoldDelta` in a debit, stop.
- **Migrations run on the container ENTRYPOINT.** Apply locally by hand
  (`--migrate`) before running the stack.
- **Stop the server before `dotnet build`**; Docker must be up for `dotnet test`.
- **No wire change is expected.** The Delve is REST (`/api/v1/delve*`,
  `NetworkBroadcastSystem.cs:1065`). If a field does go on `StateUpdatePacket`,
  use the `add-command` skill and satisfy `StateUpdatePacketFieldCoverageTests`.

---

## 5. Phased plan for the recommended option

### Global constraints

- Each phase is its own PR and is independently revertible.
- Diamonds: `DelveRegistry.MaxDiamondsPerWeek` does not change, and the Deep
  mints no diamonds. A test asserts that (Task 2.3).
- No new entry in any `PowerCeilingTests` ledger. A test asserts that the Deep's
  rewards are non-stat (Task 2.3).
- Every constant carries a `// Modul:` comment with the measured number it was
  priced against.

### Phase 0: measure income honestly (S)

**Files:**
- Modify: `server/FolkIdle.Server.Tests/GoldSinkAffordabilityTests.cs`
- Modify: `server/FolkIdle.Server.Tests/GatheringEconomyTests.cs`
- Modify: `docs/TASK_BOARD.md` (task 37: link this plan and the measured numbers)

- [ ] **Step 1: add a "top of the game" income profile beside the 180-kills model.**
  Derive kills/hour from `CombatDamageModel.ExpectedSecondsPerKill` against the
  Death Knight for a geared level-94 reference sheet (reuse `ProgressionRateTests`'
  geared profile rather than inventing one), × the full gold formula at
  `GlobalGoldDropMultiplier` = the value the owner chose (question 6). Assert it
  lands within 3x of the measured **~10M/h** (production, 2026-09-23). If it does
  not, the model is wrong, not the player.
- [ ] **Step 2: turn the gold sinks into an asserted table** (extend
  `GatheringEconomyTests`' "SINKS" section with a gold block, as task 37 asks,
  or a sibling fact in `GoldSinkAffordabilityTests`). One row per sink:
  reroll, fusion, village level, feast n = 16/20/25, Delve gate, Deep stake+tolls
  to floor 20. Print minutes-of-top-income, and **assert** that at least one
  *repeatable* sink can absorb ≥30% of an hour's top income (fails today; goes
  green with Phase 2).
- [ ] **Step 3: pin the audit behaviour** with a test naming the current
  `GlobalGoldDropMultiplier` outcome, so a future change to what the audit counts
  cannot silently change combat gold by a third.
- [ ] **Step 4: run** `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "GoldSink|GatheringEconomy"`
  (Step 2's new assert is expected red; mark it `Skip = "task 37 phase 2"` only
  if Phase 2 is not in the same PR, and say so in the PR).
- [ ] **Step 5: commit** `test(economy): measure top-of-game gold income, assert the sink table`.

### Phase 1: the Delve gate follows wealth (S, optional per question 3)

**Files:**
- Modify: `server/FolkIdle.Server/Engine/DelveRegistry.cs`: `EntryFeeFor(int region, long goldHeld)`.
- Modify: `server/FolkIdle.Server/Domain/Economy/DelveEngine.cs`: read the locked gold row **before** computing the fee (it is already locked in `StartRunAsync`); quote the same function in `GetViewAsync`.
- Modify: `server/FolkIdle.Server.Tests/DelveTests.cs`, `GoldSinkAffordabilityTests.cs`.

- [ ] **Step 1:** write failing tests: the fee is the region fee for a player
  holding little; it is `StakeFraction × held` for the top account (492M → 2.46M);
  the view's quoted fee equals the charged fee **to the gold**.
- [ ] **Step 2:** implement; keep `TheDelveCostsAboutAnEveningPerRegion` green
  for the region floor.
- [ ] **Step 3:** `exercise.mjs` Delve block: it already asserts "gold left to
  the exact fee". Confirm that it reads the fee from the server view, not a client
  constant.
- [ ] **Step 4:** commit `feat(delve): the gate is a stake on what you hold, floored by region`.

### Phase 2: The Deep (M)

**Files:**
- Create: migration `…_DelveDeep` (additive: `DelveRunRecords.IsDeep bool`, `StakeGold bigint`, `LanternsBought int`; `PlayerRecords.DelveDeepestFloor int`, `DelveDeepestThisWeek int`, reusing `DelveWeekKey`).
- Modify: `server/FolkIdle.Server/Models/*` (`DelveRunRecord`, `PlayerRecord`), `FolkIdleDbContext` if configured there.
- Modify: `server/FolkIdle.Server/Engine/DelveRegistry.cs`: `StakeFraction`, `TollForFloor(stake, floor)`, `LanternRefillPrice(stake, bought)`, `DeepSuccessChance(value, floor)`.
- Modify: `server/FolkIdle.Server/Domain/Economy/DelveEngine.cs`: `DescendDeepAsync` (from a banked-at-8 run or a fresh Deep entry for a player with `DelveDeepestFloor ≥ 8`), `BuyLanternAsync`; door resolution past floor 8 charges the toll in the same transaction as the roll.
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`: two POST verbs under `/api/v1/delve/`, the same auth + `ReloadState` enqueue as the existing ones.
- Modify: `server/FolkIdle.Server/Models/DevFixtureSeeder.cs`: fixture has `DelveDeepestFloor = 8` so the Deep is reachable; add to `DevFixtureInvariantTests`.
- Modify: `client_web/src/routes/Delve.svelte` (toll quote, lantern "continue" with its price, record), `client_web/src/lib/…` Delve API types (REST JSON, not the generated protocol).
- Tests: `server/FolkIdle.Server.Tests/DelveTests.cs` (or `DelveDeepTests.cs`).

- [ ] **Step 1: rules first (pure, no DB).** Failing tests:
  - toll strictly increases per floor; refill price doubles; both are ≥ the region gate.
  - `DeepSuccessChance` never exceeds `MaxSuccessChance`, strictly decreases with depth, and never goes below `MinSuccessChance` (a stated curve, `EveryUnboundedMultiplierIsAStatedCurve` style).
  - stake is frozen: spending gold mid-run does not change the next toll.
- [ ] **Step 2: engine + persistence.** Failing tests (Testcontainers):
  - a descent debits **exactly** the toll from `CommodityRecords` gold; not-enough-gold returns `NotEnoughGold` with a view, and **nothing** changes (no roll, no floor).
  - buying a lantern debits exactly the quoted price and adds one charge; a stale quote (client sent an old price) is refused visibly, and the client never sends a price at all (the server computes it).
  - `ADoorOutsideTheOfferedRangeIsRefusedAndChangesNothing` holds for Deep floors.
  - the run survives a relogin (row-backed; the view is rebuilt from the row).
- [ ] **Step 3: the guard rails, asserted (Task 2.3).**
  - banking or dying in the Deep **mints zero diamonds**, before and after the weekly ceiling; `DelveDiamondsThisWeek` is unchanged by any Deep action.
  - the Deep writes no stat, attribute, codex or inheritance column (assert the `PlayerRecords` row diff contains only the gold, the record columns and `DelveWeekKey`).
  - `GoldSinkAffordabilityTests`: Deep stake + tolls to floor 20 + three refills at the Phase 0 top-income profile is **between 0.5 and 3 hours** of top income, and the Phase 0 "≥30% of an hour is sinkable" assert turns green.
- [ ] **Step 4: client.** `Delve.svelte`: after a floor-8 clear show "Descend into the Deep: toll X", the per-floor toll, the lantern price when charges are 0, and the personal record. Use `{#if}`, not `<details>`; no `<select>`; ticking values are not inside controls; ≥44px targets. Run `npm run check:ratchet`, `check:touch`, `check:clipping`.
- [ ] **Step 5: `exercise.mjs`.** It must spend and round-trip (see CLAUDE.md "a check that spends fixture state passes once and fails forever"):
  1. read `/api/v1/delve`; record gold G0 and the quoted Deep toll T.
  2. descend; re-read; assert gold = G0 − T **exactly** and the server view shows floor 9.
  3. if charges hit 0, buy one lantern at quoted price P; assert gold fell by exactly P.
  4. walk out; **reload the page**; re-read; assert the run is closed, gold is still down by the total spent, `DelveDiamondsThisWeek` unchanged, and the record ≥ 9.
  Because the stake is a percentage of holdings, the fixture never runs dry. The region floor is the minimum, so seed the fixture with ≥10x the r5 gate (assert that in `DevFixtureInvariantTests`).
- [ ] **Step 6: docs.** `docs/TASK_BOARD.md` task 37 → DONE with the measured before/after table; `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` (new columns); Wiki entry for the Deep (the in-game Wiki); a tutorial objective if the owner wants one (`tutorialObjectives.ts`).
- [ ] **Step 7: verify** with the `verify` skill: `dotnet test` (Docker up), `npm run build`, `npm run exercise` on the local stack (re-seed first).
- [ ] **Step 8: commit per layer** (`feat(delve): the Deep rules`, `…engine and migration`, `…screen`, `test(exercise): the Deep spends and round-trips`).

### Phase 3: deploy and measure (S)

- [ ] **Step 1:** deploy with the `deploy` skill (SSH push; `git pull` does not work on the box; the migration runs on ENTRYPOINT).
- [ ] **Step 2:** `npm run smoke:screens` against production (the only check safe there).
- [ ] **Step 3:** one week later, read-only SELECT: `EcoTelemetryLedgers` daily deltas and the top account's `DelveDeepestFloor`. Compare the share of gross income absorbed against the Phase 0 baseline (≥19% lower bound today). Record the result in task 37.

### Not in this plan

- C (Patronage/cosmetics), D (Guild Works → task 38), E (Masterwork forge): follow-ups, pending the owner.
- Chest sale price (question 7) and the `GlobalGoldDropMultiplier` decision (question 6). Both must be *decided* before Phase 0 Step 1, but *changing* them is its own task.
