# Task 54: cosmetic chests, avatars and frames - implementation plan

> Project skills used: `run-stack`, `verify`, `deploy`, `add-command` (only for
> the one new drop kind on `ResponseLootDropPacket` - a constant, not a layout
> change). Everything else is REST.

**Goal:** rewards that add no power. Four rarities of cosmetic chest drop from
monsters and arrive free every fifth level; opening one gives an avatar or a
frame of that rarity; both chests and cosmetics can be traded on the market.

## Owner decisions (2026-09-28)

| Question | Decision |
|---|---|
| Where they show | Most places a player's name appears; not where they would obstruct (list below). |
| How earned | Chests from monster kills (live AND offline) and one free chest every 5th level. The chronicle pass may hand out chests later. |
| Chest rarities | Common, Rare, Epic, Legendary. |
| Level chest roll | 50% Common / 30% Rare / 15% Epic / 5% Legendary. |
| Monster drop rate | One rate PER chest rarity, matching an item tier: Common = Mythic, Rare = Relic, Epic = Ancient, Legendary = Divine. |
| Market | Both unopened chests and owned, un-worn cosmetics are tradable, at whatever price the seller sets. |
| Existing players | Backfilled: one chest per 5 levels already reached, granted once. (Today one account qualifies: level 96, 19 chests.) |
| Art | Avatars from the 25 painted monster portraits; frames drawn as SVG in the client. The owner may replace the frames. |

## The numbers

**Monster drops.** Per kill, independently of luck (a cosmetic economy should
not inflate with Fortune; `PowerCeilingTests` is untouched because nothing here
is power). Rates are `EquipmentDropChance (0.15) x RarityTier.BaseShare(tier)`,
where the share is the tier's weight over the WHOLE weight table, Normal's flat
100 included (total 196.67). The first draft of this table divided by 100 and
overstated every rate twice over:

| Chest | Like an item of | Chance per kill | About one in |
|---|---|---|---|
| Common | Mythic (0.5) | 3.81e-4 | 2,622 kills |
| Rare | Relic (0.1) | 7.63e-5 | 13,111 |
| Epic | Ancient (0.05) | 3.81e-5 | 26,222 |
| Legendary | Divine (0.01) | 7.63e-6 | 131,111 |

One roll per kill against the cumulative ladder, so at most one chest per kill.
The constants live in one `CosmeticRegistry` and a test asserts them against
`RarityTier`'s weights and `EquipmentDropChance`, so a retune of either moves
this table or fails loudly.

**Level chests.** One per multiple of 5 of `PlayerRecords.CurrentLevel`,
rolled 50/30/15/5 at grant time.

**Market price: the seller's choice (owner, 2026-09-28).** No corridor, unlike
equipment. The only bounds are 1 gold and a technical ceiling of
1,000,000,000 that keeps the arithmetic far from overflow. Same market tax as
equipment. Known and accepted: a free price lets a player move gold between
their own accounts by over-paying for a chest; at today's population the owner
chose freedom over the guard. If that changes, a corridor is one check in the
list handler.

## Content

**Avatars (25)** - the canonical monster portraits
(`client/Assets/Images/SpritesWeb/Locations/0N/Monsters/*.webp`):

| Rarity | Avatars |
|---|---|
| Common (8) | Region 1 and 2 regulars: Field Mouse, Horned Rabbit, Meadow Viper, Wild Boar, Thorny Vine, Gray Direwolf, Forest Dryad, Mountain Bear |
| Rare (8) | Region 3 and 4 regulars: Desert Crab, Ashen Basilisk, Ember Elemental, Sandstone Golem, Ice Bat, Snowy Yeti, Glacial Wraith, Rock Giant |
| Epic (6) | Region 5 regulars + the region 1 and 2 bosses: Grave Ghoul, Fortress Gargoyle, Dark Necromancer, Death Knight, Alpha Wolf, Shadow Lynx |
| Legendary (3) | The region 3-5 bosses: Magma Wyrm, Frost Titan, Malakor |

Every player also has their own race portrait (the 12 `Characters/*.webp`) as
a free default avatar; it is not a chest item and cannot be sold.

**Frames (16)** - 4 per rarity, SVG rings drawn in `lib/ui/CosmeticFrame.svelte`,
ornament growing with rarity (plain band -> studded -> knotwork -> animated
glow, the glow off under `prefers-reduced-motion`). "No frame" is the default.

A chest of rarity R gives a uniformly random avatar or frame of rarity R
(41 items: 25 avatars + 16 frames; the pick is uniform over that rarity's
pool, so Legendary is 3 avatars + 4 frames).

The catalogue is a server-side registry (`CosmeticRegistry.cs`, ids like
`avatar_malakor`, `frame_epic_2`), served to the client by
`GET /api/v1/cosmetics/catalogue`. The client keeps a map id -> picture only
(the same pattern as item icons), and a test fails if a catalogue id has no
picture.

## Storage

- **`cosmetic_items`** (snake_case `[Table]`): `Id`, `PlayerId`, `Kind`
  (0 chest, 1 avatar, 2 frame), `DefinitionId` (`chest_rare`, `avatar_malakor`,
  `frame_epic_2`), `Rarity` (1-4), `Source` (0 kill, 1 level, 2 market),
  `AcquiredAtUtc`, `IsListed`. One row per owned thing - duplicates are rows,
  and a row is what the market moves. Volumes are tiny (see the rates).
- **`PlayerRecords`**: `EquippedAvatarId` (string, null = race portrait),
  `EquippedFrameId` (string, null = none), `LevelChestsGranted` (int).
  None of the three is on `StateUpdatePacket`, and none is written by the
  checkpoint: REST writes the first two, the grant worker writes the third.
- Migration is additive; the backfill is NOT done in SQL (see below).

## Grant paths

1. **Kills (live and offline).** Both already arrive at
   `CombatLootEngine` as `CombatLootDropRequest` with `Kills`. The chest roll
   runs once per kill inside the request's existing transaction and inserts
   `cosmetic_items` rows. A drop is reported on `ResponseLootDropPacket` with a
   new `DropKindCosmeticChest = 3`, `QualityTier` = chest rarity,
   `InstanceId` = the row, so the loot list and the reveal show it.
2. **Levels (all three level paths at once).** Rather than hooking
   `ProgressionEngine`, `ApplyBulkExperience` and `ApplyCombatXp` separately
   (CLAUDE.md: "three paths grow a level, and each one has to be told
   separately"), the tick compares `CurrentLevel / 5` with an in-memory
   `LevelChestsGranted` (hydrated at login) once per player per tick. When it
   is behind, it enqueues a grant and advances the in-memory counter. The
   worker is idempotent: under a row lock it reads the DB counter, inserts one
   chest per missing milestone and writes the counter, in one transaction. A
   relogin that re-enqueues can never pay twice. **The backfill is this same
   path**: an existing account's counter starts at 0, so its first tick after
   release grants its owed chests.
   The worker is a guarded `StartCron` loop, registered in
   `CronWorkerGuardTests`' inventory, with a per-item try/catch and a bounded
   drain.

## REST

| Endpoint | Does |
|---|---|
| `GET /api/v1/cosmetics/catalogue` | The 41 definitions with rarity. |
| `GET /api/v1/cosmetics` | My chests (counts by rarity), owned cosmetics, equipped ids. |
| `POST /api/v1/cosmetics/open {Rarity}` | Opens one of my chests of that rarity: deletes the chest row, inserts the rolled cosmetic, answers what it was. |
| `POST /api/v1/cosmetics/equip {Kind, CosmeticItemId or null}` | Wear an owned avatar/frame, or go back to the default. |
| `GET /api/v1/cosmetics/worn?ids=1,2,3` | Batched lookup of other players' equipped avatar/frame (and race), for chat. |
| Phase 4: `GET /api/v1/market/cosmetics?kind=&rarity=`, `POST /api/v1/market/cosmetics/list {CosmeticItemId, Price}`, `.../buy {ListingId}`, `.../cancel {ListingId}` | The market (below). As built: its own `cosmetic_market_listings` table. |

Every POST holds the account stripe (the router already does) and every
refusal answers a reason the screen shows as a sentence - no silent rollback.
A worn cosmetic cannot be listed; listing requires un-wearing it first.

## Where they show

Shown: the profile modal (large), leaderboards, chat messages and the chat
member list, the guild roster, the world boss damage board, and the player's
own header/Character figure. Not shown: the tab bar, the combat view, the
loot list, market rows (only the item itself), and any row already at its
touch-target height where a 32px portrait would not fit - these get nothing
rather than a squeezed picture. One `Avatar.svelte` component (portrait +
frame, sizes sm/md/tile/lg) is used everywhere so the rule is in one place.
**As built:** every place uses the one batched `worn` lookup
(`stores/worn.ts`, `PlayerAvatar.svelte`) rather than growing
`AvatarId`/`FrameId`/`RaceId` on six different payloads - six copies of one
fact would be the drift this codebase keeps paying for.

## The screen

A **Wardrobe** screen (menu group with Character; not a tab-bar tab):
chests by rarity with an Open button and a short reveal, then a grid of
owned avatars and frames with Wear; unowned ones are shown dimmed so there is
a collection to complete. A dot on the Character tab when a chest is unopened.

## Phases

1. **Content and storage**: registry, migration, catalogue + my-cosmetics
   GETs, equip. Tests: registry pool sizes, rate constants vs `RarityTier`,
   equip refuses what you do not own.
2. **Grants**: the kill roll in `CombatLootEngine` (with the offline path
   proven by a `Kills = N` request), the level worker with backfill, the new
   drop kind. Tests: level 96 -> 19 chests exactly once across two relogins;
   a worker started in a test is stopped in `finally` (static queues).
3. **Open + the Wardrobe screen + Avatar component + the places it shows.**
   `exercise.mjs`: grant a chest through a dev endpoint
   (`POST /api/v1/dev/cosmetics/chest`, 404 without `FOLKIDLE_DEV_TOOLS`),
   open it, assert the collection grew, wear it, assert the profile reports
   it, then restore the fixture's previous avatar.
4. **Market**: listing/buying/cancelling chests and cosmetics at the
   seller's price, with the tax; the Market screen gets a "Cosmetics" tab.
   `exercise.mjs` round-trips a listing (list, cancel, assert returned).

Phases 1-3 ship together; 4 can ship after.

## Done when

`exercise` covers open/wear/list, the geometry checkers are clean, the server
suite is green, and a new account at level 5 receives exactly one chest.
