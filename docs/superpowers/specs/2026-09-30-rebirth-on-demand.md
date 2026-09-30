# Rebirth on demand (task 88)

Owner decision, 2026-09-30: the calendar season is replaced by a rebirth the
player triggers. What carries is what the season rollover already keeps; a
rebirth also pays a small permanent bonus. The calendar end (era 1, due
2026-11-02 09:40 UTC, paused by hand) must not wipe anyone.

## 1. What the season rollover does today (from `SeasonalRotationEngine`)

`ExecuteEraCheckAsync` runs every five minutes. It closes the active era when
the era is past `EndTimestamp` and not paused, or when the admin has pressed
"end now". It then disconnects every client, freezes the tick
(`IsEraTransitionActive`), and runs `ExecutePlayerRolloversAsync` in one
serializable transaction over **every** player:

1. **Shards.** `CalculateLegacyShards(gold, level², inventory score)` goes into
   that player's `PlayerLegacyLedger` row for the closed era.
2. **Placement.** The roster is ranked as the leaderboard ranks it. Diamonds
   go by rank, `BestSeasonRank` is set for the top 50, and the top 3 are
   announced.
3. **PlayerRecords:** level 1, XP 0, active potions cleared, `FreeRespecUsed`
   back to false, and `AvailableSkillPoints` set to 2 per Seal.
4. `TRUNCATE player_skill_tree`.
5. Eight equipment pointers are nulled on `characters`.
6. Gold is set to 0 and every other `CommodityRecords` row is deleted.
7. `TRUNCATE EquipmentInstances RESTART IDENTITY`.
8. Open SELL orders on unescrowed market items are deleted, the remaining
   orders are detached, and `TRUNCATE MarketEquipmentInstances`.
9. Every character is set to a fresh adult (`AgeTicks = ChildEndTicks`).
10. `TRUNCATE village_newcomers`; the arrival clock and the recruitment counter
    go to 0.
11. `HallOfAncestorsEngine.CullToCapAsync`: the Hall is cut to its cap. The
    main character stays, then the members marked Keep, then the best blood.
12. The chronicle pass goes back to 0.

**Three defects in that list**, all of which rebirth would turn into exploits.
They are fixed in the shared code, so the admin's global "end now" gets the
fixes as well:

- **Attributes are never reset.** STR/DEX/CON/LCK and `UnspentAttributePoints`
  are 7 points per level (`RaceAttributeGrowth`). A second climb pays them
  again on top of the first, which makes it the linear, uncapped stacking
  that `PowerCeilingTests` forbids. They now go back to
  `AttributeRegistry.StartingValue` with 0 unspent, the state level 1 has.
- **Step 5 stops at eight slots.** The tool slots 8-10 (Axe/Pickaxe/Rod) keep
  their ids through a `RESTART IDENTITY`, so they point at other players'
  items (the CLAUDE.md "eleven slots" trap). All eleven are nulled now.
- **Activities are not reset.** A level-1 character kept its region-5 fight
  and died over and over. Every character is set idle (`ActiveActivityId = 0`).

## 2. What rebirth keeps and resets

Rebirth is steps 1 and 3-12 for **one player**. It uses the same code path,
refactored to take an optional player filter: `DELETE ... WHERE PlayerId = @p`
replaces `TRUNCATE`, and ids are not recycled. Step 2 (placement) stays
global-only, because a single player cannot place against a roster that is
not ending.

**Carries.** None of this is touched by the reset:

- Hall of Ancestors: every surviving member's lineage (aptitudes, genes,
  generation, epic mark), the Keep marks and the purchased slots. The cull to
  the cap still runs.
- Seals (`SealsEarnedMask`), and the +2 skill points per Seal again after each
  rebirth.
- Inheritance levels (`player_inheritance_stats`).
- Shards (`PlayerLegacyLedger`, plus those earned by this rebirth) and the perks
  bought with them (`LegacyPerks`).
- Diamonds, paid respec grants, cosmetics, titles and personal records.
- Village **buildings**, race masteries and unlocks, gathering masteries, the
  monster codex, the deeds' counters and `BestSeasonRank`.
- Larder contents, mail, guild membership, and breeding cooldowns and
  gestations.
- **New:** `RebirthCount` and `RenownedRebirths`.

**Resets.** Level and XP. The attributes, back to their starting values. The
skill tree, with 2 points per Seal handed back. Active potions and the free
respec. All gear, including worn gear in all 11 slots. Gold, and every
material stack. Unescrowed market listings. Character ages, back to adult.
Activities, set to idle. The village gene pool, with its clock and its
recruitment price. The chronicle pass. The Hall members past the cap.

**Then granted (rebirth only).** The registration starter kit, through the same
`StarterEquipmentGrant`: the claymore and ten fish go in the chest, and the
three Normal tools are worn. Without it, a level-1 account with nothing is the
account that died to the first monster until the starter weapon existed. Every
piece is worthless on the market, so rebirth cannot farm them.

## 3. The permanent bonus: Renown

A rebirth is **renowned** when the player is at level `RebirthRules.RenownLevel`
(50, halfway to the level-100 deed) or above when they trigger it. Each renowned rebirth
raises a permanent damage bonus:

    damage% = floor(15 × (1 − 0.8^n))       n = renowned rebirths
    n: 1→3%, 2→5%, 3→7%, 5→10%, 10→13%, ∞→<15%

- **The cap is asymptotic, below 15%.** It feeds the same `inheritDamagePct`
  term as Inheritance damage. That applies on the live path
  (`EffectiveMilliAttackFor`) and the offline path. It is registered as a
  lever in `PowerCeilingTests`' damage ledger, and in the rule that every
  lever is a stated curve.
- **Why damage.** The damage ledger is the one that measures power against the
  ladder, so the bonus is checked where it matters. 15% is well under
  Inheritance's +40%, which costs about 25,000 diamonds. The bonus is felt
  on the first region of a new climb, and is noise by region 5.
- **Why a level gate on the bonus but not on rebirth.** "Whenever they
  choose" is kept: rebirth is allowed at any level. Without the gate, though,
  rebirthing at level 2 on repeat would max the bonus in minutes. The gate
  makes each step of the bonus cost a real climb.

## 4. The preview

`GET /api/v1/rebirth/preview` is read-only. It returns:

- the current `RebirthCount` and `RenownedRebirths`, the level, the renown
  level, and whether this rebirth would be renowned;
- the damage bonus now, after this rebirth, and the cap;
- the shards this rebirth would pay, from the same formula;
- **what you lose**: level, gold, material stacks, equipment pieces, skill
  tree points spent, attribute points placed, and village newcomers;
- **what the Hall lets go**: the names of the members past the cap, ranked by
  the same `HallOfAncestorsRules.ChooseSurvivors` the cull uses;
- **what you keep**: Seals and their skill points, Inheritance levels, shards,
  diamonds, Hall members kept, and village buildings.

## 5. Confirmation UX

The Rebirth panel sits on the **Ancestors** screen, which already explains
what a rollover carries. It is not a new destination, so `screens.mjs` does
not change.

- **Step 1:** a "Rebirth…" button opens the full keep/lose list and the names
  the Hall would let go.
- **Step 2:** "Yes, rebirth now" and "Cancel", as plain buttons: no `<select>`,
  and nothing that ticks inside a control.
- The POST carries `ExpectedRebirthCount` from the preview. A second submit,
  or a stale page, gets `409 AlreadyReborn` and changes nothing.
- On success the panel says what happened ("Reborn. Renown 3 → +7% damage;
  +412 shards") and the owned-item queries are invalidated.

## 6. The calendar machinery

- `ExecuteEraCheckAsync` no longer ends an era on its date. With no active
  era it creates one, so shard ledgers always have an era. Otherwise it acts
  only on the admin's explicit "end now". That typed phrase stays as the
  owner's global tool, and it now uses the fixed reset.
- `EndTimestamp` and `IsRolloverPaused` stay in the schema, and the admin GET
  still reports them. The admin panel says the date no longer ends a season.
- Leaderboards keep ranking by level. A rebirth drops you down the board, and
  that is the player's own choice. Weekly payouts are unchanged. Placement
  diamonds are only paid by a hand-ended global season.
- The Chapter V deed "Finish a season in the top fifty" could never complete
  once seasons stop ending, which would lock that Seal for good. It becomes
  "Be reborn at level 50 or above", and an old top-50 finish still counts.

## 7. A rebirth while the player is online

The player is online when they press the button, so the live payload has to
change with the database. The flow:

1. The POST (under the account stripe lock) puts a `RebirthRequest` on
   `PlayerSessionRegistry`. The tick drains it, then:
   - suspends the payload and sets `RebirthPending`;
   - calls `RequestFlush(Command, then: …)`, so the rebirth reads rows the
     checkpoint has just written;
   - in `then`, runs the engine, loads the fresh state and puts it on
     `StateReloadQueue`.
2. With no live payload, the POST waits on `WaitForPendingFlushesAsync` and
   runs the engine itself.
3. The engine purges the player's Redis frame and its gold, wood, stone and
   iron buffers. Otherwise write-behind would put level 96 back.
4. The engine raises `LogicEpochCounter` by 3, which keeps it inside the
   command gate's drift tolerance. Any stale snapshot of the old life is then
   refused rather than written over the reset.
5. A `Logout` that arrives while `RebirthPending` is set skips its flush.
   Everything it holds was flushed already, and the reset overwrites the rest.
6. The reload drain does not carry the live activity across a rebirth
   (`StateReloadMerge`).
7. The POST awaits the outcome for up to 15 s and returns
   `{Result, RebirthCount, RenownedRebirths, DamageBonusPct, ShardsEarned}`.

No call to `FlushStateAndAdvance` is made on the tick.

## 8. Migration

One additive migration: `PlayerRecords.RebirthCount` and
`PlayerRecords.RenownedRebirths`, both `int NOT NULL DEFAULT 0`. There is no
backfill, because nobody has been reborn.

## 9. Risks

- **Write-behind race.** A Redis write-behind pass that read the frame before
  the purge and commits after the reset can put the old level back. The
  window is milliseconds, every five minutes. The epoch bump does not cover
  it, because write-behind does not check epochs. It is recorded here and
  not fixed.
- **Shards can be farmed.** The gold term, `12.5·log10(gold)`, pays at any
  level. Everything shards buy is capped (`LegacyPerkResolver.MaxPerkRank`
  50), so this cannot run away. It does let a determined player buy the
  perks faster.
- **Legacy perks are not in the damage ledger.** The combat speed perk (up to
  +50%) predates this task and is still missing.
- **Mail smuggling.** Gold or items mailed to an alt survive the reset,
  exactly as they survive a season.
- **Hall cull deletes characters.** The preview names everyone it would let
  go, before step 2.
- **Loot in flight.** A drop that `CombatLootEngine` has queued but not yet
  written, from a kill just before the rebirth, can still land afterwards. It
  would be one piece, from the old run.

## 10. Status (built 2026-09-30, branch `feat/88-rebirth`)

Built as designed, with two changes.

- **Renown gate.** Its rationale was "the breeding gate", and that gate has
  been removed. The value stays at 50, now read as halfway to the level-100
  deed.
- **Starter kit.** A reborn account gets it; see §2.

**Covered by tests.**

- `RebirthTests` (Postgres, shared collection):
  - the carry list and the reset list, field by field;
  - a neighbour player left untouched;
  - a concurrent double submit, which rebirths once;
  - a rebirth below the renown level, which pays no Renown;
  - the online path through `RebirthTickCoordinator`: flush first, a
    reload with no fight and no gold, and a stale snapshot refused by the
    fence;
  - the Redis purge;
  - the Renown curve.
- `SeasonControlTests`: an overdue era that is not paused no longer ends.
- `PowerCeilingTests`: the Renown lever.

**Checked live.** A server in a container, over HTTP and a JSON WebSocket
session:

- The rebirth is answered in about 430 ms, and a second submit gets 409.
- The open socket receives level 1 and stays connected.
- A checkpoint after the rebirth commits against the fenced epoch, and the
  row stays at level 1.

**Not run.** `npm run exercise`: this machine has no Playwright browser.
