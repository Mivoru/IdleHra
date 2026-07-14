# FolkIdle Current Implementation State

Status: living document, snapshot of the technical layout as of the most
recent hardening pass. Update this file whenever a structural change lands;
do not let it drift into aspirational/planned content - that belongs in
`NEXT_STEPS_BACKLOG.md`.

## 1. Solution Layout

- `server/FolkIdle.Server/` - the ASP.NET-hosted game server (single
  process, `Program.cs` entry point). `Engine/` currently holds 71 source
  files (all gameplay/simulation/background-worker logic); `Models/` holds
  57 source files (EF Core entities, DTOs, and `FolkIdleDbContext`). Both
  directories are flat (no domain sub-namespaces) - see Section 7 for the
  deliberate decision not to restructure this in the current pass.
- `server/FolkIdle.Server.Tests/` - xUnit integration test project, uses
  Testcontainers to spin up a real Postgres 16 instance per test collection
  (`PostgresTestFixture`).
- `client/` - Unity 6000.5.2f1 project. `Assets/Scripts/Network/` holds the
  binary packet layer and `WebSocketClient`; `Assets/Scripts/UI/` holds
  MonoBehaviour view/binder components; `Assets/Scripts/Engine/` holds
  client-side caches (`CodexInventoryCache`, `EquipmentInventoryCache`, etc).
- `docs/architecture/` - this documentation set (new as of this pass).

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
tick thread via one of the 24 `ConcurrentQueue<T>` members exposed on
`PlayerSessionRegistry` (`Engine/PlayerSessionRegistry.cs`) - e.g.
`MarketMatchQueue`, `ForgeUpgradeQueue`, `QuarantineNotificationQueue`,
`ChronoAccelerationQueue`. The tick loop drains these queues every frame
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
every DB-mutating engine method (see e.g. `ChronoCoreEngine.ConsumeChronoCoreAsync`,
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

Binary, fixed-layout packet structs shared in spirit (not in code) between
client and server: `ClientAuthPacket` (48 bytes), `ClientCommandPacket`
(384 bytes), `StateUpdatePacket` (654 bytes). Both sides validate their own
compiled struct size at startup against these constants
(`server/FolkIdle.Server/Network/NetworkPacketLayoutGuard.cs`, called from
`Program.cs`; no client-side equivalent existed prior to this pass - see
below).

`client/Assets/Scripts/Network/UnsafePacketParser.cs` deserializes inbound
`StateUpdatePacket`s via `Unsafe.ReadUnaligned`. As of this pass it exposes
`TryParseState(buffer, receivedCount, out packet)`, which validates both
`receivedCount` and `buffer.Length` against `Unsafe.SizeOf<StateUpdatePacket>()`
before touching the pointer, rejecting truncated/undersized buffers instead
of reading past them. `WebSocketClient.ParseAndEnqueuePacket` uses this and
drops (logs and returns on) a failed parse rather than propagating garbage
state.

## 8. Test Suite State

`FolkIdle.Server.Tests` currently has one long-standing, environment-specific
failure unrelated to any application code:
`E2EGameLoopTest.Test_E2E_ClosedLoopVerification` fails with a WebSocket
503 in sandboxed dev environments where the `HttpListener`-based WS
endpoint cannot bind/serve correctly; this is not reproduced in a normal
deployment and is treated as a known baseline exception. Any new test run
should be compared against "one known failure, N passing" rather than
expecting a fully green suite in this environment.

## 9. Known Dead Code (not yet removed)

- `Engine/SeasonEraEngine.cs` - an orphaned duplicate of the live
  `Engine/SeasonalRotationEngine.cs` with a different (stale) Legacy Shard
  formula. Not instantiated anywhere in `Program.cs`. Reactivation hazard
  if anyone wires it up by mistake, thinking it is the live path.
- `Engine/PlayerChronoRegistry.cs` plus a second, unused
  `ChronoBufferEngine.ProcessLoginHandshake` overload - zero callers.
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
