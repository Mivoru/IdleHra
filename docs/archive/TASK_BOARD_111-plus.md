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

