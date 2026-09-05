# FolkIdle Current Implementation State

Status: living document, snapshot of the technical layout as of the most
recent hardening pass. Update this file whenever a structural change lands;
do not let it drift into aspirational/planned content - that belongs in
`NEXT_STEPS_BACKLOG.md`.

## 1. Solution Layout

- `server/FolkIdle.Server/` - the ASP.NET-hosted game server (single
  process, `Program.cs` entry point). `Engine/` holds 109 source files
  (gameplay/simulation/background-worker logic); `Models/` holds 66 (EF Core
  entities, DTOs, and `FolkIdleDbContext`); `Migrations/` holds 55 applied
  migrations. `Domain/` has since been split into five sub-namespaces -
  `Combat`, `Economy`, `Progression`, `Social`, `Shared` - so the "both
  directories are flat" note that used to sit here no longer holds; the
  remaining flatness is inside `Engine/` only.
- `server/FolkIdle.Server.Tests/` - xUnit integration test project, uses
  Testcontainers to spin up a real Postgres 16 instance per test collection
  (`PostgresTestFixture`).
- `client_web/` - **the client players actually run.** Svelte 5 + Vite,
  twenty-four screens under `src/routes/` and twenty-five shared components
  under `src/lib/ui/`. Its wire types are GENERATED from the server's own
  `--dump-protocol` output and committed; nothing here mirrors a packet by
  hand. Verified by `scripts/exercise.mjs`, which drives a real browser
  against a real server and asserts the world changed.
- `client/` - the retired Unity 6000.5.2f1 project. Kept for the shared
  artwork and audio, which the web client fetches from the server rather than
  duplicating (`client/Assets/Resources/Audio/README.md` is the live
  reference for what plays when). The C# under `Assets/Scripts/` is history.
- `docs/architecture/` - this documentation set (new as of this pass).
- `CLAUDE.md` at the repository root - the operating rules an assistant loads
  automatically. `AGENTS.md` is a POINTER to it, deliberately not a copy, so
  other tools read the same file rather than a second one that drifts.
  `.claude/` carries the project's skills, subagents and hooks; only
  `settings.local.json` is git-ignored. The hooks enforce three rules
  mechanically - see `CLAUDE.md`'s "What is enforced for you".

## 2. Core Tick Architecture

`SimulationEngine` (`Engine/SimulationEngine.cs`) runs a single dedicated
thread at 10 Hz and owns `_activePlayers`, a
`Dictionary<long, TickStatePayload>`. Because exactly one thread ever reads
or writes this dictionary, in-memory per-player state (stats, active
activity, combat progress, inventory counters, etc, all packed into the
`TickStatePayload` struct in `Engine/TickStatePayload.cs`) requires no
locking and no allocation to mutate - the tick loop uses
`CollectionsMarshal.GetValueRefOrNullRef` to get a `ref TickStatePayload`
and mutates fields in place.

Any engine method that needs to touch the database runs off the tick thread
(`Task.Run`, or a background polling cron) and reports results back to the
tick thread via one of the 35 `ConcurrentQueue<T>` members exposed on
`PlayerSessionRegistry` (`Engine/PlayerSessionRegistry.cs`) - e.g.
`MarketMatchQueue`, `ForgeUpgradeQueue`, `QuarantineNotificationQueue`,
`CombatLootDropQueue`. The tick loop drains these queues every frame
with zero-allocation `TryDequeue` calls and folds the results into the live
`TickStatePayload`. `PlayerSessionRegistry` also tracks a simple
`ConcurrentDictionary<long, bool> _onlinePlayers` used by `IsPlayerOnline`
to decide whether a result should be queued for live delivery or written
straight to the database for an offline player.

This queue-drain pattern is the backbone nearly every engine in the
codebase depends on for correctness; see Section 7 for why it was not
touched in this pass despite a request to make it "stateless".

## 3. Persistence and Migration State

Postgres via EF Core (Npgsql), `IsolationLevel.Serializable` transactions
combined with explicit `FOR UPDATE` row locks are the standard pattern for
every DB-mutating engine method (see e.g.
`MarketOrderBookEngine.PlaceLimitOrderAsync`,
`AntiCheatTelemetryEngine.RequestShadowBan`).

EF's default table naming is PascalCase and matches the C# class name
(pluralized in some cases, e.g. `PlayerRecords`). Raw SQL against these
tables must double-quote identifiers (`"PlayerRecords"`, `"MarketOrderRecords"`).
A small set of entities carry an explicit `[Table("snake_case_name")]`
override and must be referenced unquoted or snake_case-quoted in raw SQL:

| Model                          | Table name                     |
|---------------------------------|---------------------------------|
| CharacterRecord                | characters                      |
| CharacterLineageRegistry       | character_lineage_registry      |
| HistoricalMarketArchive        | historical_market_archives      |
| PlayerAchievement              | player_achievements             |
| PlayerRaceMastery              | player_race_masteries           |
| PlayerLifetimeAchievement      | player_lifetime_achievements    |
| PlayerWorldBossAttempt         | player_world_boss_attempts      |
| MonsterCodexEntry              | monster_codex_entries           |
| ConversationMessage            | conversation_messages           |

(`MentorshipAcademyAssignment` and `VillageResident`/`VillageInfrastructure`
carry `[Table(...)]` overrides too, but to their default PascalCase names -
those overrides exist for other EF reasons, not casing.)

Season resets (`Engine/SeasonalRotationEngine.cs`) use
`TRUNCATE TABLE ... RESTART IDENTITY CASCADE` for the three unconditional
full-table wipes (`EquipmentInstances`, `BankEquipmentInstances`,
`MarketEquipmentInstances`) rather than `DELETE FROM`, to avoid per-row WAL
bloat; conditional/partial wipes remain `DELETE`/`UPDATE`.

## 4. Market Subsystem

There are two independent, live, wired trading paths sharing the same
underlying tables:

- **Order-book path** (`Engine/MarketOrderBookEngine.cs`,
  `CommandType.PlaceLimitOrder`): BUY/SELL limit orders matched by
  `MatchOrdersAsync`. The SELL side expects the item to already exist as a
  `MarketEquipmentInstances` row (it does not migrate items out of a
  player's bag itself).
- **Direct escrow path** (`Engine/MarketEscrowEngine.cs`,
  `CommandType.MarketListItem` / `MarketBuyItem`): the actual
  `EquipmentInstances -> MarketEquipmentInstances` bag-to-market migration
  pipeline, with a `FOR UPDATE` lock on both `PlayerRecords` and the target
  `EquipmentInstances` row, an equipped-item guard
  (`PlayerRecord.EquippedWeaponId`/`EquippedArmorId`), and the same
  volatility-corridor check as the order-book path.

Both paths share `MarketOrderBookEngine.CalculateRollingAveragePriceAsync`
(internal, cross-file) for the 7-day rolling average / ContentRegistry
fallback price and the 5%/8%/15% wealth-scaled tax brackets. See
`GAME_DESIGN_SPEC.md` Section 3 for the exact formulas.

Known architectural characteristic (not a bug): every match/listing takes a
`Serializable` + `FOR UPDATE` database round-trip. This is a deliberate
integrity-first tradeoff, not an oversight - see `NEXT_STEPS_BACKLOG.md`
item 4 for how to address contention without giving up transactional
correctness.

## 5. Anti-Cheat and Quarantine Pipeline

`Engine/AntiCheatTelemetryEngine.cs` tracks per-player command timing
variance (`CommandTimingProfile`, a 100-sample ring buffer) and flags
suspected automation via `RequestShadowBan`. A confirmed flag: sets
`PlayerRecord.IsQuarantined`/`Quarantine_Active`, sequesters the player's
active `MarketOrderRecords` listings (`"SellerId"` / `"Status" = 0`),
writes a Redis quarantine flag, and force-disconnects the live WebSocket
session via `NetworkBroadcastSystem.ForceDisconnect`. The same
disconnect-on-quarantine behavior applies to `BillingVerificationEngine`'s
refund-triggered quarantine path.

## 6. Billing / IAP

`Engine/BillingVerificationEngine.cs` relies on `PrimaryPurchaseLedger.TransactionId`
carrying `[Key]` (a genuine Postgres-enforced unique constraint) combined
with a `Serializable` transaction as its idempotency guarantee against
duplicate/replayed purchase receipts - no separate unique index is needed
on top of the primary key.

## 7. Client Network Layer

Fixed-layout packet structs. **Seven of them as of 2026-09-05**, and the sizes
move - read `NetworkPacketLayoutGuard.cs` rather than this paragraph, because
that file is the authority and carries a byte-by-byte history of every change:

| packet | size | direction |
|---|---|---|
| `AuthHandshakePacket` | 530 | client -> server, first message only |
| `ClientCommandPacket` | 340 | client -> server |
| `StateUpdatePacket` | 797 | server -> client, the 10 Hz snapshot |
| `RequestChatMessagePacket` | 139 | client -> server |
| `ResponseChatMessagePacket` | 147 | server -> client |
| `ResponseLootDropPacket` | 22 | server -> client, one per item granted |
| `ResponseCombatEventPacket` | 26 | server -> client, one per resolved blow |

**Every size must be unique.** The binary receive loops demultiplex on byte
length alone, and `Validate()` fails the build on a collision - which it did
when `ResponseCombatEventPacket` was first written at 22 bytes, exactly the
loot drop's size.

The 700-byte structural ceiling on `StateUpdatePacket` is **gone**: it moved to
768 when Fishing and Herbalism mastery arrived, and the packet has since passed
that too. The guard's own comment explains why the number was a discipline
marker rather than a transport limit - nothing fragments at 700, and size-based
demultiplexing stays unambiguous because the nearest other packet is 530.

**THE CLIENT'S COPY IS GENERATED, NOT MIRRORED.** This section used to describe
two hand-maintained guards that drifted; the web client's
`protocol.generated.ts` comes from the server's own `--dump-protocol` and CI
fails on a diff. The Unity guard it refers to belongs to the retired client.

`ResponseCombatEventPacket` (2026-09-05) is the newest and the reason is worth
keeping: **this wire carried no combat event at all** until then. Everything a
player saw during a fight was inferred from the difference between two
`CurrentMonsterHp` snapshots - and a measured trace showed that field taking
exactly ONE value across 27 consecutive snapshots, because a geared character
kills an early monster between two samples. See `docs/TASK_BOARD.md` task 8.

Connections authenticate via a hand-rolled HMAC-SHA256 JWT
(`Engine/AuthenticationEngine.cs`), not a raw bearer Guid. The client obtains
one from `POST /api/v1/auth/login` (device-ID login-or-provision, see
`UiLoginWindow.cs`), then sends it as the very first WebSocket message inside
an `AuthHandshakePacket`; `NetworkBroadcastSystem.HandleClientLoopAsync`
rejects any connection whose first message is not a valid, cryptographically
verified handshake, closing the socket before any gameplay `CommandType`
packet is ever read. A successful handshake force-acquires that account's
`RedisPlayerSessionLock` and evicts any existing session for the same
`AccountId` (same-pod via direct `_connectedClients` replacement, cross-pod
via the `session-evict` Redis Pub/Sub channel), preventing multi-boxing.
Authenticated HTTP endpoints (forge inventory, market browser, breeding
roster/preview, codex, race mastery, achievements) read the same JWT from an
`Authorization: Bearer` header via `TryResolveAuthenticatedPlayerAsync`.

`client/Assets/Scripts/Network/UnsafePacketParser.cs` deserializes inbound
`StateUpdatePacket`s via `Unsafe.ReadUnaligned`. As of this pass it exposes
`TryParseState(buffer, receivedCount, out packet)`, which validates both
`receivedCount` and `buffer.Length` against `Unsafe.SizeOf<StateUpdatePacket>()`
before touching the pointer, rejecting truncated/undersized buffers instead
of reading past them. `WebSocketClient.ParseAndEnqueuePacket` uses this and
drops (logs and returns on) a failed parse rather than propagating garbage
state.

## 8. Test Suite State

**592 server tests, all passing** as of 2026-09-05, verified against a real
Postgres via Testcontainers. The client carries a further **310** (vitest), and
`npm run exercise` drives **120** checks against a running stack.

**Do not quote any of those three numbers from a document, including this one.**
CLAUDE.md says so and it is right: 182, 341 and 470 all appear in different
places in this repo, each true on the day it was written. Run the suite.

Historic note, kept because the two exceptions below were real:

- `E2EGameLoopTest.Test_E2E_ClosedLoopVerification` (the WebSocket 503 noted
  below) now passes.
- `Test_MarketEscrow_ConcurrentListings_ExactReplicaNoSerializationDrift`
  was failing under full-suite load - six concurrent `Serializable` listings
  where five lost the race and `ListItemAsync` swallowed the 40001 and
  returned false. Fixed in the engine, not the test: that method now runs
  under a retrying execution strategy.

**A green suite requires a working Docker daemon.** Every test in
`HardenedEngineIntegrationTests` belongs to the Postgres collection, so a
Docker outage fails all 182 at once with
`PostgresTestFixture.DisposeAsync` NullReferenceExceptions rather than with
anything resembling a code error. Check Docker before debugging a mass
failure.

Historical note, retained because the condition may recur in other
sandboxes: `Test_E2E_ClosedLoopVerification` used to fail with a WebSocket
503 in sandboxed dev environments where the `HttpListener`-based WS endpoint
could not bind/serve correctly.

## 8b. The chrono bank was deleted (2026-09-02)

The chrono bank, Time Warp, chrono cores and the 2x/4x chrono acceleration are
**gone**. What was removed, and the three things that are easy to get wrong if
you meet a stale reference to any of it:

**Opcode 8 survived, renamed.** `ToggleChronoAcceleration` is now
`SetSimulationSpeed` (same value - renaming an enum member does not move an
opcode). It never touched the bank: it sets `SpeedMultiplier`, and the tick pays
for every extra iteration out of `AccumulatedTimeBankMs` at 100ms each. That
path is unrelated to chrono and still works, so `SpeedMultiplier`,
`AccumulatedTimeBankMs` and `CurrentSimulationSpeedMultiplier` all remain.
Opcodes **24, 47 and 48 are retired gaps** and must never be reused.

**Over-cap offline time was already discarded before this.**
`OfflineSimulationEngine.BankOverflowSeconds` had been a no-op for a long time;
deleting the bank changed nothing about offline balance. Time past the cap is
dropped at the `Math.Min` in `SimulateOfflineProgress`, which now says so.

**The packet got smaller.** `StateUpdatePacket` 800 -> **779** bytes,
`ClientCommandPacket` 359 -> **339**. Four state fields went
(`BankedChronoSeconds`, `IsChronoAccelerating`, `VisualBankedChronoSeconds`,
`ActiveChronoLockExpirationTicks` - the first and third were two copies of one
number, read by two different screens) plus four command fields. Both pins in
`NetworkPacketLayoutGuard` moved in the same commit as
`npm run generate:protocol`. The ceiling is 832, asserted by
`Test_StateUpdatePacket_StructuralSizeIsStrictlyUnder832Bytes` - not the ~700
some older notes claim.

**Players were compensated 1:1 into `AccumulatedTimeBankSeconds`**, by
`20260902180220_DeleteChronoBank`. A banked chrono second bought exactly one
extra simulated second, and the surviving speed toggle buys exactly one extra
simulated second per 1000ms banked, so the conversion is at par and needed no
exchange rate. Nine accounts were paid; four unattributable rows totalling 1,823
seconds were written off deliberately and recorded in the migration. `Down()`
restores the schema but never the balances - do not run it against a database
that has served `Up()`.

## 9. Known Dead Code (not yet removed)

- ~~`Engine/SeasonEraEngine.cs`~~ - **gone. This entry was stale.** The file
  does not exist; the only three references to the name left in the repo are
  documentation lines, one of which was this one. `NEXT_STEPS_BACKLOG.md`
  already recorded the deletion and this section contradicted it.
- ~~`Engine/PlayerChronoRegistry.cs` plus a second, unused
  `ChronoBufferEngine.ProcessLoginHandshake` overload~~ - **also stale on both
  counts.** The file does not exist (its table was dropped by
  `20260716203252_DropPlayerChronoRegistry`), and there is exactly one
  `ProcessLoginHandshake`, which *is* called, from
  `Domain/Shared/StateCheckpointManager.cs:1315`.

  Modul: both entries above were checked against the tree on 2026-09-02 and
  found to describe code that had already been removed. A dead-code list that
  names files which no longer exist is worse than no list - it sends the next
  reader hunting for phantoms, and it cost exactly that during the chrono
  deletion pass. Verify an entry here before acting on it.
- `Engine/ForgeSplicingEngine.cs` line ~165: `int.TryParse(targetItem.BaseItemId, out int baseId)`
  is effectively dead - `BaseItemId` is always a descriptive slug string
  (e.g. `gilded_sabatons_boots_armor_slot_base`, from
  `ContentRegistry.GetItemBaseId`), never a numeric string, so this parse
  always fails and `regionTier` silently defaults to 1 for every forge
  affix roll. See `NEXT_STEPS_BACKLOG.md` item 7.

## 10. Explicitly Deferred This Pass

A prior task requested (a) relocating all 40+ Engine/Models files into
domain-driven namespaces, (b) making `PlayerSessionRegistry` fully
stateless via Redis, and (c) replacing the order-book's relational
matching with Redis ZSETs plus an async write-behind pipeline. All three
were deferred by explicit user decision after review, because:

- (b) as literally specified would move the queue-drain mechanism described
  in Section 2 into Redis, which both violates the zero-allocation tick
  constraint (synchronous Redis round-trips every 10 Hz frame) and does not
  address the actual horizontal-scaling blocker, which is `SimulationEngine._activePlayers`
  (the live per-player tick state), not `PlayerSessionRegistry`.
- (c) as literally specified would replace the `Serializable` + `FOR UPDATE`
  transactional matching model with an eventual-consistency Redis structure,
  reversing the double-spend/RMT hardening this codebase has been built up
  around, for a real financial subsystem (gold and item transfers).
- (a) is a valid long-term cleanliness goal but is a 100+ file mechanical
  change with no functional benefit and a large review burden; better done
  incrementally than as a single mass move.

See `NEXT_STEPS_BACKLOG.md` items 3, 4, and 5 for the recommended,
non-contradictory way to revisit each of these.

## 12. Character Roster, Equipment and Activity Model

Three systems that were partly present but unreachable have been completed;
this section is the current truth for all three.

### 12.1 Multi-character slots

A player fields up to `CharacterSlotEngine.MaxCharacterSlots` (3) characters
at once. Slot 2 unlocks at Town Hall level 3, slot 3 at level 5 - the Town
Hall is raised only with `raw_log` and `copper_ore`, which puts extra
characters on the gathering critical path rather than on a level timer.

`TickStatePayload`'s flat activity fields (`ActiveActivityId`, `PlayerHp`,
`CurrentMonsterId`, the `Slot1_*` identity fields, equipment, cached affix
totals) double as the tick's **active-character register**. Each tick,
`SimulationEngine.SwapSlotIntoActiveRegister` swaps a slot's parked
`CharacterActivityState` into that register, runs the ordinary per-activity
tick, and swaps it back. The swap is its own inverse and the loop always
finishes with slot 1 loaded, so the outbound packet, the checkpoint flush and
the offline extrapolation all keep seeing the main character exactly as they
did before multi-slot existed.

`ProcessAccountTick` holds the once-per-tick work that is NOT per character -
character aging, mana regeneration, potion countdowns, child maturation. It
must never move back inside `ProcessSubTick`: running it per slot would age
every character three times as fast and expire potions three times quicker
the moment a second slot was assigned.

Two characters may never run the same activity
(`CharacterSlotEngine.IsActivityOccupiedByAnotherSlot`). Any character may do
anything otherwise - combat, gathering, fishing - so long as no two do the
same thing.

`StateCheckpointManager.LoadPlayerState` orders characters by `SlotIndex`.
This is load-bearing: everything downstream indexes the list by position
(`characters[0]` is the main character whose gear hydrates the register)
while the unlock gate and occupancy mutex key off `SlotIndex`.

### 12.2 Per-character equipment

Equipment lives on `CharacterRecord`, not `PlayerRecord`, in six slots:
Weapon, Helmet, Chest, Gloves, Leggings, Boots (`EquipmentSlotEngine`'s
`Slot*` constants, mirrored by `UiEquipmentSlotsPanel`). The old single
"Armor" slot became Chest, which is where the generic `_armor_slot_` BaseId
fallback still resolves.

Inventory stays account-wide - one backpack, one Village Chest, one larder -
so the player manages a single bag while each character carries its own gear.

Anything that destroys, transfers or re-points an `EquipmentInstances` row
(market listing, forge fusion, mail, seasonal wipe, the inventory screen's
"is equipped" flag, the backpack census) must ask
`EquipmentSlotEngine.IsEquippedAnywhereAsync`, which spans every character
the player owns. Checking one character would let a player sell gear another
is wearing and leave a dangling equip pointer.

Both equip paths run inside `Database.CreateExecutionStrategy().ExecuteAsync`
on a retry-configured context, because rapid equips contend for the same
character row's `FOR UPDATE` lock. The delegate is re-runnable: it clears the
change tracker on entry and returns an outcome rather than enqueuing
anything, so the command result and slot-update notification are published
exactly once, after the strategy settles.

### 12.3 Activity id bands

Combat targets and gathering nodes share one activity id space. They are kept
apart by band (`Engine/ActivityIdBands.cs`):

| Band | Contents |
| --- | --- |
| 1 - 90 | Legacy monsters (not canonical progression content) |
| 91 - 115 | The five canonical regions, four monsters plus a boss each |
| 1001 - 1005 | Woodcutting nodes |
| 2001 - 2005 | Mining nodes |
| 3001 - 3009 | Fishing spots |
| 4001 - 4012 | Herbalism nodes |
| 9999 | World Boss sentinel |

`ProcessSubTick` resolves an activity by checking `TryGetGatheringNode`
first, so any overlap silently makes combat unreachable for the shared ids.
Before the re-key, Region 3's monsters (101-105) sat on top of Woodcutting
101-105: the whole region could not be fought, and the Kobold race unlock
that hangs off the Magma Wyrm's first kill was unobtainable.
`Test_ActivityIdBands_MonsterAndGatheringSpacesCannotOverlap` asserts the
spaces are disjoint outright, so re-introducing an overlap fails there rather
than silently deleting a region.

## 13. Races, Breeding and Sustain

Six races exist (`RaceIds`). Every account starts with a **male/female Human
pair**; `BreedingEngine` requires a male paternal and female maternal parent
of the same race, so a lone character is a genetic dead end and a pair is a
founding population. Each of the five region bosses' first kill unlocks one
further race and grants a male/female pair of it
(`RaceUnlockRegistry`, `CharacterGrantEngine`, detection in `CodexEngine`'s
kill batch, which is the only place that knows a monster's prior kill count).

The auto-eat larder is three persisted slots on `PlayerRecord`, filled with
`CommandType.StockFoodSlot` (65) through `LarderEngine`. `FoodRegistry` holds
the GDD's ten cooked-food heal payouts (40 to 82,000 flat HP); food is
classified by the `_food` BaseId marker, since the ten real cooked foods
(items 194-203) never carried the older `_food_consumable` marker.

The Village Chest (`VillageStashInstances`) is unbounded - unlimited stacks
and unlimited stack height - and every consumption path reads Backpack +
Chest, so stored materials stay spendable at the workbench, forge and market
without being carried back out.

## 14. Anti-Cheat Posture

Two detectors, both deliberately tolerant of slow-but-honest clients after
each was found permanently banning real players:

- **Macro detection** uses the coefficient of variation of inter-command
  intervals, plus a minimum observation window and minimum mean interval.
  Absolute variance cannot distinguish "very regular" from "very fast", so
  it banned anyone clicking quickly. Timer-driven client traffic
  (`PingNetworkDiagnostics`, `AntiCheatChallengeResponse`) is excluded, and
  profiles are discarded on session end.
- **Integrity challenge** asks the client to prove it can compute
  `ComputeChallengeHash`. That is a test of knowledge, not speed, so the
  response window is 15s and escalation needs
  `ConsecutiveChallengeMissLimit` (4) consecutive misses, cleared by any
  answer. It was 500ms with a single-miss ban, which punished mobile latency
  and made automated Play Mode runs impossible.

A quarantine is otherwise irreversible in-game, so
`Program.cs --lift-quarantine <playerId>` exists as an operator path; it also
releases the account's frozen market listings.

## 15. Progression Model (measured, not assumed)

The pacing of this game is analytically solvable because of one invariant:
**every monster grants `XP = MaxHp / 5` and `gold = MaxHp / 20`**. Monster HP
therefore cancels out of any rate calculation, and XP-per-second is a pure
function of the player's DPS - which monster is farmed does not change
progression speed at all, only risk and loot table.

Level cost is `ProgressionEngine.GetRequiredXpForLevel`, the single
authority (it was previously copy-pasted into four places with "must stay in
sync" comments and no enforcement): `400 * 1.06^level`. The exponent tracks
gear power, which triples per region tier - weapon `FlatAttackPower` runs
12 / 36 / 108 / 324 / 972 across the five tiers.

Modelled clear time per region, using weapon base power alone and ignoring
affixes, STR growth and set bonuses - deliberately a floor, not an estimate:
**76 / 127 / 169 / 199 / 222 minutes**, about 13.2 hours total, with
gathering a steady 9-11% throughout.
`Test_Progression_EveryRegionClearsInsideThePlayableTimeBand` fails if any
region leaves the playable band.

Measured live on 2026-08-01 against a real stack: a level-40 character in
tier-2 gear at quality tier 3 sustained **~20 XP/s (~100 DPS)** and levelled
40 -> 41 in 150 seconds. That is roughly 3x the model's floor for that gear,
which is the expected headroom.

Region bosses sit at ~5x the HP and ~2.5x the ATK of their own region's
strongest regular monster (the endgame boss at ~6x / 3x), and above the next
region's opening monster.
`Test_Content_RegionBossesAreContinuousWithTheirRegionCurve` pins the whole
shape, including the reward invariants above.

## 16. Known Value-Computed-But-Never-Consumed Defects

This codebase has a recurring failure mode worth naming: a value is
computed correctly, stored on a struct, threaded through the payload - and
read by nothing. It has produced at least eight shipped bugs (crafting
output, larder writes, loot census, item base power, the affix payload
collision, and the three below). **When adding a stat or bonus, grep for a
consumer before believing it works.**

**The pattern recurred on 2026-09-04/05, three times, in a new direction each
time.** Recording them because "no known outstanding instances" is a statement
with a date on it, not a property of the codebase:

- **`AffixRegistry.RollAffixes` took an `itemRarityTier` and never read it.**
  A dead parameter, and the influence it was named for had never been wired -
  which is why the entire fourteen-tier rarity ladder was worth 1.48x against a
  region step's 3.00x. Now wired; see `docs/TASK_BOARD.md` task 9.
- **`WorldBossAttemptCount` was written in one place and loaded by nothing.**
  After a relogin it read as zero, so the screen showed three unspent attempts
  and the server silently rolled back the click. Found by an exercise run
  reporting "0 -> 2 spent" on a single strike. **`StateUpdatePacketFieldCoverageTests`
  now checks the whole wire for this shape**: every field that travels is either
  loaded at login or on an explicit `RuntimeOnlyByDesign` list with a reason.
- **`EquipmentInstance.IsAffixLocked` was the INVERSE.** Read in ten places -
  reroll, forge fusion, the validator, the market projections - and set to true
  by nothing, so none of that code could ever run. The read side was
  thoroughly wired and the write side did not exist. Built on 2026-09-05.

Everything below was closed earlier and is kept as the record:

- All five 4-piece set-bonus effects are consumed by the combat tick.
  `FireDamageMultiplierPct` and `BurnApplicationActive` in the outgoing damage
  step, `ThornsReflectionActive` and `DamageCapActive` in the incoming one,
  `CooldownReductionActive` at the skill-cast site. The fifth was
  `CcImmunityActive`, which could never fire - this game has no player-facing
  crowd control - and was replaced with the damage cap rather than having a CC
  system invented to justify it. See `NEXT_STEPS_BACKLOG.md` item 19.
- `CombatStats.ForgeSuccessPct` (Luck) modifies the forge fusion roll and
  `CombatStats.OutOfCombatHpRegen` (Constitution) drives an idle regen tick.

The check that matters when adding the next stat: **grep for a consumer.** A
value that compiles, is stored, and rides the payload is not thereby used.

## 17. Client Server Address

`ClientServerConfig.BaseUrl` is the one place the client stores which server
it talks to, resolved `FOLKIDLE_SERVER_URL` -> `PlayerPrefs` -> the
`http://localhost:8080` default. `UiLoginWindow` is the **sole writer** and
publishes in `Awake`; every other class reads a get-only property.

This replaced twenty-five independent `ServerBaseUrl` fields that nothing
ever assigned, which meant the client could authenticate against a real
server while all twenty-two HTTP caches silently queried localhost. Do not
reintroduce a local copy.

## 18. Development Fixture

`--seed-dev` provisions a repeatable playtest account
(`dev@folkidle.local` / `FolkIdleDev123!`): three level-50 characters, all
equip slots filled on the main one, Town Hall 5, materials, gold, and a
stocked larder. It is double-guarded - the flag alone does nothing unless
`FOLKIDLE_ALLOW_DEV_SEED=1` is also set - because unlike the other operator
flags it writes a known password.

**A fixture that cannot do the thing you are testing is worse than no
fixture**, and this one has failed that way five separate times. Each was
silent, and each cost a debugging session that started by suspecting the
feature:

- **It could not breed.** No Breeding Grounds, no
  `character_lineage_registry` rows, and no sexes. The roster endpoint SKIPS
  a character with no lineage row, so the Breeding screen's parent list came
  back empty and read as a loading state.
- **Every attribute was zero.** Setting `CurrentLevel` directly does not grow
  STR/DEX/CON/LCK - the game adds them per level GAINED - so a level-40
  account fought with a 148 HP health bar against the ~3,100 a real one
  carries, and died to a boss a genuine player beats. The same trap
  `ProgressionRateTests` documents in its own comment.
- **It stocked no wood or stone**, so no village upgrade priced in them could
  be paid for.
- **Its village pool was one villager per sex**, and every `exercise.mjs` run
  marries one - so the second run had nobody left and the pairing step failed
  for want of a partner rather than for a defect.
- **It held no CATALOGUED material**, 2026-09-01, which is the two-namespace
  trap again. It stocked the four gathering slugs and the village's three, and
  none of those have an `ItemDefinition`. The guild depot is keyed on
  `ItemDefinitionId`, so every material the account carried answered 400 on the
  Donate panel - undrivable on the account that exists for driving things by
  hand. It now also stocks `birch_log`, `golden_birch_log`, `malachite_ore`,
  `willow_log` and `hematite_ore`.

`DevFixtureInvariantTests` now asserts the first of those. Suspect the
fixture before the screen.

**Which namespace a material belongs to is the question to ask first.** Several
string spaces share one `CommodityRecords` table and are not interchangeable:
the gathering slugs (`raw_log`, `oak_log`, the village's wood/stone) have no
`items.json` entry and therefore no `ItemDefinition`; the catalogued materials
do; and a third `*_crafting_material` space exists alongside both. Anything
keyed on `ItemDefinitionId` silently rejects the first, and the web client's
`registry.itemsByBaseId.get()` returns undefined for it - which renders a stack
of 5,000 as "x0". Six defects in one day trace to this; see the 2026-09-01
handoff in `NEXT_STEPS_BACKLOG.md`.

**ORE is settled and is ONE space.** Exactly one ore per region, common and
rare, and every system uses the same names - mining pays them, the village
spends them, tool recipes consume them, guild buffs take them:
`copper_ore`/`malachite_ore`, `iron_ore`/`hematite_ore`,
`sulfur_ore`/`obsidian_ore`, `silver_ore`/`cobalt_ore`,
`darksteel_ore`/`astralite_ore`. The authority is
`VillageManagementEngine.TierMaterials`, matched by the gathering loot tables
(90/10 weights) and `GuildContributionEngine.BuffTierMaterials`.

**The stocked larder is load-bearing, not a convenience.** Auto-eat fires
the moment HP crosses the threshold, and an empty larder stops the activity
outright with `ActivityHaltReason.OutOfFood`. The first version of this
fixture omitted it and produced a character that halted about a minute into
its first fight, which defeats the purpose of an unattended-playtest
account.


## 19. The Long Game: deeds, Seals, the Hall, and the gene pool

Four systems that share one purpose - giving a season something it leaves
behind, when levels and gear are both wiped. The design is
`LONG_GAME_SPEC.md`; this is where the pieces live.

- **`DeedRegistry`** holds five chapters of six deeds, pure and static so
  every threshold is testable without a database. **`DeedProgressSource`**
  reads the twenty-odd numbers they ask about off the tables once, behind
  `/api/v1/deeds/snapshot`. **`SealEngine`** banks a Seal on that read -
  there is no claim command, deliberately, because a claim button is a thing
  to forget.
- **A Seal is +2 permanent skill points EVERY season.** The seasonal reset
  therefore sets `AvailableSkillPoints` to what the Seal mask pays rather
  than to zero; zeroing it would have paid each Seal exactly once.
- **`HallOfAncestorsRules`** decides who survives a rollover: ten slots, four
  more for diamonds, the player's marks first and the strongest blood after.
  The main character can never be culled - their id IS the account's
  `PlayerGuid`, so losing them breaks the account rather than the roster.
- **`BreedingEngine.ExecuteHeroVillagerBreedingAsync`** is the standard pair.
  A villager marries once and becomes an elder; the pairing is never inbred,
  because a newcomer has no parents here.
- **`AssignCharacterSlot`** is the only thing in this server that has ever
  written a `SlotIndex` after creation. Without it every child bred past the
  third slot was permanently unplayable.

## 20. What the client is told about a moment

The wire carries **no combat event of any kind** - there is no "you hit for
N" packet - so the client infers every hit from the difference between two
`CurrentMonsterHp` snapshots (`client_web/src/lib/stores/damage.ts`). That is
enough for a number and nothing else, which is why three small fields exist:

- `LastHitWasCrit` - set where the crit is rolled, cleared on every swing.
- `EquippedWeaponKind` - 0 melee, 1 ranged, 2 magic, resolved from the
  equipped weapon's base id so the client need not fetch an inventory to
  decide which effect to draw.
- `LastVictoryTick` / `LastDeathTick` - EDGES, never values, the contract
  `OfflineSummaryTick` established: incremented only when the thing really
  happened, never reset, so the client compares against its own last-seen
  value and shows each card exactly once.

`NetworkPacketLayoutGuard` pins the packet size and refuses to boot when it
moves. Every entry in its comment history is a field that had to justify its
bytes; add to that list rather than editing the number.

## 21. Drawing over artwork, and how the two stay married

The skill tree is the one screen where painted art and generated geometry
share a frame, and it has now failed that twice in ways worth writing down.

**It drew a second tree.** The overlay owned a trunk, a root system and five
brass limbs, laid on top of a Yggdrasil illustration that has all three. Two
trees in the same place, and the drawn one won on z-order. The trunk and roots
are gone: the art supplies them, and what is left overlaid is **only what
carries state** - a joint per limb, two buds per fork, a crown, and the
connectors between them.

**The art lives INSIDE the svg**, as an `<image>` at the viewBox's own aspect
ratio, not as a CSS background on the wrapper. That is the whole guarantee
that a node cannot land off a branch: one coordinate system, one aspect, no
container resize that moves one and not the other. `LIMB_ANCHORS` are
positions on the *painting's* branch crotches, so they are art-specific -
replacing the illustration means re-placing those five points, and nothing
else.

**Curvature comes from tangents, not from a bow.** Each connector was one
quadratic bowed by a fixed FRACTION of its run, so every limb was the same arc
at a different scale and the longest ones bent hardest. It is a cubic now: the
first control point leaves the trunk straight up, the second arrives along
that spread's own outward direction, and both offsets are **capped** - past
the cap a longer run adds length rather than bend.

**A zero-width bounding box eats an SVG filter.** The glow was declared with
the default `filterUnits="objectBoundingBox"` and percentage bounds. The
centre limb's connector is a perfectly vertical line, so its bounding box has
width zero - and 220% of zero is zero. The filter region collapsed and the lit
stroke was never painted, leaving only its unfiltered dark casing: the middle
branch rendered as a shadow while the other four glowed, which read as the
fifth having been *designed* differently. It is `userSpaceOnUse` pinned to the
viewBox now, where no element's own geometry can shrink it away. Any filter
that may land on an axis-aligned line needs this.

**Labels go PAST the fork, not among it.** A bud reaches at most 36 from its
joint and the crown sits 13 beyond the taken one, so any label offset under
about 50 lands on a node. It was 30, which merely *looked* fine on four limbs
because their spreads run diagonally and the text drifted sideways off the
buds. The centre limb's spread is straight up - which is exactly where its two
buds go - so its label sat on its own fork. One number for all five (52) plus
`PAD_TOP`, which extends the viewBox upward without scaling or cropping the
art, so the top limb has sky to put a label in. Without that headroom the only
way to place that one label was to special-case it, which is what caused the
complaint in the first place.

**A pale line needs a dark casing under it.** The centre limb runs straight up
the PAINTED TRUNK - the one part of the illustration that is bright, warm and
busy, and also the colour of the glow. It vanished completely: the node at the
top of it lit, and nothing appeared to connect it to anything. No single
stroke colour is safe over a painting, so every connector is drawn twice, a
dark casing first and the bright stroke on top. Same answer as the labels and
the unlit node halos.

**Brass is a surface colour.** It reads painted on a panel and vanishes over a
busy illustration. `--glow-warm`, `--glow-soft` and `--glow-hot` exist for
things that emit rather than reflect. The same asymmetry runs through the
nodes: an **unlit** joint's halo is DARK (it clears a patch of foliage for its
ring to sit against) and spending a point flips that circle to a glow. A pale
circle on painted leaves is invisible; a dark one is not.

### `npm run check:overlap`

Hit-tests every control on every screen at desktop and phone widths and
reports the ones a player would aim at and miss. Its first version compared
bounding boxes and reported 309 pairs, nearly all lies - a list with
`overflow: auto` gives its scrolled-out children real rects that land on
whatever is painted below the list. Asking `elementFromPoint`, and clipping
against scroll ancestors first, is what makes it honest.

Its standing finding is the floating chat handle covering real controls on
narrow screens. The dock reserves its own footprint at the end of a page, but
a long scrolling page always has something under a fixed corner; the durable
fix is moving chat into the nav, which is a UX decision rather than a layout
bug.
