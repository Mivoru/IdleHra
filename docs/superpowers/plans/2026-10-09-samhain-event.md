# Samhain (Halloween) event, the seasonal event framework, and pets

Owner request 2026-10-09. Decisions marked **(owner)** were answered by the
owner; the rest are recommendations accepted with the plan.

## Window

- Event goes live **as soon as PR 1 is deployed (owner)**, so it can be tested
  on production, and ends **2026-11-07 00:00 UTC** (after Oct 31 and Samhain
  itself, Nov 1).
- Shop grace: the shop stays open **3 days after the end**, then the currency
  is removed. **(owner)**
- In-game name is **Samhain**, not Halloween. The lore is that The Cailleach
  is angry the festival is called Halloween and wants to hold the land in an
  endless winter.

## 1. Seasonal event framework (reused for Christmas, spring, summer, filler)

- One definition per event in `SeasonalEventRegistry` (C#, like
  `CosmeticRegistry` - compile-checked and testable, no loader): id, start/end
  UTC, grace days, currency name, per-action chances, offline factor, daily
  cap, shop. The client reads all of it from `GET /api/v1/event`.
- The server decides whether an event is active from the calendar, like
  `WorldBossCalendar`. A dev endpoint (404 unless `FOLKIDLE_DEV_TOOLS=1`) opens
  it out of season - a calendar-gated feature without one is untested most of
  the year.
- Independent of `GlobalEventType` (Blood Moon etc.), which is a rotating
  buff, not a seasonal event.

## 2. Currency: pumpkins

- A chance to drop on every kill and every gathering action (mining,
  woodcutting, fishing). Offline earns too, at a reduced rate.
- The rate is set from a MEASUREMENT of production action counts per player
  per day, not from intuition: pick a target pumpkins-per-active-day, divide.
- Currency removed after the grace period.
- **Measured 2026-10-09**: production `player_stat_samples` show 360-670
  kills an hour on live accounts; `GatheringToolEngine.ComputeRequiredTicks`
  puts mastery 75 at ~1,600 harvests an hour on a region-5 node and the top
  account (mastery 326) at 3,000-12,000. Set: **kill 5%, harvest 1%** (an hour
  of either work pays about the same).
- **Owner, 2026-10-09: offline pays the same as online, and NO daily cap** -
  a player who plays more earns more; prices get adjusted instead. Expected:
  a fighter plus two gatherers around the clock ~1,500 a day, a lone new
  character ~250. Proposed prices (owner to confirm): avatars 1,000, shop
  pets 3,000 - ~23,000 for everything, two weeks for a dedicated account.
- Storage: `PlayerRecords.EventCurrency/EventCurrencyEventId/DayKey/
  EarnedToday`, owned by the live payload like diamonds (rolled and spent on
  the tick, written by the checkpoint). Wire: `StateUpdatePacket.EventCurrency,
  EventCurrencyEarnedToday, SeasonalEventId, SeasonalEventPhase` (855 -> 863).
- Buying is the `BuyEventShopItem` (81) command: the tick takes the price, the
  engine saves the row under the PlayerRecords row lock, and anything that
  does not end in a saved row is refunded. Results 63-66.
- Dev: `POST /api/v1/dev/event {EventId}` forces an event live (0 = calendar),
  `{Grant: n}` adds currency - exercise.mjs uses both.

## 3. Pets (permanent, survive the event and rebirth)

- **One active pet per character (owner).** Each pet is owned ONCE per
  account and equipped on at most one character at a time; duplicates cannot
  be bought. The collection (7 pets) is therefore the cap, so the bonuses do
  not grow with the character count.
- Price: 3,000 per shop pet (proposed, see the currency section).
- Strength **(owner)**: shop pet +5% to one stat; rare-drop pet +8% to two
  stats; boss pet strongest. Every pet bonus enters `PowerCeilingTests`.
- Proposed mapping to the existing art (`client/Assets/Images/WithWhiteBackground/Halloween/pets`):
  Ghostie XP, Pixie gold, Wolf pup damage, Skeleton dog gathering speed,
  Black cat drop chance, Mini vampire crit damage, Witch world boss damage
  (rare drop from any kill/gather action, ~1 in 20,000 - to be measured).
- Later: extra pet slots are the intended reward for capped rebirth ranks
  (see the rebirth rework, not part of this plan).

## 4. Seasonal boss: The Cailleach

- A tab in the world boss window. Six tiers on the boss-ascension pattern:
  tier N is as strong as region N's boss, tier 6 an extrapolated region-6
  boss.
- Rewards: pumpkins per win; first clear of a tier pays an avatar/cosmetic;
  tier 6 first clear pays the title "Breaker of the Long Winter" and the boss
  pet. **Diamonds on first clear of a tier only (owner)**, at least **120 in total
  (owner)**: 10 / 15 / 20 / 20 / 25 / 30.
- **Unlimited attempts (owner).** With no daily cap, the pumpkins a win pays
  must be tuned so farming the boss is not much better per hour than farming
  monsters of the same region - measured, like the drop rate.

## 5. Shop placement

- One component, a pumpkin chip ("pumpkin icon + count"). Desktop: between
  the Active Event banner and gold/diamonds. Mobile: under the game name next
  to the currencies.
- Tapping it opens the Event screen with Shop / Boss / Story tabs. Add the
  route to `client_web/scripts/screens.mjs`.
- Shop: 5 pets, 8 avatars (Banshee, Dullahan, Jack-o'-lantern, Pooka,
  Pumpkin, Skeleton, Vampire, Werewolf), possibly gold/consumables.

## 6. Theme

- `data-event="samhain"` on `<html>` swaps a few colour tokens (orange,
  purple) and the loading/opening screens. No particles or animation;
  `check:perf` must stay green.
- The chest-opening stage (`/chest/bg-*.jpg`, the owner's "opening screen")
  and the loading screen swap to the Samhain paintings while the event runs
  and return after it.
- Assets: avatars, pets, the boss and the currency icon all have white
  backgrounds - key them out, convert to AVIF through the existing pipeline.
  **Avatars are cropped to the head and upper body (owner)**, not the whole
  figure, so they read at portrait size.

## Delivery

Three PRs, so a slip in a later one does not block the launch:

1. Framework + pumpkins + shop + chip + theme (the launch minimum, merged and
   deployed by 2026-10-22).
2. Pets.
3. The Cailleach.

Each PR is verified with `npm run exercise` (a new step that earns and spends
pumpkins and round-trips a pet equip), not with smoke tests.

## Deferred (separate design sessions, need measurement)

- **Rebirth rework**: today a rebirth wipes gold, materials, equipment,
  market listings and the skill tree for +3% damage (cap 15%). Direction:
  a capped number of Renown ranks paying more and on more axes, incl. pet
  slots, so a newcomer can still catch up.
- **Breeding onboarding**: keep the mechanics, make them visible - a child
  preview (`GeneticSplicingEngine.PreviewLocus` already computes the range),
  a "recommended pair" button, and a short explainer of the two phases
  (Inn gets you to 20, only mutation goes past it).


## Built - PR 2 + 3 (2026-10-10)

**Pets** (`PetRegistry`, `PetEngine`, table `player_pets`):
- Ghostie +5% XP, Pixie +5% gold, Skeleton Dog +5% gathering speed, Black
  Cat +5% drop chance, Wolf Pup +5% crit damage - shop, 3,000 each
  (owner confirmed). Witch +8% world boss damage and +8% drop chance - rare,
  1 in 10,000 per pumpkin earned (a busy account finds her in about a week).
  Mini Vampire +10% damage and +10% world boss damage - The Cailleach's
  sixth winter, first clear (owner swapped it with the Wolf Pup, 2026-10-10).
- A pet's bonuses fold into the character's `EquippedAffixTotals` in
  `ComputeEquippedTotalsAsync`, so live, offline, the world boss and the
  guild-war snapshot all read them through the one function each stat
  already had. Four fields were added for stats no affix rolls (XP, gold,
  gathering speed, world boss damage). `PowerCeilingTests` lists the pet lever.
- Placing: `POST /api/v1/pets/assign`, Character > Gear. Both unique indexes
  (one of each pet per account, one pet per character) are in the database.
- Found on the way: the equip update's six TOOL fields had no writer, so any
  equip zeroed tool tiers and tool affixes until the next login. Fixed in
  `EquipmentSlotEngine.BuildNotificationAsync`, regression in `PetTests`.

**The Cailleach** (`SeasonalBossRegistry`, `SeasonalBossEngine`, table
`seasonal_boss_clears`, opcode `StartSeasonalBoss = 82`):
- Tier N is region N's boss at its FIRST-CLEAR wall, even when beaten. Tier 6
  is region 5's wall +20% attack and +50% health - MEASURED in
  `SeasonalBossTests`: +50% attack beat even Transcendent/Legendary level-100
  gear (boss attack is a cliff); at +20% region 5's wall gear dies in 22 s
  and the best gear wins in ~33 min.
- A tier opens after its region's boss and the tier below. First clear pays
  diamonds 10/15/20/20/25/30 (=120), gold, 250-1,500 pumpkins, the titles
  "Frost-Touched" (III) and "Breaker of the Long Winter" (VI), and the Mini
  Vampire (VI) - by mail, in the transaction that records the clear. A repeat
  win is an ordinary kill (5% pumpkin), so unlimited attempts are no farm.
- Shown on the World Boss screen and the event screen's Boss tab; the combat
  screen names her and shows her portrait while a tier is armed.
