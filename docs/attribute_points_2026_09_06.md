# Attributes became a choice, and the power ledger got a test — 2026-09-06

Two pieces of work, both following from the day's audit.

---

## 1. Attributes are spent, not dealt

### Why

Levelling allocated STR/DEX/CON/LCK for the player, by race, with no say in it.
Three things were wrong with that at once:

- **The player never chose anything.** Four numbers moved on their own and no
  screen in the game explained what any of them did.
- **One of the three levelling paths forgot to do it.**
  `OfflineSimulationEngine.ApplyCombatXp` raised the level and paid the skill
  point and granted no attributes at all — and in an idle game that is where
  most levels come from. The only live account past level 1 reached **level 86**
  holding a brand-new registration's **50 / 50 / 50 / 25**.
- **Nobody noticed for months**, which is the real indictment. A system whose
  complete absence is invisible is not carrying its weight.

### What it is now

A level pays **7 attribute points** and the player places them.

Seven is exactly what a Human used to be dealt (2 STR + 2 DEX + 2 CON + 1 LCK),
so the pacing model — which has always been built on a Human — is unchanged by
this becoming a choice. The five other races were on eight and lose a point a
level; that difference was never surfaced anywhere and is not worth keeping as a
permanent racial advantage nothing explained. Races keep their identity through
the innate passives and mastery bonuses they already have, which are visible.

The genetic multiplier survives intact — epic mutation, bred loci and the inbred
penalty scale the **points** now instead of a faster automatic allocation, so
breeding for a better lineage is still worth what it was.

### The parts

| piece | where |
|---|---|
| the pool | `TickStatePayload.UnspentAttributePoints`, `PlayerRecords.UnspentAttributePoints` |
| earning | `RaceAttributeGrowth.ApplyLevelUpGrowth` grants, no longer allocates |
| spending | `CommandType.SpendAttributePoint = 76` |
| the wire | `StateUpdatePacket.UnspentAttributePoints`, 797 → 801 bytes |
| the screen | Character → Attributes, with what each one buys |
| the check | `exercise.mjs` clicks +1 and asserts the pool fell **and** the attribute rose |

The command is **pure tick-thread arithmetic** — no scope, no transaction, no
queue. Both the balance and the four attributes already live on the payload and
the checkpoint already persists all five, so the cheapest possible handler was
available precisely because the state was tick-owned. An out-of-range attribute
id or an amount larger than the balance is **refused with a result code**, not
silently ignored.

### The backfill, which is the repair half

The migration grants `7 × (level − 1)` minus whatever a player already received
above the starting values. That settles two things at once: the conversion from
dealt to spent, **and** the levels the offline path never paid for in the first
place. Player 8 is owed roughly **595 points**.

It is idempotent by construction — an absolute value derived from level and
current attributes, so re-running it is a no-op rather than a second grant.

### Still open

**The four attributes are thin.** STR feeds attack power, DEX accuracy and crit
chance, CON health/armour/block, LCK loot and forge. Making them a choice is
what makes that thinness worth fixing next: a player who can now *see* the four
will notice that one of them is a strictly better buy in most situations.

---

## 2. `PowerCeilingTests` — the ledger

### Why

Nothing in this codebase had ever asked what a maxed character multiplies up to.
Every bonus was reviewed on its own; the **product** was nobody's job. So it was
only ever discovered when a player said the game felt wrong — twice in one day,
at 71.9x and 142.8x.

### What it does

It prints every multiplicative lever with its documented maximum and the running
product, then asserts the total against **the content ladder it has to climb**
rather than against a number I invented:

```
lever                        multiplier   running
weapon damage affixes            5.00x       5.0x
inheritance damage               1.40x       7.0x
codex damage                     9.94x      69.6x   (a CURVE, quoted at 50,000 codex levels)
crit, expected                   9.00x     626.5x
attack speed                     2.50x    1566.2x
set: fire damage                 1.10x    1729.1x

TOTAL: 1,729x    monster ladder: 750x    headroom: 2.3x
```

The headroom assertion is the load-bearing one. Under the old linear codex curve
the same ledger came to about **87,000x against a 750x ladder — 116x headroom**,
which is what "I deal 73,500 damage to every monster" looks like as a number. It
would have failed loudly, months before a player had to report it.

Three rules are enforced:

1. **Headroom stays in band** (0.5x–10x of the content ladder).
2. **No single lever may exceed the product of all the others.** That is the
   specific shape that failed twice — one term so large that everything beside
   it is noise.
3. **Every unbounded multiplier must be a diminishing curve.** Measured, not
   read: at ten times the input, a lever must pay less than ten times the
   output. Linear-and-uncapped is the one shape that is never allowed.

### It found something on its first run

**Crit chance had no ceiling.** It sums from DEX, Vila mastery, five affix rolls,
the bred crit locus and the skill tree, and nothing clamped the total — so
`NextDouble() <= chance/100` was a guaranteed crit forever past 100. Five
crit-chance rolls on one region-5 Legendary weapon already exceed it alone,
which turned crit from variance into a flat multiplier and made every further
crit-chance roll a dead affix nobody was told about.

Clamped at 100%. It takes nothing from anyone — you cannot crit more often than
always — and it makes the excess visible to the ledger instead of silently
wasted.
