# Onboarding: the complete step list

Written **before** any of the second tier was built, because Task 5 on the task
board is the one most likely to sprawl and a step list agreed up front is the
only thing that bounds it.

This document is the specification. `client_web/src/lib/stores/tutorialSteps.ts`
and `client_web/src/lib/stores/tutorialDiscoveries.ts` are the implementation,
and `client_web/tests/tutorial.test.ts` asserts the two agree.

---

## 1. The rule that does not change

**A step is a predicate over the state packet.**

That is the whole design, and it is why the original three steps worked. The
alternative — a machine advanced by events — has to be watching at the moment
each thing happens, and a player who levelled up in a closed tab loses the step
forever. Reading state is self-healing: whatever the player did and wherever
they did it, the next packet says which steps are outstanding.

It is also why onboarding is testable in a node runner with no browser. Every
predicate below is a pure function of one `StateUpdate`, and every field named
is a real field on `client_web/src/lib/net/protocol.generated.ts`.

Two consequences worth stating plainly:

- **No new wire fields.** Nothing here required one. Where the packet could not
  express a trigger it is called out in §5 rather than papered over.
- **No bespoke tracking.** The only thing persisted is *which explanations have
  been shown*, never *how far through onboarding the player is*. Progress is
  always re-derived.

---

## 2. Tier one — the first session

Three steps, in order, each blocking the next, shown until all three are done.
These are **instructions**: they ask the player to do something they have not
done.

| # | System | Predicate (done when) | Surface | Text |
|---|---|---|---|---|
| 1 | Auto-Eat | `Food1_Count + Food2_Count + Food3_Count > 0` | coach panel → Auto-Eat | Start by fishing, then load the catch into Auto-Eat. It heals you mid-fight, and without it the very first monster will kill you before you can kill it. |
| 2 | Combat | `CurrentLevel >= 2` | coach panel → Combat | Now open Combat and press Fight on Field Mouse. Your character keeps fighting on its own, even after you close the page. |
| 3 | Equipment | `Number(EquippedWeaponId) > 0` | coach panel → Character | Monsters drop equipment. Open Character and click a slot to wear it — gear is where nearly all of your power comes from, not levels. |

Level 2 is used for the combat step because it is the first thing that cannot
happen without a kill. All three larder slots are summed because a player whose
food landed in slot two was once told to fill a larder that was full.

### The order was wrong, and it closed the game's entrance

**Reordered 2026-09-02.** The first version put combat first, on the reasoning
that nothing else can happen before a kill. That reasoning was backwards: a
kill is exactly what cannot happen first.

Measured on a brand-new account against a live server, following step one as
written:

| start | outcome |
|---|---|
| naked, empty larder | dead at **29 s**, Field Mouse still on **264 of its 465 HP** |
| after 60 s of fishing (16 perch) | dead at **65 s**, Field Mouse down to **73** — closer, still a loss |
| a properly stocked larder | the fight is won, at about the 75 s the pacing model predicts |

The character has 100 HP and the mouse deals 8 every 2 s; it out-damages the
player by better than two to one. Because the three steps **block each other in
order**, step one could never be completed, so the food advice — sitting in step
three, behind two steps that a kill has to finish — was never shown to anybody
who needed it. The tutorial's own first instruction killed the player, and then
said nothing else, forever.

The balance is not at fault and was not touched. `ProgressionRateTests`
`.TheFirstMonsterTakesAboutSeventyFiveSeconds` pins the opening kill at 40–110
seconds and passes, because it hands its simulated character a million bites of
food; its own comment says that without them "the character dies in about
thirty seconds", which is within four seconds of what a real new account does.
Region 1's attack was deliberately left un-tripled for the same reason (see
`NEXT_STEPS_BACKLOG.md`, "The whole ladder was raised threefold"). The model was
always right *given food*. Onboarding was what failed to deliver the food.

Equipment moves to last because that is also its true position: a weapon comes
off a corpse, so it cannot precede the kill that drops it.

The regression guards are `tutorial.test.ts` ("starts with the larder, because
the first fight cannot be won without it") and the onboarding section of
`exercise.mjs`, which drives a real new account through fishing and stocking and
asserts the step advances.

Tier one **takes precedence** over tier two: while any of the three is
outstanding, no discovery moment is shown. A new player is not asked to think
about the Hall of Ancestors before they have hit anything.

---

## 3. Tier two — discovery moments

These are **explanations**, not instructions. Each fires once, the first time
its predicate is true, and says what a system is for. The player is never
blocked and never has to acknowledge one to keep playing.

Ordered by roughly when a player meets them. When several are pending at once
the earliest in this list wins, and the rest wait their turn.

| id | System | Predicate (fires when) | Screen | Text |
|---|---|---|---|---|
| `gathering` | Gathering | `WoodcuttingMasteryXp + MiningMasteryXp + FishingMasteryXp + HerbalismMasteryXp > 0` | gathering | You have started gathering. Each profession levels its own mastery, and a higher mastery is a faster tick — woodcutting, mining, fishing and herbalism are tracked separately. |
| `backpack_full` | Inventory | `InventoryCapacity > 0 && InventorySpaceRemaining <= 0` | chest | Your backpack is full, and a full backpack stops drops arriving at all. Sell or scrap from the Chest — nothing warns you again once this happens. |
| `crafting` | Crafting | `TotalItemsCraftedCount >= 1` | crafting | Crafting is a job a character does over time, not an instant purchase. Craft ×10 queues ten of the same recipe in one go, which is how you make anything in quantity. |
| `tools` | Tools | `AxeToolTier + PickaxeToolTier + RodToolTier > 0` | character | An axe, a pickaxe and a rod are *equipment* — slots 8, 9 and 10 on the paper doll, past the eight combat slots. A worn tool is what makes gathering fast; an unworn one does nothing. |
| `skills` | Skill tree | `AvailableSkillPoints >= 1` | skills | You have a skill point to spend. The Skill Tree is permanent for the season — a first respec is free, and after that it costs. |
| `village` | Village | `VillagePopulation >= 1 \|\| CurrentPopulationCount >= 1` | village | Villagers have arrived. They work the production buildings, which earn wood and ore while you are away, and the Inn is what houses them. |
| `region2` | Regions | `HighestUnlockedRegion >= 2` | combat | A new region is open. Regions are unlocked by killing the **boss** of the previous one, not by levelling — if the next region looks locked, the boss is the reason. |
| `market` | Market | `Gold >= 5000` | market | You have gold worth spending. The Market is other players' gear, priced by them; the seller pays a wealth-scaled burn, so the price you see is not the price they keep. |
| `forge` | Forge | `ForgeLevel >= 1` | forge | The Forge fuses two items into a better one, and rerolls the affixes on a single item. Its **building level is the rarity ceiling** — a level 5 Forge cannot fuse past rarity 5. |
| `town_hall` | Town Hall | `max(ForgeLevel, InnLevel, BreedingLevel, LumberjackLevel, MineLevel, WarehouseLevel) >= 2 + TownHallLevel * 2` | village | A building has hit the Town Hall ceiling. Every non-structural building is capped at `2 + Town Hall level × 2`, so the Town Hall is the only way the rest of the village grows. |
| `guild` | Guilds | *(not on the packet — see §5)* `statistics.GuildName !== ''` | guildops | You are in a guild. Donating materials to the depot raises guild buff tiers, and an active buff applies to every member — including you, offline. |
| `breeding` | Breeding | `BreedingLevel >= 1` | breeding | The Breeding Grounds are built. Pairing two characters — or a character and a villager — produces a child that inherits aptitudes, and rarer races come out of exactly this. |
| `first_child` | Breeding | `ActiveChildMaturationMs > 0` | breeding | A child is maturing. It is playable once it grows up; the Inn's level shortens the wait, and until then it occupies one of your character slots. |
| `world_boss` | World Boss | `WorldBossEventState === 1` | worldboss | A world boss is up. Everyone hits the same health bar and the rewards scale with your share — attempts are limited, and an attack with an empty larder is discarded silently. |
| `deeds` | Deeds & Seals | `AchievementTierTotal >= 1` | progression | The Book of Deeds tracks what you have done across every season. Finishing a chapter earns a **Seal**, and each Seal is +2 skill points every season from now on — permanently. |
| `ancestors` | Hall of Ancestors | `max(Slot1_AgePhase, Slot2_AgePhase, Slot3_AgePhase) >= 2` | ancestors | One of your characters is ageing. Levels, gear and gold all reset at the end of a season; the Hall of Ancestors is the short list of people who carry through, and anyone not marked is culled. |
| `inheritance` | Inheritance | `PremiumCurrencyBalance >= 40` | inheritance | You have enough diamonds for a first Inheritance level. Inheritance bonuses are bought with diamonds and are the one thing that survives every season reset untouched. |

Seventeen moments, and every one of them is a system the task board listed as
having nothing that teaches it.

### Why these predicates and not others

- **`region2` uses `HighestUnlockedRegion`, not `DefeatedRegionBossMask`.** The
  mask tells you a boss died; the unlock is the thing the player noticed.
- **`town_hall` fires on the ceiling being *reached*, not on the Town Hall
  existing.** A fresh account has `TownHallLevel = 0` and a ceiling of 2, so
  "the Town Hall exists" would fire on the first packet and teach nothing. The
  moment worth explaining is the one where an upgrade gets refused.
- **`first_child` uses `ActiveChildMaturationMs`, not `Slot2_CharacterId`.**
  The character id being non-empty is a lasting fact; the maturation timer is
  the actual moment of breeding, and it keeps `first_child` distinct from
  `breeding`.
- **`ancestors` fires on age phase, not on a second character.** `AGE_PHASES`
  is `Child / Adult / Veteran / Elder`, so `>= 2` is "someone is getting old",
  which is exactly when the rollover cull stops being abstract.
- **`inheritance` uses 40 diamonds** because that is
  `inheritanceUpgradeCost(0)` — the cheapest thing the screen can actually sell
  you. Telling a player about a shop they cannot buy from is how the old step 2
  ("craft something" on an account with no materials) went wrong.

---

## 4. Surface, persistence, skipping

### Surface: one docked panel, plus a pulse on the real control

The board offered three options and said coach-marks on the real control are
the most effective and the most work. The choice made here is the middle it
also named, with the useful half of a coach-mark kept:

> **One dismissible panel docked bottom-centre, which pulses the nav button for
> the screen it is talking about.** It points at the real control without any
> positioning maths, so it cannot clip at a narrow container width — which a
> floating anchored bubble absolutely can, and a separate agent is auditing
> panels for exactly that bug class right now.

Rules it follows, identically for tier one and tier two:

- **Never modal.** No backdrop, nothing to click through, the game keeps
  running behind it. An idle game whose whole promise is that it runs without
  you must not fence the player into a tutorial.
- **One at a time.** Tier one first; then the earliest pending discovery.
- **Two buttons and no more**: *Take me there* (navigates, and the nav button
  it pulses is the destination) and *Got it* (marks seen). Tier one keeps
  *Skip*, which is the global off switch.
- **Narrow-safe by construction**: `max-width: min(38rem, calc(100vw - 1.5rem))`,
  wrapping flex, no fixed pixel widths, buttons allowed to fall onto their own
  row. Nothing is positioned relative to another element's box.

### Persistence: `localStorage`, keyed by `PlayerId`

Seen-state lives in `localStorage` under `folkidle.onboardingSeen.<PlayerId>`.

Chosen over server-side storage because the alternative costs a wire field or a
REST endpoint plus a schema migration, and the thing being stored is *"has this
person read a sentence"* — the cheapest possible data, and the failure mode of
losing it is being told something you already know once. Server-side storage
would be the right call for anything the player earned; this is not that.

The honest cost: **it is per-device.** A player who signs in on a phone is
taught again there. That is accepted, and it is written down here rather than
discovered later.

Keyed by `PlayerId` so two accounts on one browser do not inherit each other's
seen-set.

**Across a season reset**: a reset drives most predicates back to false and
then true again — `HighestUnlockedRegion` returns to 1, buildings return to 0.
Because the seen-set is keyed by account and never cleared by the game, none of
it is re-taught. This is the direct answer to "a season reset must not re-teach
everything", and it falls out of storing *seen* rather than *progress*.

### The baseline, and why a veteran is not buried

A player who has been playing for weeks and then clears their browser would,
on the naive rule, have fifteen moments all true at once and get every one of
them in sequence. So: **on the very first packet for an account with no stored
seen-set, every predicate that is already true is recorded as seen.** From then
on the ordinary rule applies.

The consequence is exactly the right one in both directions:

- A brand-new account baselines with almost nothing true, so it is taught
  everything as it arrives — including things that became true while the tab
  was closed, because the check is re-evaluated on every packet rather than at
  the moment of the transition.
- An established account baselines everything it has already passed, and is
  still taught anything it has not reached yet.

### Skippable and re-openable

- **Skip everything**: the existing `folkidle.tutorialDismissed` flag now
  silences tier two as well as tier one. One click, from the panel or from
  Settings.
- **Re-open**: Settings → Tutorial lists every moment with its text, whether it
  has been seen, and a *Show again* per row, plus *Reset all explanations*.
  Nothing is reachable only once.

---

## 5. What the packet cannot express

Reported rather than solved. A wire change is expensive here — the packet is
near its 800-byte layout guard and another agent is mid-flight on it — and none
of these is worth one.

| Wanted | Why the packet cannot say it | What was done |
|---|---|---|
| **Guild membership** | There is no guild id on `StateUpdate`. `GuildCombatVanguardPoints` and friends are populated only during a war, and `GuildLogisticsLevel` only by a depot notification, so all three are zero for a member who has just joined. | Sourced from `fetchStatistics().GuildName`, which `GuildOps.svelte` already uses for the same question and which shares a query cache with it. The predicate stays pure: it takes `(state, facts)` where `facts.hasGuild` is supplied by the component. |
| **Guild buff tiers / donations** | Depot stock is REST (`/api/v1/guild/logistics/snapshot`). | Folded into the `guild` moment's text. |
| **Affix rerolls specifically** | Whether the player *owns* a rerollable item is inventory state, which is REST. `EquippedWeaponAffixLocked` only says they have already used the feature. | Folded into the `forge` moment, which fires on a real unlock. |
| **Conversations / chat** | Nothing on the packet reflects chat at all — no channel, no unread count, no first message. `ActiveLanguageState` exists but is unused by the client and is a localisation setting, not a chat fact. | **Not implemented.** A chat moment would need either a wire field or a client-local event hook, and the second would break the "predicate over the packet" rule that makes all of this testable. |
| **Market: first listing or first purchase** | Market activity is entirely REST. | `market` fires on `Gold >= 5000` instead — "you have money worth spending", which is the moment the screen becomes interesting anyway. |
| **Mailbox** | Unread mail is a REST badge. | Not implemented; the badge is already visible in the nav and is self-explanatory. |
| **Codex / Leaderboards / Store / Boosts** | Expressible in principle, deliberately left out. | Each is a browse-only screen that explains itself; adding a moment for every screen is how this task sprawls. |

---

## 6. Verification

- `client_web/tests/tutorial.test.ts` — every predicate asserted to fire on the
  synthetic packet that should trigger it and **not** to fire on one that
  should not, plus tier-one precedence, the baseline rule and ordering. Node
  runner, no browser.
- `client_web/scripts/exercise.mjs` — registers a **brand-new account**, then
  drives it: asserts the coach panel appears on step 1, that *Take me there*
  navigates, that winning a fight advances it to step 2, that *Got it* on a
  discovery moment removes that moment and does not bring it back on reload,
  and that Settings can re-open it.
