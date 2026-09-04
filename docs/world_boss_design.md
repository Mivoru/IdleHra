# World boss: making the fight a fight

Written 2026-09-05, before any code, because the task's own risk line says the
danger here is scope and genre fit rather than difficulty. Task 10 in
`docs/TASK_BOARD.md`.

## What it is today, checked in the source

A button.

`WorldBoss.svelte` renders the boss, a shared health bar and three attempt pips.
`WorldBossEngine.MaxAttemptsPerEncounter` is **3**. Each attempt posts one
number - `predictedDamage`, which the client computes as
`max(typicalHit, 1000)` from its own running estimate of what the player's
ordinary combat hits for - and `ComputeAppliedDamage` clamps it to
`[1000, 100,000,000]` and then to the boss's remaining health.

So the whole interaction is: press Attack, three times. **The damage is a
property of your gear that the client reports about itself.** The only decision
in it is having stocked the larder beforehand, because an empty larder makes
`ExecuteAttackAsync` discard the attempt.

Everything around that is real and working, and this design does not touch it:
a server-authoritative shared health pool behind `SELECT ... FOR UPDATE`,
per-player attribution in `_playerDamageMap` mirrored into a Redis hash, an
event window with dormant/active/concluded states, boss health scaled to the
number of players online, and a ranked reward path in
`ProcessDefeatedBossAsync`. **The content is fine. The interaction is the gap.**

---

## 1. What decision does the player make?

Not "what do they press". The proposal:

> **The boss is armoured in five plates. You choose which one to strike, the
> server tells you what your strike learned, and everyone attacking the boss
> sees what everyone else has already broken.**

Each encounter the server seeds, privately, which single plate is the **weak
point** for that boss. Striking it does **three times** normal damage. Striking
an armoured plate does **full normal damage** and **breaks** that plate,
permanently, for every player in the world, visibly.

Note what that is not: there is no penalty for guessing wrong. A wrong strike is
a full contribution to a health pool whose rewards are ranked by exactly that.
What you miss is an upside, not a payment - see "A wrong strike is not punished"
below for why the first draft had this the other way round and why it was worse.

So the state of the boss when you arrive is a message from the players who came
before you. Five plates, one of them soft:

- **First players of the event** pick blind, one in five, and whatever they
  learn they contribute to everyone. They also strip the armour that narrows it
  for the next arrival.
- **Everyone after** reads the plates. Four broken and the boss still standing
  means the fifth is the weak point, and the decision is trivial - which is
  correct: by then the crowd has *earned* the answer.
- **In between** is the interesting case, and it is a real judgement: three
  attempts against five plates means you cannot brute-force it alone, so
  whatever the board already tells you is worth 1.67x.

This is a decision, it is legible on a screen a player checks once or twice a
day, and it needs no reflexes. It also gives the shared health pool - which
already exists and is currently invisible as anything but a number - something
to *say*.

**Why not the obvious alternatives.** A timing minigame or a click-accuracy
challenge fights the genre and the existing balance philosophy: this project's
anti-cheat has already banned players for clicking too regularly, and the four
active skills were removed after being measured at "+90% damage for clicking
every three seconds" (`SkillTreeRegistry`). A damage-per-second race would
reward the player with the best gear twice, which the ranked reward table
already does once.

## 2. Where is the server bound?

**The client stops sending a damage number at all.**

This is the largest single improvement available here and it is worth doing even
if none of the rest ships. Today `clientPredictedDamage` is a number the client
computes about itself, and the only thing standing between it and the boss's
health pool is a 100,000,000 ceiling. Under this design the client sends **a
choice** - one byte, which plate - and the server computes the damage from the
player's own equipped stats, which it already holds.

| | today | proposed |
|---|---|---|
| what the client sends | a damage figure | a plate index, 0-4 |
| what bounds it | a 100M clamp | nothing to bound - it is not a quantity |
| where damage comes from | the client's estimate | the server's own equipped totals |

An out-of-range plate index is rejected the way any bad command is. There is no
score, so there is nothing to inflate.

**The one thing that must not leak:** which plate is the weak point. It is held
server-side and revealed only by the consequence of a strike - never sent to a
client that has not earned it, or the deduction is over before it starts.

## 3. Does it fit three attempts?

Yes - and the answer changed once it was measured, which is the whole reason
this phase produced a document instead of a commit.

Three attempts against **five** plates is the point. The first draft used three
plates so that the budget matched the puzzle exactly, and that turned out to be
the flaw: with three of each you cannot fail to find the weak point, so knowing
where it is beats not knowing by only 1.2x - a mechanic nobody would look at.
At five plates a solo player cannot brute-force it, the board becomes worth
reading, and knowing is worth 1.67x. The table is under "Five plates, not
three".

`MaxAttemptsPerEncounter` stays at 3. `BattleSessionCapSeconds` stays. Nothing
about the attempt accounting in `ExecuteAttackAsync` changes except what it does
with the request.

## 4. What survives?

Everything that is already correct:

- the shared health pool and its `FOR UPDATE` transaction
- `_playerDamageMap` and the Redis contribution hash
- the event window and `ScaleActiveBossAsync`
- `ProcessDefeatedBossAsync` and the ranked rewards
- the larder check, the attempt cap, the battle session cap

**New state is small:** five plate states per boss instance, which belong on
`WorldBossSnapshots` beside the health, and a seeded weak-point index that is
never sent to a client.

---

## Checked against "this is an idle game"

The gate the task sets. Point by point:

- **No reflexes.** Nothing is timed. A player who opens the screen once a day
  makes exactly the same decisions as one who watches it.
- **No new grind.** The attempt budget is unchanged; this changes what an
  attempt *means*, not how many there are.
- **It rewards attention, not presence.** Reading the plates is worth something
  and costs nothing but looking - which is the loop an idle game wants.
- **It is better with other people and fine without them.** A solo player has
  three strikes against five plates: they may not find the weak point, and their
  three strikes still land in full. A populated server turns the same board into
  shared knowledge, which is what a *world* boss should be. This is the one
  place the design deliberately favours the crowd, and it favours them with an
  upside rather than by taxing the solo player.

## What would make this a bad idea

Written down deliberately, because a design with no failure mode has not been
thought about:

- **If the weak point is guessable from outside the game** - a pattern, a
  rotation, a datamined seed - the decision collapses into a wiki lookup. It has
  to be seeded per encounter from something a client cannot see.
- **If the damage difference between right and wrong is too large**, a player
  who guesses badly feels punished for arriving early rather than rewarded for
  pioneering. 3x on the weak point, and full normal damage everywhere else, puts
  the gap between knowing and guessing at 1.67x - meaningful, and survivable.
- **If the plates are not visible before you commit**, this is not a decision,
  it is a slot machine. The screen has to show the boss's current state plainly,
  and that is a client change of the same size as the server one.

## What is deliberately NOT in this design

- **Minigames.** They were mentioned in the request. Everything above is
  reachable with a choice and a number; a minigame adds an input surface, an
  exploit surface, and an accessibility problem, for the same decision.
- **Changing the reward table.** Ranked by contribution is fine and orthogonal.
- **Boss phases or timers.** They would make the fight a schedule, which is the
  one thing an idle game must not ask for.

## The three decisions, taken 2026-09-05

These were open questions in the first draft. They are decided now, and one of
them was decided against the instinct that wrote it.

### Five plates, not three - and the arithmetic overturned the first answer

Three plates felt right because it matches the three-attempt budget exactly. It
is wrong, and the reason is that **a blind player converges too fast**: with
three plates and three attempts you cannot fail to find the weak point, so
knowing where it is barely beats not knowing.

Worth of knowing where the weak point is, if a weak-point strike does M times
normal damage and a wrong strike does normal damage:

| plates | M=2 | M=3 | M=5 |
|---|---|---|---|
| 3 | 1.20x | 1.29x | 1.36x |
| 4 | 1.33x | 1.50x | 1.67x |
| **5** | 1.43x | **1.67x** | 1.92x |
| 6 | 1.50x | 1.80x | 2.14x |

**At three plates the information is worth 1.2x, which would make the whole
mechanic decorative.** Nobody reads a board that pays 20%. The premise of the
design is that the boss's state is a message from the players before you, and a
message has to be worth reading.

**Five plates at M = 3, so knowing is worth 1.67x.** Enough that reading the
boss is the obvious first thing a player does, not enough that arriving early is
a disaster.

### A wrong strike is not punished, it just misses the bonus

The first draft had a wrong strike do *reduced* damage. That is a worse design
and the fix is free: **a strike on an armoured plate does full normal damage and
breaks the plate.** Only the weak point pays the multiplier.

The difference is entirely in how it feels. Under the reduced version, a solo
player who guesses badly is punished for something they had no way to know.
Under this one they lose an upside, which is the honest description of what
happened - and their three strikes are still three real contributions to a
health pool that is ranked by exactly that.

It also removes the free-rider problem the first draft carried. Breaking a plate
helps everyone who comes later and costs the breaker nothing, so there is no
reason to sit and wait for someone else to do it.

### No discovery bonus. The multiplier is the reward, for everybody

The first draft asked whether the bonus should go to the player who FINDS the
weak point or to everyone who strikes it afterwards. The right answer is that
the question is wrong.

- Paying only the finder decides a reward by **timezone**, not by skill. In a
  global idle game with one shared boss, "first" mostly means "awake".
- Paying only the later strikers punishes the person whose strikes created the
  information everyone else is reading.

So neither: the weak-point multiplier applies to every strike that lands on it,
including the one that found it. The pioneer is already compensated - they get
the multiplier from the moment they hit it, on the same terms as everyone else,
and the arithmetic above shows the gap between guessing and knowing is 1.67x
rather than anything punitive.

### Breaks last the whole encounter

`ScaleActiveBossAsync` rescales the boss's health when the number of players
online changes. **Plate state must not reset with it.** Knowledge destroyed by
something the player cannot see or influence is the worst failure mode a
deduction mechanic has - it reads as the game lying rather than as a rule.

Plates reset when the boss does: a new encounter, a new weak point, a new seed.
Health scaling and plate state are orthogonal and stay that way.

---

## What this borrows, and from where

- **Breakable parts that persist through a fight** - Monster Hunter. It works
  there because a hunt is long enough to act on what you broke; here the
  equivalent length comes from the fight being shared across many players rather
  than from one player's session.
- **A secret that is re-seeded per encounter** - the lesson from every game whose
  puzzle got solved once and then lived on a wiki forever. If the weak point were
  a property of the boss rather than of the encounter, this design would have a
  shelf life of about a day.
- **Rewards ranked by contribution rather than by the last hit** - standard for
  MMO world bosses, and already how `ProcessDefeatedBossAsync` works. It is what
  makes "your wrong strike still counted" true rather than consoling.

What is deliberately NOT borrowed is the coordination layer that raid bosses in
Destiny or WoW are built on - mechanics that need several people acting at the
same moment. This is an idle game; two players are rarely on the screen
together, and a design that assumes they are would be dead most of the day.

## Numbers to build against

| | value | why |
|---|---|---|
| plates | 5 | information worth 1.67x; see the table above |
| weak-point multiplier | 3x | with 5 plates, the point where knowing matters without early arrival being a disaster |
| armoured-plate strike | 1.0x, and the plate breaks | no punishment for not knowing |
| attempts | 3, unchanged | `MaxAttemptsPerEncounter` |
| weak point re-seeded | every encounter | or a wiki solves it permanently |
| plate state resets | only with the boss | never on a health rescale |

