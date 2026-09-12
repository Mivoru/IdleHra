# Breeding rework — a loop that was sealed at both ends

Date: 2026-09-12
Status: approved, ready for planning

## Why

The developer tried to breed and could not work out how. Measured against the
live database, he was not confused — the feature is impossible:

```sql
SELECT count(*), count(*) FILTER (WHERE "Level" >= 50), max("Level") FROM characters;
-- total 80, at_least_50 0, max_char_level 1
```

`BreedingEngine` gates both pairings on `pChar.Level < 50`, a column on the
`characters` row. The only writer of that column in the entire server is
`DevFixtureSeeder.cs:459`. A player's 88 is `PlayerRecords.CurrentLevel` — the
account level. Characters have no level of their own and never did.

So breeding has been unreachable for every account since launch, and the only
place it works is the dev fixture, which writes the column by hand. This is the
repo's own `Grep for a WRITER as well as a reader` trap.

Three further defects were found while measuring, each independently
significant:

1. **Aging is a three-hour treadmill whose only exit was breeding.**
   `ProcessAgeSlot` puts a fielded character at Senior (−10% damage, HP, attack
   speed) after two hours and Old (−20%) after three, permanently. The intended
   remedy is to breed a successor. That has never been possible, so every
   account has been decaying with the recovery path sealed. The reporting
   player's main character carries 2,137,634 age ticks — about 59 hours fielded,
   all of it at −20%.

2. **The Breeding Grounds level does nothing.** `BreedingLevel` is read in four
   places and every one tests `<= 0` or `== 0`. Upgrading past level 1 changes
   no number in the game.

3. **`VillagerCeiling = 20` is unreachable.** Villagers roll
   `2 + rand(0..InnLevel)`; the Inn cannot exceed 12 (Town Hall maxes at 5,
   ceiling is `2 + 5*2`), so villagers cap at 14. `BreedingAptitudes`'s own
   comment — *"0 to 20 is village-driven"* — describes numbers the game cannot
   produce.

And `BreedingEngine` has **twenty `RollbackAsync` calls and zero
`EnqueueCommandResult` calls**: every refusal is silent.

## What is NOT changing

The genetics are sound and stay untouched: weighted inheritance, the genetic
loci, epic mutation, the inbreeding inversion, relatedness detection. The
ninety-day season rollover and the Hall of Ancestors are untouched. The
breeding cost (`500 * (generation + 1)`) is untouched — it was never the
barrier.

## The design

### 1. The gate

Delete the level test from `ExecuteBreedingAsync` and
`ExecuteHeroVillagerBreedingAsync`. Delete `characters.Level` — no writer, no
meaning — along with every read of it in the roster endpoint, the two preview
endpoints and the client labels.

What gates breeding afterwards is what the player can see:

- Breeding Grounds ≥ 1 (already enforced, unchanged)
- the hero is an adult (`AgePhase >= 1`)
- one man and one woman
- same race
- neither parent resting on cooldown, neither locked in escrow
- for a village pairing, the newcomer has not already married

Five visible rules, no hidden number.

### 2. Aging

The thresholds are currently written twice as bare literals — in
`SimulationEngine.ProcessAgeSlot` and again in `OfflineSimulationEngine` — which
is the two-copies-of-one-truth shape this repo keeps shipping defects from.
Extract to one `AgePhaseCurve`, the way `MonsterDefenceCurve` is one place, and
retune:

| phase | now | proposed | penalty now | proposed |
|---|---|---|---|---|
| Child | 0–1 h | 0–1 h | — | — |
| Adult | 1–2 h | **1–40 h** | — | — |
| Senior | 2–3 h | **40–80 h** | −10% | **−5%** |
| Elder | 3 h+ | **80 h+** | −20% | **−10%** |

`ProcessAgeSlot` recomputes the phase from `AgeTicks` on every tick, so this
needs **no migration and no backfill**: every live character re-derives against
the new curve on the first tick after deploy. The reporting player's main drops
from Elder −20% to Senior −5%, and every account on the box gains power.

Aging stops being the whip. The reason to breed becomes the bloodline, which
is what survives the season reset.

### 3. Two levers, one per phase of the climb

The climb was measured and it stalls. Villagers roll `2 + rand(0..InnLevel)`;
at the reporting player's Inn 5 that is 2–7, matching his village exactly (best
value across ten newcomers: 6). Once a bloodline passes the Inn's range, every
villager is worse than the line and contributes nothing but unrelatedness, and
the only remaining climb is drift: +1 at 25%, −1 at 10%, epic +1 at 5% — about
**+0.20 a generation**, against a cap of 50.

**Phase 1 — the Inn raises the floor.** Change the roll to
`2 + rand(0..InnLevel * 3 / 2)`, so a maxed Inn (12) reaches 2–20 and
`VillagerCeiling = 20` becomes true rather than aspirational.

**Phase 2 — the Breeding Grounds buys selection.** Give the building the job it
has never had. Its level decides how many of the four aptitudes the player may
**choose to breed for**; a chosen aptitude takes the better parent's value
*guaranteed* instead of the weighted coin.

| Grounds level | selected aptitudes |
|---|---|
| 1–3 | 0 — pure chance, as today |
| 4–6 | 1 |
| 7–9 | 2 |
| 10–12 | 3 |

The Grounds level also raises the up-mutation chance to `25 + level`, so the
long climb past the village's reach goes from about +0.20 to about +0.32 a
generation.

Reaching 50 stays a multi-season asymptote — `BreedingAptitudes` intends that
and it is preserved. What changes is that a single breeding becomes visible
progress toward something the player chose, rather than a coin flip that can
lose ground.

Selection travels as a new `BreedingSelectionMask` field on
`ClientCommandPacket` — a dedicated field, not a reused one. Riding an existing
field would give one field two meanings, which is already a recorded trap in
this repo (`LogicEpochCounter`). The field is added through the `add-command`
skill and the protocol is regenerated; it is not hand-written.

The server clamps the mask to the count the Grounds level permits, and ignores
bits above it. A client that asks for more selection than it has bought gets
the permitted number, not a refusal — the count is a server truth and the
client's copy is a hint.

### 4. Names

`CharacterRecord` has no name. The breeding dropdown therefore identifies
heroes by the first eight characters of a GUID, which is why the reporting
player could not find his own main in a list of ten.

Add a `Name` column, rolled at birth from a Slavic/Czech folk-name table split
by sex, so nobody is forced to type one; renameable later. Names then appear
wherever a character is listed — breeding, the Hall of Ancestors, the slots.

Existing characters are backfilled by the migration from the same table, seeded
by the character's own id so the assignment is deterministic and repeatable.

### 5. One flow instead of two tabs

The screen's two tabs ("Marry the village" / "Cross your own") ask the player to
understand the difference before they can start. Collapse to one question — a
hero, and a partner — with the partner list grouped into "From the village" and
"Your own line". The two engine methods stay; the screen chooses which to call
from what was picked. A player never has to learn there were two systems.

### 6. Make the refusals speak

Every one of the twenty rollbacks gets a `CommandResultCode`, the way
auto-reroll's endings just did. Silent rollback is this server's documented
favourite way to lie, and breeding is its largest single concentration.

### 7. Explain it, on the screen and in the Wiki

The mechanic is only worth having if a player can understand it, and the
previous version was not understandable. Two places:

- **On the breeding screen**: what the pairing will produce, which aptitudes are
  selected and what selection means, what the Inn and the Grounds each control,
  and what happens to the child afterwards. Short, next to the control it
  describes — not a help page.
- **In the Wiki**: a full Breeding page — the loop end to end, the aptitude
  table and what a point is worth, the two-phase climb with its real numbers,
  what the Inn and Grounds levels buy, aging, inbreeding, and what survives a
  season rollover.

## How a new player breeds, after this

1. Build the **Inn**. Newcomers settle every few hours; the Inn's level sets how
   good their aptitudes roll.
2. Build the **Breeding Grounds**. That is the whole unlock — no level.
3. Open **Breeding**, pick a hero, pick a partner of the same race and opposite
   sex. 500 gold at generation 0.
4. Choose which aptitude to breed for, if the Grounds is level 4 or above.
5. The **child** is born into the **Hall of Ancestors**. Field it into a slot; it
   matures from Child to Adult in an hour.
6. The villager who married in is spent — everyone marries once.
7. At the **ninety-day season turn**, levels, gear, gold and the village are
   taken back. The Hall and the aptitudes bred into it are what survive.

## Testing

- `BreedingGateTests` — the gate accepts an adult of any level and refuses each
  of the five visible rules, with the right result code for each.
- `AgePhaseCurveTests` — one curve, both callers agree, every boundary, and the
  penalty band. A regression guard that the live and offline paths cannot drift.
- `BreedingSelectionTests` — a selected aptitude never takes the worse parent's
  value; the mask is clamped to the Grounds level; the permitted count per level.
- `BreedingClimbTests` — the measurement, printed and asserted: generations to
  reach 20 at each Inn level, and the per-generation drift at each Grounds
  level. Following `PowerCeilingTests`, a printed number is asserted or it is
  decoration.
- `CharacterNameTests` — every character has a name, the backfill is
  deterministic, and the roll is sex-appropriate.
- Client: `serverMirrors.test.ts` gains the Grounds→selection-count table, which
  is a server truth the client renders.
- `exercise.mjs` gains a breeding step that asserts the roster grew.
