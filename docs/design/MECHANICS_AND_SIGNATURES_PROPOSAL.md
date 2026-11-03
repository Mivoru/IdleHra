# Stateful mechanics (2b.6) and signature affixes (5.2) - proposal

Written 2026-11-03 for the owner's design pass (roadmap sections 5.3 step
2b.6 and 8.2 step 5.2). Nothing here is built. Each option says what it does,
where it sits, what it costs, and a recommendation. The owner picks; the
build follows the picks.

## 0. What changed, and what did not

**Rule 4.1** (SKILL_TREE_REWORK.md) allowed only mechanics that resolve inside
one swing and have a closed-form expectation, because offline catch-up used an
expected-value model. Since 2b.4 (1.0.1581, flag on in production) **offline
combat runs the live tick itself** (`CombatSimulator`), so a mechanic that
keeps state across swings is paid identically online and offline. That is
what unlocks Bleed, Sunder and Momentum.

**What still limits them:**
1. **The advisor.** `HuntingProjection` (the "best monster" pick and the
   bots) stays an expected-value model after 2b.5. A stateful mechanic needs
   an approximation there. The approximation only ranks monsters; it never
   pays anything, so being off by a few percent is acceptable.
2. **Simulator cost on the Ampere box.** Measured 2026-11-03: ordinary rows
   36-49 ms per 12 offline hours (budget 50), but War-tree and burn rows
   122-180 ms. Every per-swing mechanic adds to the hot loop. Each pick below
   must be re-measured with `OfflineSimulatorBenchmark` before it ships.
3. **The tree ceiling.** The tree harness must keep the best build at
   0.95-1.05x of today's best at every band (roadmap 6.3).
4. **2b.5 first.** Retire `ProjectCombatLegs`' combat half before adding
   mechanics, so there is one combat model to add them to.

## 1. Stateful mechanics (2b.6) - pick 2 or 3

| # | Mechanic | Effect (starting numbers) | Column | Why it is fun | Cost / risk |
|---|---|---|---|---|---|
| M1 | **Sunder** | each hit takes 1% of the monster's armour, up to 10 stacks; resets on a new monster | Might | Bosses and walls are armour-heavy; Sunder answers the wall the way Bossbreaker does, but rewards long fights | Low. One counter per fight. Advisor: average stacks over the expected fight length |
| M2 | **Momentum** | +1% damage per consecutive hit, up to +15%; a miss resets it | Precision | Turns accuracy into a visible streak on the combat feed | Low. Advisor: closed form from hit chance (geometric) |
| M3 | **Bleed** | a crit also bleeds for 20% of its damage over 3 s; bleeds stack | Precision | Crit builds get a second number on the feed | **Medium.** Needs ticks between swings (the simulator jumps to event ticks; a bleed adds events). Highest perf cost |
| M4 | **Counterstroke (original)** | after a block, your next hit is a guaranteed crit | Guard | Gives the Guard build a damage rhythm, which Living Fortress lacks | Low. One flag |
| M5 | **Desperation** | below 30% health, +20% damage | Guard | A comeback | **Medium.** Depends on the health path that auto-eat drives; the larder decides how often it fires. Hard to balance |
| M6 | **Overkill** | damage beyond a kill carries 50% into the next monster's first hit | Might | Fast killers see their big hits matter | Low. One carried number |

**Recommendation: M1 Sunder, M2 Momentum, M4 Counterstroke.** Each is one
counter or flag, cheap in the simulator, and simple to approximate for the
advisor. They give Might, Precision and Guard one stateful node each. Bleed
is the most "RPG" of the six, but it is the one that costs the simulator
real time, so it is better after the Ampere numbers for the first three.
Desperation fights the larder, which is the game's real difficulty ceiling
(feedback memory "the larder is the difficulty ceiling").

**Where they go:** as a rank-1 node in the column's tier 3 or 4, replacing
nothing. Each new node needs a rank-buffer id (the buffer has room at 128),
a harness row, a parity row (simulator against the live tick), and a line on
the combat feed.

## 2. Signature affixes (5.2) - pick about 8

A signature is a **mechanic on an item**: one slot, only on Legendary or
better, or through fusion. Each one amplifies a tree mechanic, so the tree
says what to hunt for. All are counters or flags.

| # | Signature | Amplifies | Slot | Effect |
|---|---|---|---|---|
| S1 | **Ruinous** | Ruin | weapon | Ruin fires every 7th hit instead of every 8th |
| S2 | **Unbroken Sequence** | Perfect Sequence | weapon | Perfect Sequence fires every 5th swing instead of every 6th |
| S3 | **Bastion** | Living Fortress | chest | block cap +3 points (85 to 88) |
| S4 | **Golden Reliquary** | Golden Fleece | amulet | Golden Fleece every 80th kill instead of every 100th |
| S5 | **Crushing Mend** | Crushing Blow | gloves | a crushing blow heals 1% of max health |
| S6 | **Echoing Riposte** | Echo + Riposte | ring | Echo can follow a Riposte too |
| S7 | **Executioner's Mark** | Executioner | weapon | the Executioner threshold (`WarEffects.ExecuteBelowPct`) rises by 5 points |
| S8 | **Prospector** | Fortune | amulet | every 50th kill drops a material of the next region |
| S9 | **Iron Will** | Last Stand | helmet | Last Stand recharges every 45 minutes instead of 60 |
| S10 | **Sundering Edge** | Sunder (if M1 is picked) | weapon | Sunder stacks to 15 |

**Recommendation: S1-S8.** S9 is weak (Last Stand rarely matters, measured).
S10 only applies if Sunder is picked. Every signature names its node, so a
player without that node sees "needs Ruin" on the item and the chest
can grey it out, instead of it being a dead roll.

**Rules for all signatures:**
- **Roll:** Legendary and better, 10% chance for a signature slot. A fusion
  of three Legendary or better items that carry the same signature keeps
  it. Not rerollable by the ordinary affix reroll; that would turn the
  Forge into a slot machine for the top item.
- **Persisted:** a signature id on the item (a migration). The wire carries
  the id; the client reads names from the registry like affixes.
- **Offline:** paid by the simulator like everything else, with a parity row
  per signature.

## 3. Order of work

1. 2b.5: retire the duplicate offline combat model (after the quiet week,
   about 2026-11-10).
2. The picked mechanics, one PR each, measured on the Ampere box.
3. 5.1 affix families (in progress, independent of both).
4. 5.3 signatures: the migration, the roll, then the picked signatures in
   batches of 3-4.

## 4. Decisions (owner, 2026-11-03)

- **D1/D2 mechanics: all four - Sunder, Momentum, Counterstroke AND Bleed.**
  Bleed is built last of the four and measured on the Ampere box on its own,
  because it adds simulator events.
- **D3 signatures: S1-S8 and S10** (S10 because Sunder is in). S9 dropped.
- D4 the roll: as recommended unless the Forge redesign (owner, same day:
  "the Forge and the affix system need an upgrade and a redesign") changes it.

### The questions as asked

| # | Decision | Recommendation |
|---|---|---|
| D1 | Which stateful mechanics | M1 Sunder, M2 Momentum, M4 Counterstroke |
| D2 | Bleed now or later | Later, after measuring the first three on the Ampere box |
| D3 | Which signatures | S1-S8 |
| D4 | Signature roll | Legendary+, 10%, kept by a matching fusion, not rerollable |
