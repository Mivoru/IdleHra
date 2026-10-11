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
