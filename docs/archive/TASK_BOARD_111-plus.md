# FolkIdle Task Board - closed tasks from 111 up

Closed sections moved here from `docs/TASK_BOARD.md`, verbatim, with what
was done underneath each one.

### 116. Insights harvest rate looks dead

`player_stat_samples.Harvests` (read off the Logistics achievement row,
`StatSampler`) stayed flat all of 2026-10-10 for the owner's account while
two characters gathered, so Progress -> Insights probably shows 0 harvests
an hour. Find where the Logistics counter stops (a maxed achievement?) and
give the sampler a counter that always moves. Grep for the WRITER.

**Done 2026-10-11.** Not a maxed achievement - the counter never
accumulated at all, so the **Logistics achievement itself could not be
earned**. Production had 133 Logistics rows with a highest progress of 169
against a first tier of 10,000. Three faults:
- `HarvestLoopCount` is per CHARACTER (it swaps with the slot register), and
  the checkpoint read only the payload's own copy, which is slot 1. An
  account whose first character fights reported 0 however much the others
  gathered.
- Nothing loaded it at login, so every session restarted at 0 and the
  checkpoint wrote that session's count over the row.
- The offline projection never counted harvests at all.

The fix adds an account-level `TickStatePayload.LifetimeHarvests`. It never
swaps, it is hydrated from `player_lifetime_achievements` at login, and it is
raised by `RunGatheringTick` and by `CalculateGatheringProjection`. The
checkpoint writes every lifetime counter as `Math.Max` (row, payload).
`ForgeUpgradeCount` had the same missing load: production progress was 0 on
all 133 Forging rows, and tiers came only from the synthesis-tier path. It is
hydrated the same way now. Past harvests cannot be recovered, so every account
starts counting from deploy. `LifetimeCounterPersistenceTests` covers the
round trip.

Still open: `HighestForgeSynthesisTier` has no storage either. After a login
the wire's `AchievementTierTotal` undercounts forging tiers 3-4 already held,
so the first synthesis of the session can toast a tier the account owns.

### 117. exercise.mjs leaves a 50-diamond mail per run

The monster-pet duplicate check (`/api/v1/dev/pet-drop`) mails the fixture
50 diamonds every run and nothing claims it. Harmless, but the fixture's
mailbox grows. Claim it in the same check (round-trip), or have
`--seed-dev` clear the pet-duplicate mail.

**Done 2026-10-11.** The check now opens Mail and claims every
"found you again" row, earlier runs' leftovers included. It asserts that none
is left: a new step, "the duplicate diamonds can be claimed from the
mailbox". It matches on the duplicate's own wording, so an admin gift of the
same size is left alone.

### 113. Re-measure pumpkins and tune the Witch (due 2026-10-11 ~22:40 UTC)

Pumpkins went x5 (kill 0.5%, harvest 0.1%) and the Witch to 1 in 20,000 per
pumpkin on 2026-10-10 (PRs #163, #165). Both were sized on the OLD rate x5,
because the new one had run for 1.5 h. Baseline at 2026-10-10 22:39 UTC:
Mivoru (8) 5,496, Tomda (107) 1,542, (pro)boss (108) 1,522. There is no
earnings ledger: from each balance subtract Cailleach first clears since
then (`seasonal_boss_clears`, 250-1,500 per tier) and add back shop spending.
Then divide by the hours. Acceptance:
- an ordinary active account (Tomda) still reaches 2-3 pets + 2-3 avatars
  (~10,000 pumpkins incl. boss) by 2026-11-02;
- the Witch's chance over the rest of the event, per account, is reported
  to the owner (target was ~73% for the owner, ~38% for Tomda) and the
  constant (`PetRegistry.RareDropPerCurrency`) moved if they ask.
Details: `NEXT_STEPS_BACKLOG.md`, 2026-10-10 evening entry.

**Done 2026-10-11 15:36 UTC (17 h after the baseline, not 24).** The rates are lower bounds, because offline earnings land only at the next login:
- Drops since the baseline: Mivoru +607 (about 36/h; 50/h in the morning), Tomda +284 (16.7/h), (pro)boss +189 (11/h). The 1,000 from (pro)boss's winter IV clear is left out.
- No shop purchases in the window.
- Tomda projects to about 12,250 by 2026-11-02 (drops plus winters IV-V), so the target holds.
- Witch odds over the rest of the event at 1/20,000: Mivoru 58-71%, (pro)boss about 24%. Tomda already owns her.
- `RareDropPerCurrency` is left at 1/20,000.


### 114. Rebirth that is worth taking - BUILT 2026-10-11 (PR #168)

**Built:** cap 10, +2 skill points a rebirth (in the reset's own SQL),
+50% XP below `PlayerRecords.PeakLevel` (in `LiveKillXpMultiplierPct`, so
live, offline and the projection agree), keep-gear priced 5M-100M by
quality tier, a second pet slot (`player_pets.Slot`) at 5, and the title
The Ever-Returning at 10. **Not done, by finding:** `EquipmentLevelGate`
stays unenforced. It was retired on purpose for the region gate, and
`EquipmentAttributeGate` already stops a reborn level-1 from wearing
region-2+ gear (20 points a region tier, from 0). The rebirth-count mark
beside the name and the R10 frame are not built yet. Open owner question:
the keep price is taken from gold the rebirth wipes anyway, so it bites
only as gold held at that moment (and in the shards that gold would buy).
Malakor's Ascension (task 112) is not re-measured for +20 skill points.

**Owner decisions, 2026-10-11** (these replace the proposal below):
- **Faster re-run:** a flat +50% XP on every level below the account's
  best-ever level, whatever the rebirth count. To be measured before
  shipping (back to 50 is about 72 h instead of 108 h on the pacing model).
- **2 skill points per rebirth**, into the existing skill tree. There is NO
  separate rebirth tree - there are enough trees already (skills,
  attributes, inheritance). A skill tree polish comes later.
- **Equipment can be kept** in the bank through a rebirth, for a gold price
  per item that scales with rarity: about 5M for the lowest up to 100M for
  the top tier, geometric between them. Paid from the gold the rebirth is
  about to wipe. The level requirement (`EquipmentLevelGate`, defined but
  NEVER enforced - a level-1 can wear region-5 gear today) must be enforced
  on equip, so kept gear comes back as the character re-levels.
- **Pets all stay.** None is erased by a rebirth.
- **Milestones:** a second pet slot (R5 proposed), and a title and frame at
  R10 with the rebirth count shown beside the name. **No extra character
  slot** - it would let one account farm every kind of work at once and
  never worry about food or materials.
- Cap of 10 rebirths. More rebirth rewards are to be designed later.

Gold income for the price, from `gold_income_daily` over the week before
2026-10-11: Mivoru 270M a day, (pro)boss 28M, Tomda 22.5M.

**Original statement and proposal:**

The owner, 2026-10-10: rebirth does not pay - staying on the last region
is better; it must be visible and worth it, but capped so a new player can
still catch up, after which players build and farm the endgame. Today it
pays only Renown, `floor(15 x (1 - 0.8^n))`% damage for rebirths at level
50+ (`RebirthRules`). Proposal on the table, not approved:
- a cap of about 10 rebirths;
- each pays something that makes the next run FASTER (XP %, cheaper levels
  1-50), so a rebirth is not a punishment and a newcomer catches up through
  a fast start;
- one point per rebirth in a small permanent rebirth tree (loot, gather,
  crit... - the start of builds);
- milestone unlocks (e.g. R3 a character slot, R5 a second pet slot, R10 a
  title or frame) and a visible mark beside the name;
- every rebirth bonus together capped around +20-25%.
Measure before proposing numbers: how long the second run takes with the
bonuses (simulation), and what it does to `PowerCeilingTests` and Malakor's
Ascension (task 112). Also check that a bought high-level item on the
market cannot be worn by a reborn level-1 character (level requirement).

**Done and deployed:** PR #168 (1.0.1201) and PR #169 (1.0.1203).
- #169 added the rebirth badge, the milestone avatars at 3/5/7/10 (Jarilo, Vesna, Étaín, Morana) and the Phoenix at 10 (+8% damage, health and XP).
- The R10 "frame" became the gold badge plus a glowing name.
- Still open: Malakor re-measure for +20 skill points (task 112).


### 121. `gather_speed_pct` units and scope - MEASURE FIRST, balance (issue #203)

**DECIDED 2026-10-29, built in PR #211, not deployed:** fix the tooltip (whole
percent), per-tool scope, and - the owner's follow-up - the extras multiply
the core instead of adding to it.

**Current state.**
- Affix magnitudes are tenths of a percent, and every other affix and the
  tooltip read them that way.
- `GatheringToolEngine.TotalSpeedBonusPct` adds `ToolGatherSpeedPct` as WHOLE
  percent. A Legendary roll shows +5.5% and pays +55%.
- Speed is summed across all three tools, so an axe speeds up mining.
- Yield and rare find were made per profession in #202. Speed was left out on
  purpose and belongs here.

**Expected result.** The tooltip and the tick agree, and a tool speeds up only
its own profession, with the size of the change measured before it lands.

**Depends on.** #202 merged (the per-tool fields it adds are the pattern to
reuse).

**Acceptance.**
- A table of each profession's speed at reference builds (mastery, tool tier,
  village, affix), today and under each option ("engine /10" vs "tooltip x10"),
  from `ProgressionRateTests` / `GatheringShareTests`.
- A read-only production count of tools carrying `gather_speed_pct`, and how
  much speed they give today.
- The owner picks an option. No change ships without that decision.

**Tests.** A per-profession scope test, as #202 has for yield. A test that a
rolled magnitude M pays exactly what the tooltip prints for M.

**Closed 2026-10-30.** Built in PR #211, deployed with #197-#212 (through 1.0.1310): whole-percent tooltip, per-tool scope, extras multiply the core. Watch item moved to the roadmap, 3.4.

### 122. Dead set-bonus fields - BUG or DESIGN, verify first

**DONE 2026-10-29, merged as #207** (both removed; no behaviour change).

**Current state** (grepped 2026-10-29):
- `SetBonusEngine.SetBonusResult.FlatAttackPowerBonus` and
  `CooldownReductionActive` are declared and assigned by nothing.
- `StatsCalculator` still adds `FlatAttackPowerBonus` to both flat damage
  figures. That always adds 0.
- `CooldownReductionActive` is referenced only by comments in
  `StatsCalculator` (its consumer was already removed there).
- Neither is in `StateUpdatePacket`, the roster's `ActiveSetResponse` or the
  client.

**Expected result.** Each field ends as exactly one of:
- implemented, if a set's design names that bonus;
- removed;
- kept with a comment saying why it is inert.

**Depends on.** The set design (`SetBonusEngine`, and the task-63 write-up in
the archive), to learn whether any set was meant to carry them.

**Acceptance.**
- Every reader and writer listed (code, packets, client, tests, docs).
- A removal compiles with no packet or protocol change (`generate:protocol
  --check` clean).
- Nothing persisted refers to either field. Verify, do not assume.

**Tests.** Removal: the existing set tests stay green. Implementation: a test
that the set pays the bonus, and that a mixed set does not.

**Closed 2026-10-30.** Merged as #207, deployed with #197-#212.

### 123. Stale code - CLEANUP, no behaviour change

**DONE 2026-10-29, merged as #206.**

**Current state.**
- `StatsCalculator`'s Vodnik block still says `CritMitigationPct` is "computed
  here but not yet consumed anywhere". `SimulationEngine.MonsterCritMultiplier`
  and `BossGearBenchmark` consume it.
- `Domain/Combat/EquipmentLevelGate.cs` has no caller. It is mentioned only by
  comments in `RegionUnlockGate`, `CraftingEngine` and `StateUpdatePacket`.

**Expected result.** The comment tells the truth. A dead class is gone, or it
is documented as kept on purpose.

**Depends on.** Nothing.

**Acceptance.** `EquipmentLevelGate` has no code reference left (grep tests
too). The full server suite is green. The diff touches no logic.

**Tests.** None new. This is a deletion and a comment.

**Closed 2026-10-30.** Merged as #206, deployed with #197-#212.

### 124. Fairy's dead "+20% ranged damage" - DESIGN

**DECIDED 2026-10-29 (option b, +5 dodge), built in PR #212, not deployed.**

**Current state.**
- The Vila (Fairy) innate multiplies `FlatRangedDamage` by 1.2
  (`RaceBonusRegistry.VilaRangedDamageFactor`).
- Combat has one damage figure (`FlatMeleeDamage`). `FlatRangedDamage` is read
  only by `GuildWarSnapshotEngine`'s aggregate.
- `/api/v1/races` deliberately does not describe the bonus, and
  `RaceBonusRegistryTests` pins that it never will while it is dead.

**Expected result.** The Fairy's innate package is something the fight pays
and the page can state.

**Depends on.** The owner's choice. Find the original intent first: the GDD,
git history of the race block, and the archive.

**Options to prepare, each with its measured effect** on Fairy damage per
second and survival against the other five races (use the reference builds in
`RaceUnitMeasurementTests`):
- **(a)** A real ranged damage type. That is a combat-model change, not a race
  change, so price it as such.
- **(b)** Replace it with a different, working effect.
- **(c)** Remove it.

Do **not** silently redirect it into general damage. That is option (b) with
a number nobody chose.

**Acceptance.** The owner picks. The registry, the page and a test all say the
same thing.

**Closed 2026-10-30.** Option (b), +5 dodge, built in PR #212, deployed with #197-#212.

### 125. Design decisions with numbers - DESIGN

**DECIDED 2026-10-29:** Tank removed (PR #212, not deployed); ring keeps
block only for now; Legacy perks parked with Guild Wars (see "Parked by the
owner").

Prepare options, not changes. Each option comes with its measured effect.

- **Tank lineage.** `ProgressionEngine.Lineages` defines Tank (+8% base health
  per level), and no command or screen selects it. Registration writes
  `SelectedLineageId = 1` (Warrior, +5% base damage per level). For
  reference, +8% a level makes base health x9 at level 100, against Warrior's
  base damage x6.
  - Options: expose the choice (where and when: registration, rebirth),
    rework it, or delete it.
  - Measure: boss walls (`WallWindowScanTests`) and `ProgressionRateTests` for
    a Tank.
- **Ring affixes.** The ring rolls only `block_chance_pct`, so a high-rarity
  ring stacks 4-5 block rolls.
  - Options: widen the pool (crit chance, crit damage, lifesteal, flat HP),
    with its effect on the best builds' DPS, block and sustain.
  - Must keep: the `KNOWN_AFFIX_IDS` order (the wire index) and the stored
    items.
- **Legacy perks.** XP, Gold and "Combat speed" (which actually multiplies
  damage) are purchasable on the server (`PurchaseLegacyUnlocks`), but no
  client screen calls `purchaseLegacyUnlock`, so they cannot be bought.
  - Options: a screen, which then needs an honest name for the damage perk;
    or retire them.
  - Measure: where legacy points come from and how many players hold them
    (read-only query).

**Acceptance.** For each item, a one-page option sheet with numbers. The owner
decides, and only the chosen option becomes a separate build task.

**Closed 2026-10-30.** Tank removed (PR #212, deployed). The ring pool moves to the affix rework (roadmap section 6, owner decision D4). Legacy perks are parked with Guild Wars (see "Parked by the owner" on the board).

### 126. Automatic stat audit - TOOLING, prevents a repeat of 2026-10-29

**DONE 2026-10-29, merged as #208.**

**Why.** Nearly every defect found on 2026-10-29 was one shape: a value that
is bought, rolled, stored or shown, and that the simulation never reads, or
reads in the wrong unit. Examples: inheritance Gathering yield, the Fortitude
armour, the tool yield and rare find, Draugr's double divide, the 0.15 attack
speeds, the dodge dead zone, `gather_speed_pct` x10. Each was found by reading
code. Reading does not scale, and it is not repeated after the next change.

**Expected result.** A test that fails when a new stat is added without a
consumer, or with a unit that disagrees with how it is shown.

**Acceptance.**
- **Registry -> consumer:** every inheritance stat, skill-tree node, race
  effect, affix id, pet stat and trait has a test-visible effect. Changing it
  from zero to its maximum changes a figure the tick uses: attack, health,
  armour, hit or crit, yield, speed, gold or XP. A registry entry with no
  effect fails, unless it is listed in an explicit allow-list with a reason.
- **Units:** every percentage affix's tooltip value (magnitude / 10) equals
  the effect the tick applies at that magnitude.
- **Parity:** for a set of reference payloads, the live tick's figures, the
  offline projection, `CharacterStatSheet` and `HuntingProjection` agree. The
  task-78 parity tests are the pattern.
- It runs in the normal suite, under a minute.

**Depends on.** 120, so the CI it runs in is trustworthy. It will surface
whatever 122-124 have not closed yet, through its allow-list.

**Closed 2026-10-30.** Merged as #208. It found task 127.

### 127. The Clumsy Hands flaw does nothing - BUG, BALANCE DECISION

**DECIDED 2026-10-29 (option a, the flaw applies), built in PR #211, not deployed.**

**Current state.** Found by the stat audit (#208, task 126).
- The flaw trait Clumsy Hands promises "-6% gathering speed". Two guards
  in `GatheringToolEngine` drop it: `TotalSpeedBonusPct` adds the extra-speed
  term only `if (toolAffixSpeedPct > 0)`, and `ComputeRequiredTicks` applies
  the total only `if (totalSpeedBonusPct > 0)`. Changing only the first one
  still changes nothing (verified 2026-10-29); both have to change.
- The other flaws (Thin Blood, Faint Heart) do apply.
- #208 lists the trait under `KnownDefects`. That is NOT acceptance: the
  audit fails the moment the flaw starts working, so the fix must delete
  the row.

**Expected result.** The flaw does what its card says, or the card changes.

**Depends on.** The owner's choice. Making a flaw start to bite slows
gathering for every character that carries it, so it is a balance change,
not a cleanup.

**Options.**
- **(a)** Apply negative totals, with a floor so speed never goes below base
  by more than the flaw itself. Measure what share of characters carry the
  flaw (read-only query) and their speed change.
- **(b)** Reword or retire the flaw.

**Acceptance.** The option is chosen, `trait:clumsy_hands` leaves
`KnownDefects`, and a test shows a Clumsy Hands character harvesting slower
(or the card no longer promises it).

**Closed 2026-10-30.** Option (a), built in PR #211, deployed with #197-#212.

### Flaky: `OfflineCombatParityTests` row "r5 bare fed"

Fails only under full-suite load, passes alone 3 of 3. Find what it shares
with other tests (static queues, the shared Random) before widening any
tolerance.

**Closed 2026-10-30.** #209 fixed the row (a character that dies in its first six hours gets 48 lives). The remaining full-suite flakes are open task 129 on the board.
