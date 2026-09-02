# Breeding: the player-facing model

One page. Everything here is derived from the server code, not from
`LONG_GAME_SPEC.md` — where the two disagree, the code is what players
experience and the code is what is written down here. The disagreements are
listed at the bottom under [Where the spec and the code disagree](#where-the-spec-and-the-code-disagree).

Source of truth for each claim: `BreedingAptitudes.cs`, `BreedingEngine.cs`,
`GeneticSplicingEngine.cs`, `VillagerArrivalRules.cs`, `VillageArrivalEngine.cs`,
`HallOfAncestorsRules.cs`, `HallOfAncestorsEngine.cs`, `SeasonalRotationEngine.cs`,
`CharacterSlotEngine.cs`, and `SimulationEngine.ProcessAgeSlot`.

---

## 0. The words

These five names overlapped and meant different things on different screens.
This is the canon. Every breeding, Village and Ancestors surface uses these and
nothing else.

| Concept | Canonical word | Do not call it |
|---|---|---|
| One of the four bred numbers: Strength, Skill, Endurance, Fortune | **aptitude** | trait, stat, gene |
| The four aptitudes your line carries, collectively | **bloodline** | legacy, lineage |
| One of the four dominant/recessive pairs: Race, Speed, Crit, Yield | **gene** | locus, loci (server-side names only) |
| One half of a gene | **copy** — the *dominant* copy and the *recessive* copy | allele |
| A person on the Hall of Ancestors roster | **ancestor** | lineage member |
| The end-of-season deletion down to the Hall's cap | **the cull**; who survives it **carries** | reset, wipe |
| Somebody who arrives at the Inn and has not married | **newcomer** | resident, villager slot |
| A newcomer who has married into your line | **elder** | spent villager |
| Newcomers plus elders, as a resource | **the gene pool** | the village (that is the buildings) |
| Putting an ancestor into one of the three played character slots | **fielding** them | selecting, activating |
| The permanent bonuses diamonds buy on their own screen | **Inheritance**, capitalised, that screen only | — |
| What a child gets from its parents | the verb **inherits** | the noun "inheritance" (reserved above) |

The old Village screen also had a panel called "Villagers" that listed
identity-less work slots — a different concept wearing the gene pool's name on
the same screen. It is now **Work slots**.

---

## 1. What you need before you can breed at all

- **A Breeding Grounds** in the village, level 1 or better. Without it the
  server rejects the command; the screen refuses to send it.
- **A hero at level 50 who is an Adult.** Both, not either. `AgePhase >= 1` and
  `Level >= 50`.
- **Gold**: `500 × (highest parent generation + 1)`. A generation-0 founder
  costs 500; a generation-3 parent costs 2,000.
- The hero must not be on a breeding cooldown and must not be locked in a
  market trade.

## 2. The two pairings

### Hero × newcomer — the standard pair

Only the **hero** needs level 50 and adulthood. The newcomer only has to exist,
be of the **opposite sex**, and be of the **same race**.

A newcomer **marries exactly once**. Afterwards they are an elder: they stay on
the roster as a record of the blood that came in, and can never marry again.

This pairing is **never counted as inbred**. A newcomer has no parents in this
world, so there is no shared ancestor to find.

Only the hero's side is recorded as a parent. The other parent column is empty,
because the newcomer is not a character and the gene pool is deleted at the
rollover anyway.

### Hero × hero — crossing your own

**Both** parents need level 50 and adulthood, one of each sex, and the same
race. Both go on cooldown afterwards.

This pairing **can be inbred**, and the check is: the two share a parent, or one
is the other's parent. Grandparents are *not* checked (see the disagreements
section). An inbred pairing is allowed — it is degraded, not forbidden:

- Aptitude mutation **inverts**: 10% up, 25% down instead of 25% up, 10% down.
- Epic mutation drops from **5% to 1%**.
- The Speed, Crit and Yield genes each lose **25%** of both copies.

## 3. What a child inherits

### The four aptitudes — the part that matters

For each aptitude **independently**:

1. **One parent's exact value is copied**, weighted by who is stronger in it:
   `P(from parent A) = A / (A + B)`.
   A parent at 12 against a parent at 4 gives a **75%** chance of the 12.
2. **A drift roll**: 25% `+1`, 10% `−1`, 65% unchanged. Inverted if the pair is
   inbred.
3. **If an epic mutation fired**, `+1` on top of every one of the four.
4. Clamped to `0 … 50`.

So the reachable band, before the epic roll, is exactly
`min(a,b) − 1` to `max(a,b) + 1`. That is the band the preview quotes.

**The consequence that is the whole design:** each aptitude independently
favours whichever parent is better at it. Cross a fighter `(12,4,4,4)` with a
gatherer `(4,12,4,4)` and the child comes out around `(12,12,4,4)` — good at
both. You do not want two similar parents. You want two different ones.

**The consequence that makes the village necessary:** a child copies a value
that already exists in the pair. The only way to exceed it is the `+1` drift or
the epic `+1`, which together average about **+0.15 per aptitude per
generation**. Crossing your own characters converges on what you already have.
Outside blood is the only thing that puts a new number into a bloodline.

### The epic mutation

5% of the time (1% if inbred): `+1` to all four aptitudes, plus a permanent epic
mark on the child. The preview's bands deliberately **exclude** it — widening
every band by one to describe a 1-in-20 event would make the common case a lie.

### The four genes

Race, Speed, Crit and Yield. Each is a pair of numbers, a dominant copy and a
recessive one.

- Each parent passes **one of its two copies at random**, 50/50.
- The **higher** of the two received copies becomes the child's dominant, the
  lower its recessive.
- Per gene, a mutation chance of `max(0.1%, 1.5% × 1.12^−generation)` flips the
  low bits of both copies. It **shrinks** every generation: 1.5% at generation
  0, about 0.85% at generation 5, and it bottoms out at 0.1%.

What they do: **Speed** and **Crit** feed attack speed and crit chance;
**Yield** adds `+4% gathering yield per point`, online and offline; **Race** is
the race, and a pair whose Race dominants differ cannot breed at all.

Genes are a slow curiosity. Aptitudes are the axis a season leaves standing.

### What the child is, on arrival

- **Level 1**, and a **Child** (`AgePhase 0`).
- Sex is a coin flip.
- Generation = the higher parent's generation + 1.
- Placed at the **end of the roster**, not in a played slot.

**A child does not grow up on its own.** Only characters in the three played
slots age, so a newborn stays a Child until you **field** it from the Hall of
Ancestors. Once fielded it needs roughly **one hour of ticked play** to become
an Adult, and then level 50 before it can breed in turn. How many slots you have
is set by the Town Hall: slot 2 at Town Hall 3, slot 3 at Town Hall 5.

### Cooldown

Both parents in a hero × hero pairing, and the hero alone in a hero × newcomer
pairing, rest for **one hour**. There is no gestation: the child exists the
instant the pairing is confirmed.

## 4. What a newcomer contributes, and where they come from

A newcomer contributes **one race, one sex, and four aptitudes**, and nothing
else. They have no level, no gear, no pedigree and no generation — a newcomer is
generation 0 by definition, so the child's generation and the gold price both
key off the hero alone.

Their aptitudes are rolled `2 + random(0 … Inn level)`, **capped at 20**. This
is the two-phase climb the whole system is tuned around:

- **0 → 20 is village-driven.** Build the Inn, better people arrive, marry good
  blood in.
- **20 → 50 the village cannot reach.** Past 20 it is drift and selection across
  generations only — the veteran axis, measured in seasons.

The **Inn** is the single lever and it drives both halves:

| Inn level | Someone arrives every | The village holds | Aptitudes roll |
|---|---|---|---|
| 0 | 48h | 6 | 2 |
| 1 | 46h | 7 | 2–3 |
| 5 | 38h | 11 | 2–7 |
| 12 or more | 24h (the floor) | 16 (the ceiling) | 2–20 (the ceiling) |

Interval is `48h − 2h × Inn level`, floored at 24h. Capacity is
`6 + Inn level`, capped at 16.

**A full village stops the clock entirely.** Nothing is banked against a slot
freeing up later, so a mediocre newcomer occupying the last slot is costing you
the arrival you would otherwise have had. Sending them on is a real move.

Races roll uniformly across **Human plus every race you have unlocked** by
clearing a region boss — never the locked ones, because breeding refuses a
mixed-race pair and a newcomer of a race you own no character of is a portrait
and nothing else.

A **feast** buys an arrival immediately for `2,500 × 1.6^n` gold, where `n` is
how many feasts you have already thrown this season. Roughly
2,500 / 4,000 / 6,400 / 10,240 …

A season begins with **two newcomers**, rolled at Inn level 1.

## 5. What survives a season, and what is lost

The rollover is the reason breeding exists. It runs server-side with everyone
disconnected, so nothing prompts you: everything below is decided in advance by
what you built and what you marked.

**Carries:**

- The **Hall of Ancestors roster** — up to the cap — with each ancestor's
  aptitudes, genes, generation, epic mark, and recorded parents.
- Village **buildings** (including the Inn and the Breeding Grounds).
- Race masteries, unlocked races, diamonds, purchased **Inheritance** levels,
  purchased Hall slots, Seals and the permanent skill points they pay, your best
  season rank, and any paid respec grants.

**Is lost:**

- Every character's **level** — everyone is reset to level 1.
- All **gear**, all **gold**, every other material, the market, the chronicle
  pass, and every point spent in the skill tree.
- **The entire gene pool.** Newcomers *and* elders are deleted, along with the
  arrival clock and the escalating feast price. This season's Inn decides this
  season's blood; next season starts from two fresh newcomers again.

One quiet detail worth knowing: the rollover sets every surviving ancestor to
**Level 1 and Adult**. So the whole roster is breeding-age on day one — and
nobody can actually breed until somebody is back at level 50.

### The cull

The Hall holds **10** ancestors, **+1 per diamond slot bought**, hard cap
**14**. Slots cost 250 diamonds, doubling: 250 / 500 / 1,000 / 2,000.

If you own more than the cap when the season turns, the surplus is **deleted** —
the character and its lineage row both. The order of survival is:

1. **Your main character, always.** Their id *is* the account's id; culling them
   would break the account, not lose a character.
2. **Whoever you marked "Keep".** Nothing outranks a mark but the rule above.
3. Then the **highest aptitude total**, then **epic**, then the **later
   generation**, then a stable tiebreak.

Marking more than the cap is legal — the same ranking resolves it. The Hall of
Ancestors screen shows who would go if the season ended right now, faded.

## 6. The loop this is all for

1. Start the season with your best child. Level 1, like everyone, but carrying
   the bloodline.
2. Play, level, gear up. Rebuild the village — **this** season's Inn decides
   what blood you can marry in.
3. Late in the season, at level 50, breed. Marry newcomers in for numbers your
   line does not have; cross your own to combine two specialists.
4. Mark who carries. The season ends, gear and levels go, the bloodline stays.
5. Field your best child and start again a little stronger.

An aptitude point is worth `1.5%` up to 20, `0.7%` from 21 to 35, and `0.3%`
from 36 to 50 — `+30%` / `+40.5%` / `+45%` cumulative. Deliberately diminishing,
so a veteran's advantage is visible but never decisive on a shared seasonal
leaderboard.

---

## Where the spec and the code disagree

`LONG_GAME_SPEC.md` §3 and §5 are the design; these are the places the shipped
code says something else. **The code is what players get**, so this page
describes the code.

1. **Cooldown is one hour, not 24.** `BreedingEngine.BreedingCooldownSeconds =
   3600`. The spec's "one child per day, and no separate global limit is needed"
   is therefore not in force — the real cap is one child per hero per hour, and
   gold.
2. **There is no gestation.** The spec specifies 8 hours of real time that ticks
   offline, "conceive in the evening, meet the child at the morning login". The
   engine inserts the `CharacterRecord` inside the same transaction as the
   payment. The child exists immediately.
3. **The inbreeding check is two levels shallower than specified.**
   `BreedingAptitudes.AreRelated` implements the spec's "shares a parent **or a
   grandparent**" and takes grandparent arrays — but `BreedingEngine` never
   calls it. Both the engine and the preview use an inline expression covering
   parent-child and full/half siblings only. Cousins breed at full mutation
   rates and 5% epic.
4. **Breeding costs gold and the spec never mentions a price.** §3's "this is
   why it costs nothing" is about the level-1 child landing where everything
   resets, not about the ledger — but `500 × (generation + 1)` gold is a real
   cost the spec does not describe.
5. **A newborn cannot grow up without being fielded.** `ProcessAgeSlot` only
   ages the three played slots, and a newborn is placed at the end of the
   roster. The spec's ageing model does not mention this, and it is the single
   most surprising thing in the system: a bred child sits at `AgePhase 0`
   forever until the player fields it from the Hall, and Town Hall level decides
   whether there is anywhere to field it to.
6. **§6's "Not built: the aptitudes do not yet survive the season rollover"
   is stale.** `SeasonalRotationEngine` now deliberately does not wipe
   `character_lineage_registry` and says so in a comment; the cull runs against
   it instead.
7. **`VillagerArrivalRules`'s own doc comment says "NOTHING CALLS THIS YET".**
   `VillageArrivalEngine` calls it throughout. Stale comment, working code.

## Was this page hard to write?

Mostly no — the aptitude half is a genuinely clean design and one paragraph
explains it. Three parts resisted, and each is a design question rather than a
UI one:

1. **The genes are a second, parallel inheritance system that no player can act
   on.** Aptitudes are chosen, weighted, previewable and consumed by four
   systems. Genes are rolled 50/50 from copies nobody can see, mutate at a rate
   that *shrinks* the longer you play, and pay out as small unnamed bonuses to
   attack speed, crit and gathering yield. Explaining them honestly means
   writing four paragraphs about a mechanic whose correct player response is
   "ignore it". They are the part of the model that could be cut with the least
   loss and they are why the preview needs two sections instead of one.
2. **Ageing is invisible and gates everything.** "Level 50 and an Adult" reads
   like one requirement and is two, one of which is satisfied by playing and the
   other only by fielding the character into a Town-Hall-gated slot and leaving
   the game running for an hour. Every sentence about breeding a child has to
   carry an asterisk about how it becomes a parent.
3. **"Inheritance" names two unrelated systems** — what a child inherits, and
   the diamond shop of permanent account bonuses. Section 0 resolves it by
   reserving the noun for the shop, but that is a papering-over: the two will
   keep colliding in the Wiki and in tutorial copy.

Nothing here is a recommendation to change mechanics. See the report attached to
this work for the one simplification worth considering.
