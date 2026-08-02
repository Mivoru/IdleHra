# FolkIdle Next Steps Backlog

Status: living document. Numbered items are independent units of work;
number order is priority order within a category, not a strict dependency
chain unless stated. Remove an item when it ships; do not renumber the
remaining items (a gap is fine and preserves historical references in
commit messages/PRs).

## Client UI Hook Points

### 1. Region-Completion Codex UI - ALREADY SHIPPED, item was stale

Resolved, and had been for some time before anyone noticed this entry was
out of date. `client/Assets/Scripts/Engine/CodexRegionsCache.cs` and
`client/Assets/Scripts/UI/UiCodexRegionsWindow.cs` both exist, and
`UiCombatLocationPanel` consumes the same cache. The description below,
which asserts "there is no client-side reference to `RegionCompletion`
anywhere under `client/Assets/Scripts/`", is simply no longer true.
Original description follows.



The server fully implements per-region completion tracking
(`PlayerRegionCompletions` table, `TickStatePayload.CompletedAreaFlags`,
`RegionCompletionNotification` queue drained every tick, and
`CachedCodexDamageMultiplier`/yield multiplier bonuses that already affect
live combat math - see `GAME_DESIGN_SPEC.md` Section 1.3). There is no
client-side reference to `RegionCompletion` anywhere under
`client/Assets/Scripts/` - not in `UiCommandDispatcher.cs`, not in any UI
binder. The existing Monster Codex UI stack
(`UI/UiCodexListBinder.cs`, `UI/UiCodex3DViewer.cs`, `UI/UiCodexBonusBinder.cs`,
`UI/UiCodexListRow.cs`, `UI/MonsterCodexEntryView.cs`,
`Engine/CodexInventoryCache.cs`) is the concrete pattern to follow for a
new region-completion view: a cache component that mirrors
`CompletedAreaFlags` from the inbound `StateUpdatePacket`, a list/grid
binder, and a bonus-summary binder analogous to `UiCodexBonusBinder`.

### 2. Market Order-Book Browser UI - SHIPPED, item retained for reference

Resolved. `UiMarketBrowserWindow`, `UiMarketDataBinder`, `UiMarketListingRow`,
`UiMarketBuyOrderPanel` and `UiMarketSellPanel` all exist and are constructed
by `MainSceneBuilder.BuildMarketBankWindow`. Original description follows.

`UI/UiCommandDispatcher.cs` already exposes `DispatchMarketListItem()` and
`DispatchMarketBuyItem()`, which send real `MarketListItem`/`MarketBuyItem`
packets - but both read their arguments from bare public fields
(`MarketTargetInstanceId`, `MarketListingPrice`) that nothing in the client
currently populates from user interaction. `UI/UiMarketDataBinder.cs` is a
read-only HUD (current gold, tax bracket, net-payout preview for a price the
player has already decided on) - it is not a listings browser. Needed: a
view that requests/displays active `MarketOrderRecords` for a chosen
`(BaseItemId, QualityTier)`, lets the player select a target order or a bag
item + price, and wires the selection into the dispatcher fields before
calling the existing `Dispatch*` methods. The server-side corridor and tax
logic (`GAME_DESIGN_SPEC.md` Section 3) does not need any changes to
support this - it is purely a client presentation gap.

## Architecture

### 3. Real horizontal-scaling design for SimulationEngine (do not do the literal "stateless PlayerSessionRegistry" ask)

A prior task asked to make `PlayerSessionRegistry` stateless via Redis to
unblock Kubernetes HPA. That specific class is not the blocker (see
`CURRENT_IMPLEMENTATION_STATE.md` Section 10) - the actual constraint is
that `SimulationEngine._activePlayers` holds every online player's full
live tick state in one process's memory, so a given player's session is
pinned to whichever pod accepted their WebSocket connection. Two real paths
forward, in order of implementation cost:

1. **Sticky routing / pod affinity**: keep the current in-memory
   architecture unchanged, add a Redis-backed `playerId -> podId` (or
   `playerId -> podAddress`) mapping written on connect, and route/proxy a
   reconnecting client back to the pod already holding their session (or
   reject and force a clean reconnect if that pod is gone). This does not
   allow arbitrary pod interception of an in-progress session but does
   allow HPA to add pods for new connections and drain old pods gracefully
   before termination.
2. **Full state externalization**: move `TickStatePayload` itself into
   Redis (or another shared store) with per-player distributed locking for
   the duration of a tick's mutation. This is a full rewrite of the tick
   loop's core execution model, touches every engine that currently takes
   a `ref TickStatePayload`, and should not be attempted without a
   dedicated design pass and load testing plan.

### 4. Market lock contention - partition before abandoning transactional integrity

A prior task asked to replace the order book's `Serializable` + `FOR UPDATE`
matching with Redis ZSETs and an async write-behind pipeline. Do not do
this as literally specified (see `CURRENT_IMPLEMENTATION_STATE.md`
Section 10 for why - it reverses this codebase's anti-double-spend
hardening for a real-money-adjacent subsystem). If matching throughput
becomes a measured, real bottleneck (not a hypothetical one), the lower-risk
next step is partitioning contention by `(BaseItemId, QualityTier)` - e.g.
per-partition advisory locks or per-partition worker affinity - so unrelated
items no longer serialize against each other, while keeping every
individual match inside a real ACID transaction. Only reach for
eventual-consistency/write-behind designs after partitioning is proven
insufficient, and only with an explicit reconciliation/crash-recovery plan.

### 5. Domain namespace reorg for Engine/ and Models/ (deferred, do incrementally)

`Engine/` (71 files) and `Models/` (57 files) are flat. A full mass
relocation into domain namespaces (`FolkIdle.Server.Core`,
`.Combat`, `.Economy`, `.Social`, `.Infrastructure`,
`.Utils.Cryptography`, plus a Models split between EF entities and
DTOs/seed routines) was requested and deferred this pass as too large a
diff for the value delivered right now. If picked back up, do it file group
by file group (e.g. move the market trio first: `MarketOrderBookEngine.cs`,
`MarketEscrowEngine.cs`, `CraftingEngine.cs` into `FolkIdle.Server.Economy`),
verifying `dotnet build` and the full test suite after each group, rather
than as one mass move.

## Cleanup

### 6. Dead engine duplicates - ENTIRELY STALE, do not act on this

All three claims below were re-checked and every one is wrong. Kept only so
nobody rediscovers the entry and "fixes" something that is either gone or
load-bearing.

- `Engine/SeasonEraEngine.cs` **does not exist**. It was deleted in commit
  `39d204c`; there is no file matching `*SeasonEra*` anywhere in `server/`.
- `PlayerChronoRegistry` is **not dead code and is not an engine**. It is
  `Models.PlayerChronoRegistry`, a live EF entity: all 22 references are
  migration snapshots of the DbContext model. Removing it would need a real
  migration, not a file deletion.
- `ChronoBufferEngine.ProcessLoginHandshake` has **exactly one overload and
  exactly one caller** (`StateCheckpointManager` line ~1022). There is no
  unused second overload.

The general lesson, which is worth more than the entry: a "delete this dead
code" item is only as good as the day it was written. Re-verify before acting.

### 6b. UpgradeTool - SHIPPED, item retained for reference

Resolved. The Village screen has a TOOLS section with an Upgrade Tools
button and a line naming the current tier and its gathering speed bonus,
which was previously invisible - the bonus is applied inside
`GatheringToolEngine`'s tick threshold with nothing on screen attributing
it. Note the server's `ExecuteUpgradeToolAsync` takes only a player id:
there is a single account-wide tool tier, not a tier per tool type.
Original description follows.



`CommandType.UpgradeTool = 21` is validated and implemented server-side, and
tool tier is a substantial gathering multiplier - `GatheringToolEngine`
grants +10% through +200% speed across its ten tool tiers, and the measured
pacing model assumes tier 0. The only sender,
`WebSocketClient.SendUpgradeCommandZeroAlloc`, is reachable exclusively from
`UiCommandDispatcher.DispatchUpgradeTool`, which nothing wires up. So no
player can ever upgrade a tool.

Do NOT delete the sender - it is the wiring for a real feature, not dead
code. It needs a button, most naturally on the Village or Workshop screen
next to the other infrastructure upgrades.

`SendPingCommandZeroAlloc` is the other unreferenced sender; that one is
network diagnostics and is plausibly meant to be manual-only.

### 7. ForgeSplicingEngine BaseItemId parse - SHIPPED, item retained for reference

Resolved. The method already resolved the same definition a few lines above
for the tier cap, so the affix roll now reuses that value instead of doing a
second lookup that could never succeed. Note the file has moved to
`Domain/Economy/ForgeSplicingEngine.cs`. Original description follows.



`Engine/ForgeSplicingEngine.cs` line ~165 does
`int.TryParse(targetItem.BaseItemId, out int baseId)`, but `BaseItemId` is
always a slug string (e.g. `gilded_sabatons_boots_armor_slot_base`), never
numeric, so this always fails and `regionTier` silently defaults to 1 for
every forge-fusion affix roll regardless of the item's actual region tier.
Fix: use `ContentRegistry.TryGetItemDefinitionByBaseId(targetItem.BaseItemId, out var definition)`
(added this pass for the market fallback-price feature, see
`GAME_DESIGN_SPEC.md` Section 3.1) to get the real `RegionTier` instead.

## Content and Balance

### 8. Region 3-5 balance - SHIPPED, item retained for reference

Resolved. The curve is now measured rather than merely reachable, and
`Test_Progression_EveryRegionClearsInsideThePlayableTimeBand` fails if any
region leaves the playable band. Three compounding defects were found:

1. Item base `FlatAttackPower` reached `StatsCalculator` from nowhere, so all
   five gear tiers were identical in combat.
2. The level curve grew `1.15^level` (16.4x per region) against 3x more player
   power per region. Level 100 was ~59 days of uninterrupted combat.
3. Region bosses sat at 17-29x their own region's strongest regular.

Modelled clear time per region, using weapon base power alone and ignoring
affixes/STR/set bonuses (a floor, not an estimate): 76 / 127 / 169 / 199 / 222
minutes, ~13.2 hours total. Gathering is a steady 9-11% of each region, so the
31 node thresholds and the 103 recipe costs were measured and deliberately left
unchanged. Original description follows.

Every recipe ingredient is now obtainable and every gathering node drops
something (`Test_ContentRegistry_EveryRecipeIngredientIsObtainableFromSomeSource`
and `Test_ActivityIdBands_EveryRekeyedNodeKeptItsLootTable` both pin this).
What has NOT been done is any balance pass over the numbers: node tick
thresholds, drop weights, and the 103 recipes' material costs were authored to
be reachable, not to be paced. Nobody has played a full progression curve end
to end, so the shape of the mid-game is unmeasured.

### 9. Set bonuses collapsing four armour slots - SHIPPED, item retained for reference

Resolved, and it was worse than this entry described. `SetBonusEngine` awards
its tiers by counting how many worn pieces share a SetId, and it was always
sized for seven slots (`MaxTrackedSlots` is 8). Its caller handed it three.
So a player in a full matching set produced a count of at most 3 and **no
4-piece bonus in the game was reachable by anyone, ever** - not a fidelity
loss, a whole tier of content that could not fire. Fixed by replacing the
weapon/armour/leggings triple with `EquippedSetIds` (all seven slots, one
value type, same bundling rationale as `EquippedAffixTotals`).
`Test_SetBonusEngine_FourMatchingArmourPiecesReachTheFourPieceTier` pins it.
This also folded in item 17. Original description follows.



`EquipmentSlotEngine.ComputeEquippedTotalsAsync` returns a weapon/armour/
leggings SetId triple because that is what `SetBonusEngine.Evaluate` consumes.
With six equip slots, the four armour pieces all fold onto the single armour
set id, taking the first one found. Widening set bonuses to six slots is a
balance change rather than a refactor and was deliberately left out of the
equipment pass.

### 10. Helper/offhand slot - SHIPPED, item retained for reference

Resolved. `EquipmentSlotEngine.SlotOffhand` (index 6, `SlotCount` 7),
`CharacterRecord.EquippedOffhandId` plus migration
`20260731182136_AddCharacterOffhandSlot`, the `StateUpdatePacket` field, and the
client slot row. Note the estimate below understated it: the change touched 13
mirror sites, including `SeasonalRotationEngine`'s era wipe - missing that one
would have re-opened the cross-player equipped-id leak for offhand items.
Original description follows.

`AffixRegistry.EquipmentSlotMask` includes `Shield`, and
`AffixRegistry.ResolveSlot` matches the `_helper_offhand_` BaseId marker, so
helper items already roll slot-correct affixes. There is no seventh equip
slot, so they cannot be worn - the same shape as the helmet/gloves/boots gap
that the six-slot pass closed. Adding it is one entry in
`EquipmentSlotEngine`'s slot constants plus one column.

## Client UI Hook Points (continued)

### 11. Per-character loadouts for slots 2 and 3 - SHIPPED, item retained for reference

Resolved. The blocker was not the UI: `/api/v1/player/inventory` reported an
account-wide `IsEquipped` flag, which can say an item is worn but never by
WHICH character, so the Roster had no way to attribute gear even though it
had the data in front of it. The snapshot now carries
`EquippedByCharacterSlot` (-1 when carried) and `UiRosterPanel` renders a
"Gear (n/7): ..." line per slot. Original description follows.



The wire carries the ACTIVE character's six equipment slots only. Gear changes
on a button press rather than at 10Hz, so the other characters' loadouts are
deliberately left to `/api/v1/player/inventory` rather than costing 96 bytes a
frame. The Roster screen currently shows each character's activity and status
but not what they are wearing; wiring the REST snapshot into a per-character
equipment view is the remaining piece.

### 12. Race unlock feedback - SHIPPED, item retained for reference

Resolved. `UiRaceUnlockToast`, fed by `StateUpdatePacket.UnlockedRaceBitmask`.
Carried as a monotonic ownership mask rather than a one-shot event so the
announcement survives a reconnect and cannot fire twice; the first mask seen in
a session is a baseline, never an announcement. Original description follows.

`PlayerRaceUnlocks` is written and a male/female pair is granted on a region
boss's first kill, but nothing tells the player it happened - no toast, no
entry on the Roster or Race Mastery screens. The unlock is currently only
visible as two new characters appearing in the roster.

## Tooling

### 13. Unity CI - licence RESOLVED, release build needs one variable

`UNITY_LICENSE` is configured and the Unity jobs run. As of 2026-08-01 the
licence check passes and **both the EditMode and PlayMode suites are green** -
client-side verification is no longer manual-only.

The jobs now run under a GitHub Environment named `Unity`, so secrets and
variables must be defined there rather than at repository level.

**Still outstanding: the Android release build.** It fails because no
`FOLKIDLE_CDN_BASE_URL` variable is set. `BuildPipelineController` creates the
`Production` Addressables profile from it on first run and fails loudly when
it is absent, deliberately refusing to default it - Production content built
against a placeholder URL would ship and then fail to load, which is the exact
failure the surrounding code exists to prevent. Add
`FOLKIDLE_CDN_BASE_URL` to the `Unity` environment, set to the CDN root that
will serve the remote catalog and bundles.

Note this is the FIRST time the release build has ever been exercised - it
previously required an Addressables profile that only existed in a
developer's local settings asset, so it could never have passed on a clean
checkout. Expect further genuine failures on the first successful run past
the profile stage; nothing downstream of it has been proven yet.

### 15. No audio clips exist

The audio trigger layer is built and wired (`GameAudioDirector`,
`GameAudioEventRelay`, `UiButtonClickSfx` on every button, plus combat, loot,
crafting, level-up, race-unlock and error triggers), but
`client/Assets/Resources/Audio/` is empty, so the game is silent. This is
deliberate and safe - a missing clip resolves to null, `Play` returns
immediately, and nothing logs - and it is verified: all ten effects fire with
zero clips present and no exception. Dropping correctly named files into that
folder starts them playing with no code change and no scene rebuild. See that
folder's README for the names and their trigger sites. Nothing registers a
music track either, so `AmbientAudioEngine`'s crossfade has no bed to fade.

### 16. Test_MarketEscrow_ConcurrentListings - SHIPPED, item retained for reference

Resolved by fixing the engine rather than the test, exactly as this entry
proposed: `MarketEscrowEngine.ListItemAsync` now runs under a retrying
execution strategy built on `RetryingDbContextOptions`, with the command
result and log line hoisted out of the retried delegate into a
`ListAttemptOutcome` so a retry cannot enqueue a duplicate result. Original
description follows.



`Test_MarketEscrow_ConcurrentListings_ExactReplicaNoSerializationDrift` fires
six concurrent `Serializable` listings for one player and asserts all six
commit. Under full-suite load against the shared Postgres container, five
routinely lose the serialization race and `ListItemAsync` returns false after
catching the transient failure rather than retrying; in isolation it passes
every time. Confirmed pre-existing by stashing an unrelated working tree and
reproducing the identical failure on the untouched baseline - do not chase it
as a regression. The real fix is to wrap `MarketEscrowEngine.ListItemAsync` in
a retrying execution strategy the way the equip path already is (commit
`7a95764`), rather than weakening the test.

### 17. Set bonuses and the offhand slot - SHIPPED, folded into item 9

Resolved together with item 9: `EquippedSetIds` carries all seven slots, so
the offhand now contributes its set id alongside its base stats and affixes.

### 18. Client server address is now configurable - SHIPPED, new item for the record

Twenty-five classes each declared their own
`ServerBaseUrl = "http://localhost:8080"` and **nothing anywhere ever assigned
a different value to any of them**. Only `UiLoginWindow`'s copy affected
authentication and the WebSocket handshake, so a build could authenticate
against a real server and then have all twenty-two HTTP caches - inventory,
market, guild roster, mailbox, leaderboard, codex - silently query localhost
and come back empty. In practice the client only ever worked on the machine
running the server, which made every non-shipped item above untestable
anywhere else.

Now one `ClientServerConfig.BaseUrl`, resolved from `FOLKIDLE_SERVER_URL`,
then a saved preference, then the localhost default, with `UiLoginWindow` as
the sole writer.

### 19. 4-piece set tier - FULLY SHIPPED

All five 4-piece effects are now consumed by the live combat tick.

The fifth, `CcImmunityActive`, was replaced rather than implemented. It could
never fire: this game models no player-facing crowd control - Vulnerable,
Chilled and Burning are all applied BY the player TO the monster - so there
was nothing to be immune to, and building a CC system to justify one set
bonus would have been the tail wagging the dog.

It is now `DamageCapActive`: any single incoming hit is capped at 20 percent
of effective max HP. Same tank/mitigation archetype, and it answers the
failure mode this game actually has. Region bosses sit at roughly 2.5x the
attack power of their region's regular monsters, and the auto-eat larder can
only respond BETWEEN hits, never during one - so a single large hit is
unsurvivable in a way that the same total damage spread over several hits is
not. At 20 percent a wearer always survives at least five consecutive
maximum hits from full, which is exactly the window auto-eat needs.

Applied after armour and block so it is a true ceiling rather than another
mitigation term, and before the HP subtraction so the set's own thorns
reflects the CAPPED figure - the set cannot convert its defence into extra
offence. `Test_SetBonus_DamageCapLimitsASingleHitToAShareOfMaxHp` pins the
arithmetic, not merely the flag. Verified the progression pacing band is
unchanged. Original description follows.



Resolved for four of the five effects, all now consumed by the live combat
tick: `FireDamageMultiplierPct` and `BurnApplicationActive` in the outgoing
damage step, `ThornsReflectionActive` in the incoming one, and
`CooldownReductionActive` at the skill-cast site. Burn is a deterministic
fraction of the hit that applied it rather than a timed DoT - this combat
loop has no per-target effect timers, and adding a scheduler for one effect
would be a far larger change than the effect is worth.

**`SetCcImmunityActive` remains deliberately unconsumed.** This game models
no player-facing crowd control at all: the only status effects that exist
(Vulnerable, Chilled, and the new Burning) are applied BY the player TO the
monster, so there is nothing to be immune to. Implementing it would mean
inventing a CC system, which is a design decision rather than a wiring fix.
Either add player-facing CC and connect it, or give that slot in the Eternal
Dreadnought 4-piece an effect that does something. Original description
follows.



`SetBonusEngine` produces five 4-piece effects and **not one is consumed by
anything**: `ThornsReflectionActive`, `CooldownReductionActive`,
`BurnApplicationActive`, `CcImmunityActive` and `FireDamageMultiplierPct`
are copied onto `CombatStats` by `StatsCalculator` (lines ~274-277) and
read by zero call sites in the entire server.

This was harmless while the 4-piece tier was unreachable. **Item 9 made it
reachable**, so a player can now assemble four matching pieces, be told
they have a set bonus, and receive only the 2-piece flat stat. Both
authored sets are affected: Chiming Steel's 4-piece is Fire damage + Burn
(both inert) and Eternal Dreadnought's is Thorns + CC immunity + cooldown
reduction (all three inert).

Either implement them in the combat tick or stop advertising them. Do not
leave a tier that visibly qualifies and silently pays nothing - that is
worse than not having it.

### 20. Luck and Constitution bonuses - SHIPPED, item retained for reference

Resolved. `ForgeSuccessPct` is now added to the fusion roll in
`ForgeSplicingEngine`, clamped at 95 percent so enough Luck can improve the
odds without turning the forge into a guaranteed upgrade and removing the
tier sink. `OutOfCombatHpRegen` is applied by an idle-only regen tick, gated
on `ActiveActivityId == 0` on purpose: regenerating mid-fight would undercut
the auto-eat larder, which is the intended sustain mechanic and the thing
every halt reason is built around. Original description follows.



`StatsCalculator` documents Luck as granting "+0.05% Forge Success" and
Constitution as granting "+0.1 Out-of-Combat HP Regen/sec", computes both
into `ForgeSuccessPct` and `OutOfCombatHpRegen`, and **nothing anywhere
reads either field**. The forge's success roll does not consult
`ForgeSuccessPct`, and no regen tick exists.

So a player investing in Luck for forge safety, or Constitution for
regeneration, gets nothing for it. Same class of defect as item 19 and as
the item-base-power bug: the value is computed correctly and thrown away.

### 21. Broadcast dirty-checking - SHIPPED, but NOT as this entry proposed

Resolved - and the approach suggested below would have been a real bug.

This entry proposed gating on `TickStatePayload.IsDirty` and clearing it
after dispatch. `IsDirty` is owned by `StateCheckpointManager`, which uses
it to decide whether to persist to Postgres/Redis and resets it when it
does. Consuming it in the broadcast would have silently skipped saves -
trading data loss for bandwidth.

Instead each packet is compared against the last one actually sent to that
player, excluding `TicksSinceLastFlush` (which increments every tick, so
including it would make every packet differ and the check would save
nothing). Cache entries are dropped through `RemoveActivePlayer`, the
existing single choke point for session cleanup.

Verified live: an idle session receives no packets at all, while a session
in combat receives one per tick.
`Test_Broadcast_SuppressesIdenticalPacketsButStillKeepalives` pins both
halves, including the 10-tick keepalive - which is the half that cannot be
observed from the client and would starve interpolation if it silently
stopped firing. Original description follows.



`SimulationEngine`'s broadcast loop iterates all of `_activePlayers` and
calls `SendToPlayer` unconditionally - there is no check against
`TickStatePayload.IsDirty`, even though that flag exists, is maintained
throughout the tick, and is exactly the signal needed.

Cost: 695 bytes x 10 Hz = **~7 KB/s per connected player, whether or not
anything changed**. About 55 Mbps sustained at 1,000 concurrent players and
556 Mbps at 10,000 - for a game where an idle player's state is identical
frame to frame.

This is the single largest optimisation available. The obvious shape is to
send on dirty, plus a forced keepalive every N ticks so the client's
interpolation and save-trust indicator never starve. Note the client
interpolates between two snapshots (`VisualSyncProxy`), so the keepalive
interval has to stay short enough not to make motion stutter - measure
before picking it.

### 22. Hot-table indexes - SHIPPED, item retained for reference

Resolved by migration `AddHotTableCompositeIndexes`. Verified against a real
database that the planner now uses `IX_CommodityRecords_PlayerId_ItemId`
with BOTH columns as the index condition for the gold lookup, rather than
scanning the whole `ItemId` index. The pre-existing single-column indexes
were kept: they still serve the market's cross-player searches, which
genuinely do lead with the item. Original description follows.



`FolkIdleDbContext` adds exactly three indexes for this family, and each is
on the low-selectivity column rather than the one every query filters by:

| Table | Indexed on | Actually queried by |
|---|---|---|
| `CommodityRecords` | `ItemId` | `PlayerId` + `ItemId` |
| `EquipmentInstances` | `BaseItemId` | `PlayerId` |
| `MarketOrderRecords` | `BaseItemId` | `BaseItemId` + `QualityTier` + `Status` |
| `CharacterRecords` | (nothing) | `PlayerId` |

`CommodityRecords` is the worst case: the index is on `ItemId`, and
`ItemId = "gold"` matches **one row per player in the game**. Reading a
single player's gold balance - which happens on every login, every kill
reward and every purchase - scans that entire index. `CharacterRecords` has
no secondary index at all and is read on every login, equip and inventory
snapshot.

Fix: composite `(PlayerId, ItemId)` on `CommodityRecords`, `(PlayerId)` on
`EquipmentInstances` and `CharacterRecords`, and
`(BaseItemId, QualityTier, Status)` on `MarketOrderRecords`. Cheap, one
migration, no behaviour change.

Note this only affects tables using a conventional `Id` primary key. The
many tables with a composite `(PlayerId, X)` key - codex entries, race
masteries, region completions, village infrastructure, quests - are already
covered by their primary key index.

### 23. EvictVillager - SHIPPED, item retained for reference

Resolved, and the real blocker was not the missing button. The client was
never told WHICH village slots are occupied - the wire carries a population
count and nothing else - so there was no way to name a target even with a
button present. The player statistics snapshot now carries the villager
slots, and the Village screen renders a roster with a per-villager Evict
that sends the resident's real `SlotIndex` rather than its row position
(slots go sparse after an eviction, so sending the row would evict the wrong
resident). Original description follows.



`CommandType.EvictVillager` is validated and implemented server-side and
has no client reference outside the dead `UiCommandDispatcher`. Same shape
as item 6b (`UpgradeTool`), smaller stakes. Do not delete the sender.

### 24. OfflineStateEngine - SHIPPED (deleted), with one correction

Deleted. But the entry below was not quite right, and the correction is the
useful part: it had zero PRODUCTION references, not zero references. One
integration test instantiated it directly. The first sweep missed that
because it was scoped to `server/FolkIdle.Server/` and did not include the
test project.

Deleting the test along with the engine would have silently dropped the only
guard on a rule that is still live: backpack capacity is
`SimulationEngine.DefaultBackpackCapacity` plus the Human vault mastery
bonus, which `StateCheckpointManager` uses for real and which had no direct
test of its own. The test was therefore retargeted at the live formula
rather than deleted -
`Test_RaceMastery_BackpackCapacityUsesHumanVaultBonusNotAHardcodedValue`.

Reinforces item 6's lesson from the other direction: verify the scope of a
"no references" claim, not just its result. Original description follows.



Zero references anywhere, including `Program.cs` - unlike the phantom
entries in item 6, this one was verified to exist and to be unreferenced.
`OfflineSimulationEngine` is the live offline path. Safe to delete after a
final check.

### 14. Play Mode harness needs a seeded fixture account - SHIPPED, item retained for reference

Resolved. `--seed-dev` provisions a repeatable account (three characters, all
seven equip slots filled, Town Hall 5, materials, gold) and is double-guarded:
the flag alone is not enough, `FOLKIDLE_ALLOW_DEV_SEED=1` must also be set,
because unlike the other operator flags this one writes a known password. See
`DevFixtureSeeder`. Original description follows.



Verifying multi-character, equipment and progression behaviour in Play Mode
currently requires hand-seeding the database (Town Hall level, roster,
equipment) with a throwaway console app. A committed, idempotent dev-seed
entry point - alongside `--migrate` and `--lift-quarantine` - would make the
audit repeatable instead of improvised. Note it must stay clearly
non-production, guarded the way `--lift-quarantine` is.

## Audit Findings, 2026-08-01

Logged from a full sweep across server, tests and client. Grouped because
they share a cause: the wire and the command surface both grew faster than
the UI that was meant to consume them.

### 25. Thirty-two VisualSyncProxy properties have no reader

The client mirrors 32 wire values into `VisualSyncProxy` properties that
nothing in `client/Assets/Scripts/` reads. Most are harmless mirroring, but
several are live server features with no player-facing surface at all. The
two worth doing first:

- **`VisualInventorySpaceRemaining` / `VisualInventoryCapacity`.** The
  backpack "13/20" readout. `InventoryCapacity` was added to the wire
  specifically so this could exist - see the inventory census work - and the
  display was never built. A player has no way to see how full their
  backpack is until loot starts being silently discarded.
- **`VisualGatheringProgress` / `VisualProgressTicks`.** There is no
  gathering progress indicator anywhere in the client. In an idle game whose
  gathering loop is 9-11 percent of total playtime, the player watches
  nothing happen.

Also unread, lower priority: `VisualMentorCount` (the Academy XP bonus,
previously fixed for being "invisible client-side" and still not shown),
`VisualSlot1/2/3AgePhase` and `VisualChildMaturationMs` (character aging and
breeding maturation), `VisualMaxMana`, `VisualGlobalEventId`, and the three
village population fields.

Not every one of the 32 needs UI - some are genuinely internal. The point of
the entry is that nobody has ever gone through the list and decided.

### 26. TotalItemsCraftedCount is never assigned - SHIPPED, and the audit was wrong about it

This entry proposed "assign it or delete it and reclaim four bytes." Deleting
it would have been a silent regression: `UiTutorialController` reads
`VisualTotalItemsCraftedCount` and detects a completed craft purely from that
value rising. The field was not unused - it had a consumer that could never
fire. The removal compiled cleanly on the first attempt and was only caught
by grepping for readers before trusting the audit.

Shipped as the assign branch. `TickStatePayload.LifetimeItemsCrafted`
hydrates at login from `PlayerRecords."TotalItemsCrafted"`, the tick thread
increments it as `CraftingCompletionQueue` drains, and the packet clamps it
into its uint.

Deliberately NOT written back by `StateCheckpointManager`, unlike
`LifetimeDeaths` directly above it in the payload. `CraftingEngine` persists
the column inside the same transaction as the item grant, making it the
single author; a checkpoint flushing an absolute snapshot on top would
clobber any craft committed between hydration and flush.

Lesson worth keeping: "no server code writes it" and "nothing reads it" are
different claims. This audit checked the first and assumed the second.

### 27. Five commands remain unreachable from any UI

Implemented and validated server-side with no client path:
`ConsumeChronoCore`, `SubmitShardAttack`, `RegisterWorldBossDamage`,
`InitiateNodeMigration`, `PingNetworkDiagnostics`.

Two are real player features. `ConsumeChronoCore` is **not** covered by
`UiChronoBankPanel`, which sends `ActivateChronoBoost` and
`ConsumeTimeWarpCore` - different commands. `SubmitShardAttack` is mentioned
only in a comment in `UiGuildWarPanel`. The last two are plausibly
ops/diagnostics and may be fine to retire formally rather than wire.

When checking this yourself: `SendWorldBossAttackCommandZeroAlloc` looks
unreachable to grep and is NOT - it is wired through
`UnityEventTools.AddPersistentListener` in `MainSceneBuilder`, which no text
search can see. Always check the builder before believing a sender is dead.

### 28. CombatStats.SetCooldownReductionActive is redundant - SHIPPED (removed)

The cooldown-reduction effect reads its flag straight off
`SetBonusEngine.Evaluate(...)` at the skill-cast site, so the mirrored
`CombatStats` property has no consumer. The effect works; the property is
dead weight that invites someone to "fix" it by wiring a second path.
Delete the property or switch the cast site to read it - one or the other,
not both.

### 29. AssignMentor carries a slot index in the LimitPrice field

`CommandType.AssignMentor` reads its mentor slot index out of
`cmd.LimitPrice`, a market price field. It works, and it is not urgent - but
it is the same shape as the numeric-id-as-identity bugs that have bitten
this codebase repeatedly. Give it a named field if that packet is touched
for any other reason.

### 30. Stale TODO in the AssignMentor command branch - SHIPPED (deleted)

`SimulationEngine.cs` around line 1872 carries a TODO asking whether a
validator check is needed. `ClientCommandValidator.ValidateMentorshipAssignment`
is called on the very next line. One line to delete. Noted only because it
is the single TODO marker in the entire codebase and reads as a gap when it
is not.

### 31. Character stat rows render bare numbers with no labels - SHIPPED

`UiCharacterStatsPanel` writes only the integer into each row's char buffer -
`WriteIntToBuffer(_strBuffer, 0, str)` with no "STR: " prefix - so the
top-left HUD shows eight unlabelled values reading `0 / 0 / 0 / 0 / 0 / 0 /
0.0% / 0`. The placeholder text passed by the scene builder ("STR: 0") is
overwritten on the first refresh.

Confirmed in a Play Mode screenshot. Pre-existing, unrelated to the activity
status work that sits directly below it in the same panel - and invisible to
any structural check, since every row exists and is correctly wired.

Fix is the same shape as `UiActivityStatusPanel.RefreshBackpack`: write the
label into the buffer before the number.

### 32. Art history is still 472 MB of plain git blobs

`.gitattributes` now routes `*.png` and the other art formats through LFS,
but forward-only. The 127 PNGs already committed - 472 MB across three
commits (`a859802`, `885d87f`, `5d7ee3b`) - remain ordinary blobs, so every
clone still pays for them.

Migrating them is `git lfs migrate import --include="*.png" --everything`,
which rewrites 76 commits and requires a force-push. That part is mechanical.
The reason it is deferred is quota, not difficulty:

- GitHub Free allows 1 GB LFS storage and 1 GB/month LFS bandwidth.
- Migrating puts roughly 472 MB into storage, plus 124 MB of art currently
  untracked on disk, for about 596 MB - already 60 percent of the storage
  allowance with no room for future revisions of the same files.
- `unity_client.yml` checks out with `lfs: true` in TWO jobs. One CI run
  would therefore pull about 1.19 GB and exhaust the entire monthly
  bandwidth allowance on its own.
- LFS overage blocks pushes, not just fetches. The failure mode is the CI
  that was unblocked in `6af382f` breaking again, plus an inability to push
  until the next billing cycle or a paid data pack.

So this is a billing decision before it is an engineering one. Three viable
routes:

1. Buy a data pack (50 GB storage + 50 GB bandwidth). Makes the migration
   safe as specified and is the least invasive to the workflow.
2. Migrate, but drop `lfs: true` from CI and have the Unity jobs build
   against placeholder art. Free, but the build stops covering the real
   asset pipeline, which is exactly what the Android build failure was about.
3. Move art out of git entirely and serve it from the CDN already configured
   via `FOLKIDLE_CDN_BASE_URL`, keeping only import settings in the repo.
   Best long-term, largest change, and it interacts with how the client
   loads sprites today.

Until one is chosen, the forward-only `.gitattributes` is the correct state:
it stops the growth without spending quota.

## Audit Findings, 2026-08-01 (second pass)

### 33. Monster milli-HP overflowed int - SHIPPED, and it was a self-inflicted regression

`CurrentMonsterHp` held milli-HP in an `int`, capping monster HP at
2,147,483. The pacing rebalance set region bosses to
3500/14000/82000/440000/**3000000** without checking that ceiling, so
Malakor wrapped to -1,294,967,296, satisfied the `CurrentMonsterHp <= 0`
death check on spawn, and paid a full kill reward every tick: 6,000,000 XP
and 1,500,000 gold per second from ordinary progression content.

41 of 115 monsters were affected. The mirror defect on the damage side made
the four strongest monsters deal exactly 1 HP per hit.

Fixed by widening to `long` and making the endgame scaling cast saturate.
The scaling fix matters independently of the data: the multiplier compounds
at 1.25 per tier without bound, so wrapping was guaranteed eventually.

The lesson is not "check for overflow." It is that a balance change and a
representation limit lived in two files that nobody cross-checked, and
neither the test suite nor a playtest could reach the region-5 boss to
notice. The guard that now prevents recurrence is the type system - the
`int` revert fails compilation in 10 places - not a test.

### 34. Gathering mastery was never persisted - SHIPPED

Woodcutting/mining mastery was earned by three code paths, consumed for
gathering yield, and carried on the wire, but had no database column, no
hydration, and no UI. Every logout reset both professions to zero, and
nothing on screen could reveal it. Now has columns, hydration, write-back,
proxy mirrors for the levels, and `UiGatheringMasteryPanel`.

### 35. Corrections to the first audit pass

Three findings from the earlier report did not survive verification. Logged
because the wrong methods are worth remembering:

- **"155 dead buttons"** - false. Buttons are wired by serialized field
  reference and runtime `AddListener`, not `AddPersistentListener` (6 vs 56
  files). Cross-checking every button against BOTH mechanisms gives **0
  unwired of 190**. A persistent-listener scan alone is meaningless here.
- **"Command rejections are never surfaced"** - false. `VisualLastCommandResultCode`
  has no reader by design; `VisualSyncProxy` documents that UI must subscribe
  to `OnCommandResultReceived`, which `UiCommandResultToast` does correctly.
  An unread property is not automatically an unwired feature.
- **"UiChatWindow leaks an event subscription"** - false.
  `HandleRowPrefabLoaded` fires exactly once and pools rows for the window's
  lifetime, so the subscription never accumulates.

Also corrected: `AccumulatedWood/Stone/Iron` are sub-1.0 fractional carries,
so their absence from login hydration is by design, not lost state.

Standing methodological note: for this codebase, "X has no reference" is only
a finding once it has been checked against every wiring mechanism the
codebase actually uses. Three of the four false positives above came from
checking exactly one.

### 36. Remaining known gaps

- 27 `VisualSyncProxy` members still have no reader. `VisualMiningXp`/
  `VisualWoodcuttingXp` are now consumed; the rest need triage into "wire a
  UI" or "delete", individually rather than as a batch.
- 8 scene texts still overflow their rect. All come from creators other than
  `CreateHelpText`, which now auto-sizes.
- The art history migration to LFS remains open - see item 32.

### 27b. Unreachable-command triage - RESOLVED, and it surfaced a live server stall

Worked through the five commands in item 27 plus `ConsumeConsumableAsset`.
The triage mattered less than what it uncovered.

**Live defect found: `RegisterGuildDefense` blocked the tick thread.**
`SimulationEngine` ran `RegisterGuildDefenseAsync(...).GetAwaiter().GetResult()`
inline in the 10 Hz loop - a Serializable transaction taking two `FOR UPDATE`
row locks, executed synchronously, for every player. `UiGuildWarPanel` sends
it from a button, so any player could stall the entire simulation for as long
as those locks took, and blocking the tick thread while EF holds locks is a
deadlock shape as well as a latency one. Converted to `SafeDispatchAsync`.

**`SubmitShardAttack` (50) has the same shape and is NOT fixed.** It writes
its result back into `currentPayload`, so it needs the notification-queue
pattern rather than a straight `SafeDispatchAsync`. It is unreachable, which
is the only reason it has never stalled production. The call site now carries
a DO-NOT-WIRE warning. Restructuring it belongs with item 3.

**`RegisterWorldBossDamage` (19) - RETIRED.** A second entry point into the
same `WorldBossEngine.QueueAttack` that `AttackWorldBoss` already reaches,
but with weaker validation: it took the damage figure straight from
`cmd.TargetId` and merely clamped it, where `AttackWorldBoss` validates the
boss instance id, that the event is live, and that the boss is not dead. No
client path sent it. Removed the handler, `WorldBossEngine.RegisterDamage`,
`ValidateWorldBossRegistration`, and the client sender.

**`ConsumeChronoCore` (24) - cannot be wired; it has no content.** The
handler consumes a `CommodityRecords` row and grants 4 hours of banked chrono
time, but no Chrono Core item exists in the 379-entry catalogue, so every
send would fail the `core == null` check. This is a content gap, not a wiring
gap. The dispatcher method is retained with that explanation.

**`InitiateNodeMigration` (44) and `PingNetworkDiagnostics` (52) - client
halves removed.** Migration is server-orchestrated (item 3); the ping handler
echoes a token into `StateUpdatePacket.NetworkDiagnosticsToken` that no client
code reads, so the round trip measured nothing. Server handlers retained for
ops use. Fully retiring 52 would additionally reclaim 4 wire bytes and remove
an unconsumed field - worth doing next time the packet is touched.

**`ConsumeConsumableAsset` (45) was never unreachable.** The earlier audit
called it "a landmine, not a live outage" on the strength of
`DispatchConsumeConsumableAsset` having no builder binding. That was wrong:
`UiCombatLocationPanel` sends opcode 45 directly from `UseFoodButton` and
`UsePotionButton`. Combined with the broken SQL in that handler, **every food
or potion use force-disconnected the player**. Both are now fixed, but the
severity call was wrong for the same reason item 35 documents - one wiring
mechanism checked out of several.

## Affix Rarity, Reroll and Social Layer, 2026-08-01

### 37. Affix rarity system - SHIPPED

A second rarity axis, deliberately smaller than the GDD's 14 item tiers.
Items keep those tiers and keep deciding affix COUNT (GDD 5.2's 1/2/3/4/5,
cap 5, unchanged); affixes gained their own Common..Legendary scale deciding
MAGNITUDE at `floor(base * region * 1.6^(rarity-1))`.

Region keeps the growth term it always had, so progression through the five
regions still drives raw power on its own. Legendary is 6.55x Common. Rolled
values vary +/-20% around that centre, deliberately narrower than one rarity
step so a lucky Common can never beat an unlucky Uncommon - rarity stays
strictly dominant over luck, which is what keeps the Diamond upgrade
worth buying. A test asserts that ordering directly.

Affix count is NOT redefined in AffixRegistry. `RarityTier.GetAffixCount`
already implements GDD 5.2 and every drop path calls it.

Payload keys became `id`, `id@rarity`, `id#stack@rarity`. Both
`AffixRegistry.StripStackSuffix` and `ClientAffixRegistry.StripStackSuffix`
strip the marker; a key either side failed to strip would resolve to no
definition and contribute silently nothing.

### 38. Reroll economy and auto-reroll - SHIPPED

Three operations, two currencies. Value and stat rerolls cost GOLD, escalating
1.35x per consecutive attempt and saturating at a documented ceiling; only a
rarity upgrade costs Diamonds. Auto-reroll burns attempts in bulk, so pricing
it in premium currency would have made the headline convenience feature a
pay-to-win treadmill - and gold needed an endgame sink.

Auto-reroll checks reachability BEFORE spending. Targeting a shield-only
affix on a sword, or asking a value reroll to raise rarity, are rejected up
front rather than discovered by burning the budget. Rarity is a floor, not an
equality, so "stop at Epic" is satisfied by a Legendary. The logic is a pure
evaluator in `AutoRerollPlanner` - no database, no async - so it is testable
without Testcontainers.

Operation and stop condition travel as NAMED packet fields (352 -> 359),
deliberately not smuggled through `LimitPrice` - see item 29.

### 39. Announcements, congratulate button, generated audio - SHIPPED

Epic and above announce to global chat on channel type 3, at the same
threshold `UiRarityPalette` uses for glow so the two cannot disagree.
Enqueued only AFTER the transaction commits and cleared on rollback: the
queue drains on another thread, and nothing can retract a chat line.

The congratulate button sends through the ordinary chat path, inheriting the
server's rate limiting, mute and profanity handling. A dedicated command
would have bypassed all three.

All ten SFX are synthesised from code by `ProceduralSfxGenerator` -
oscillators, filtered noise and ADSR into 16-bit PCM. **Item 15 is closed.**
216 KB total, deterministically seeded so regeneration is byte-identical.
These are placeholders, not authored audio.

### 40. Still open after this work

- **Legendary voice line.** Not possible from here - the synthesiser produces
  tones and noise, not speech. Needs a recording or an external TTS.
- **Font restyle.** TMP needs a font asset built from a TTF/OTF. Sizes,
  weights and colour can be restyled against a supplied face; choosing or
  authoring the typeface cannot be done from code.
- **`ConsumeChronoCore` still has no item** - see item 27b. Unchanged.
- **LFS history migration** - item 32. The audio is the first content actually
  routed through LFS (206 KB), which validates the `.gitattributes` but does
  not change the quota maths for the 472 MB of art history.

### 41. Targeted sweep: dropped deltas and split currency stores, 2026-08-01

Run after three bugs of the same family turned up in a row. Two invariants
were swept exhaustively rather than subsystem by subsystem.

**Invariant 1 - a notification dropped on the tick thread must not destroy
value.** All 33 queue drains in `SimulationEngine` were classified. The
distinguishing property is SNAPSHOT versus DELTA:

- A notification carrying a snapshot (`LegacyStoreUpdate` new balance,
  `BillingSync` balance, `InfrastructureUpdate` building levels) is SAFE to
  drop. The database already holds the value and login re-hydrates it; only a
  live display refresh is lost.
- A notification carrying a delta is NOT safe. The producer has already
  committed the cost, so dropping it destroys what the player paid for.

Only three delta-carrying fields exist in the entire registry:
`MarketMatchNotification.GoldDelta` (fixed - see the market commit),
`ChronoAccelerationNotification.SecondsToAdd` (fixed here), and
`DamageDelta` (guild raid, idempotent - the raid boss row is authoritative).

`MailClaimRequestQueue` deserves note as the correct pattern already: if the
payload is gone the drain does nothing, so `CommitMailClaimAsync` never runs
and the mail simply stays unclaimed. Do-nothing equals no-loss by
construction, rather than by a rescue path.

`CraftingCompletionQueue` drops one quest-progress increment if the player
logs out mid-craft. The item itself is committed by `CraftingEngine`, so this
is a lost counter tick, not lost value. Left alone deliberately - a rescue
path would cost more complexity than the defect.

**Invariant 2 - every currency has exactly one authoritative store.**

- Gold: `CommodityRecords["gold"]` in all ten engines that touch it, with
  `TickStatePayload.CurrentGold` as an in-memory mirror flushed as a DELTA.
  The checkpoint never writes it back as a snapshot, which is what makes a
  direct database credit safe. No split.
- Diamonds: `PlayerRecords."PremiumDiamonds"` only. Was split; fixed.
- Legacy shards: `PlayerLegacyLedger.LegacyShardBalance` only, and the
  checkpoint only READS it (summing ledgers), so there is no snapshot
  write-back to clobber. No split.

**The generalisation worth keeping:** gold is delta-persisted and diamonds
are snapshot-persisted, and that difference decides whether an off-thread
credit is safe or gets silently refunded by the next checkpoint. Anyone
adding a currency should decide which of the two it is before writing the
first spend path.

## Live Play Mode session, 2026-08-01

Ran against the dev fixture (`--seed-dev`, player 1) with Postgres and Redis
in Docker and a real WebSocket session. Everything below was measured against
the database, not inferred.

### 42. Verified working end to end

- **Diamond rarity upgrade.** Uncommon -> Rare -> Epic -> Legendary, costing
  17, 57 and 196 Diamonds - matching `5 * 3.4^(rarity-1)` exactly. This is the
  path that was impossible before the store fix, and the balance survived a
  relog (client read 5186 after reconnect, matching the row).
- **Gold value reroll.** Cost 902 on a tier-3 item, matching
  `250 * 1.9^(tier-1)`. Diamonds unchanged, proving the currency split holds.
  Rarity preserved, magnitude moved 177 -> 151, inside the +/-20% band.
- **Legendary announcement.** Reached the client as
  `1|5|crit_dmg_pct|177` with `IsAnnouncement = true` on channel 3.

### 43. ChatEngine subscribes to Redis once at boot and never retries

Found by accident: the server was started before Redis, and chat was
completely dead - zero messages delivered, no error anywhere. Starting Redis
afterwards did not help. Only restarting the server fixed it, after which the
identical test delivered immediately.

`InitializeAsync` checks `redis == null || !redis.IsConnected` and returns
early, skipping all three channel subscriptions. There is no reconnect
handler and no retry, so a server that boots while Redis is unavailable has
permanently dead global, guild and whisper chat for the lifetime of the
process.

This is a realistic production failure, not a lab artefact: container start
order is not guaranteed, and Redis restarting under a running server produces
the same silent outcome. Same shape as the guild war bug - a condition
observed once and assumed to hold forever.

Fix direction: subscribe on the multiplexer's `ConnectionRestored` event as
well as at boot, and make the subscribe path idempotent (it already is for
the dispatch worker).

### 44. Chat rows are Addressables-only, so chat renders nothing in the Editor

`UiChatWindow.RowPrefabAddressableKey = "UiChatMessageRow"` resolves through
`AssetManager.LoadAsync`. Without built Addressables content the load fails
silently, `_rows` stays entirely null, and the window shows nothing even
while messages arrive correctly (verified: `_totalMessagesAccepted` reached 2
with zero rows instantiated).

So chat is untestable in Play Mode without an Addressables build, and if a
player build ever ships without that content, chat is invisible with no error.
Worth either bundling the row prefab as a direct reference like every other
pooled row in the project, or failing loudly when the key does not resolve.

### 45. The dev fixture seeds gear with empty affix payloads

All four seeded `EquipmentInstances` carry `AffixPayload = '{}'`, so the
reroll and affix UI cannot be exercised from the fixture at all - this
session had to write a payload in by hand. `DevFixtureSeeder` should roll a
real affix set through `AffixRegistry.RollAffixes`, exactly as a drop would,
so the fixture exercises the same path players do.

### 46. World Boss audit, 2026-08-01

Audited because it combines the two categories this codebase repeatedly gets
wrong: currency (rewards) and timers (respawn). Most of it holds up.

**Verified correct:**

- Reward delivery goes through the mailbox, whose claim path is already
  safe-by-construction: if the payload is gone the claim never commits and
  the mail is simply still there next login.
- No duplicate reward distribution. `ProcessDefeatedBossAsync` has an
  interlocked re-entry guard, takes the snapshot row `FOR UPDATE`, re-checks
  `CurrentHp > 0`, and on completion sets `EventState = 2` so the scheduler's
  `IsEventActive` check stops re-firing it. HP is reset to `BaseHp` in the
  same transaction, so the next window starts a fresh boss.
- The lifecycle is a recurring date-window poll (1st-7th, 15th-22nd UTC), not
  a one-shot equality check, so downtime spanning a boundary self-heals on the
  next tick. This is NOT the guild war bug shape.
- The 3-attempt cap reads `PlayerWorldBossAttempts` inside the transaction, so
  it cannot be bypassed by relogging - unlike the payload mirror, which is
  display only.

**Fixed: a full mailbox silently destroyed the whole reward.**
`existingMail.Count >= 50` did a bare `continue`, so a player who fought the
boss and placed in any bracket received nothing - no tokens, no gold, no log,
no telemetry, nothing visible to them or to ops. Now logged and streamed as a
telemetry event.

**Open design question, deliberately not decided here:** should an earned,
non-repeatable reward bypass the 50-item mailbox cap, or be held and
delivered when space frees up? Force-inserting would quietly break an
invariant that exists for a reason, and holding needs a retry store. Both are
design calls rather than bug fixes, which is why this pass only made the loss
visible.

**Not covered:** live end-to-end verification of a full kill. The event window
is date-gated (day 1-7 or 15-22 UTC) and today falls outside it, so exercising
a real defeat requires either clock manipulation or forcing the snapshot's
EventState - neither of which tests the scheduler that would run in
production. Static audit plus the reward-path reasoning above is what this
pass could honestly establish.

### 47. Leaderboard audit, 2026-08-01

**Fixed: every entry was named "Player".** The global leaderboard hardcoded
`DisplayName = "Player"` for all fifty rows, making it impossible to tell
anyone apart - while `PlayerRecords."Username"` sat on the very record already
loaded into the lookup dictionary two lines above. Now uses the real username,
falling back to `Player #id` because the column is nullable for accounts
created before it existed. The guild leaderboard was already correct
(`g.Name`), and the `"LocalRank"` string is a deliberate offline-fallback
placeholder, not a defect.

**Paging is correct.** `skip`/`take` are validated, the Redis call is
`SortedSetRangeByRankWithScoresAsync(key, skip, skip + take - 1, Descending)`,
and rank is `skip + i + 1` derived from the Redis index rather than the
filtered list - so a player id present in Redis but missing from Postgres
(deleted account, drift) shortens the page without corrupting the ranks below
it.

**Not a cold-start gap.** `SyncLeaderboardsAsync` runs BEFORE the 5-minute
delay, so a freshly booted server populates immediately. I initially misread
the loop order and recorded the opposite; corrected here.

### 48. Anti-cheat quarantined the dev account during a normal headless session

The live leaderboard test returned nothing, and the cause was that player 1
had `IsQuarantined` and `Quarantine_Active` both true - the leaderboard query
excludes quarantined accounts. `DevFixtureSeeder` explicitly sets both false,
so this happened AT RUNTIME during the session.

Source is the integrity-challenge path: `ConsecutiveChallengeMisses >=
AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit` sets both flags and
calls `RequestShadowBan`. The client DOES answer challenges
(`WebSocketClient.cs:269` responds on packet receipt), so this is not simply
an unimplemented responder.

That leaves two possibilities, and this pass could not distinguish them:

1. The verification hash disagrees between client and server, in which case
   every real player accumulates misses and is eventually shadow-banned.
2. The responses are correct but too slow under some conditions, in which case
   a laggy or backgrounded client is punished for its connection.

Either would be severe: quarantine silently removes a player from
leaderboards, and `RequestShadowBan` is not something a player can see or
appeal. This echoes the earlier anti-cheat finding about irreversible
penalties needing a reversal path - `--lift-quarantine <playerId>` exists and
was used to restore the fixture, but nothing surfaces that a player needs it.

Highest-priority item to investigate next: compare the client's hash
computation against the server's verifier directly, with a single challenge
round-trip logged on both sides.

### 48b. Anti-cheat challenge race - ROOT-CAUSED AND FIXED

Item 48 left two possibilities open. It was neither a hash mismatch nor slow
responses: `ComputeChallengeHash` and `XorShift32` are byte-identical on both
sides. The inputs disagreed.

The client hashes the epoch it saw in the broadcast. The server validated
against `payload.LogicEpochCounter` as it stood when the ANSWER arrived. That
counter advances on every successful checkpoint flush - and ordinary play
flushes constantly, including an explicit flush on every reroll command. So
any flush landing between broadcast and reply turned a correct answer into a
recorded miss, and enough misses meant quarantine plus `RequestShadowBan`,
invisible to the player and with no appeal.

Latency made it strictly worse, so the players most likely to be banned were
the ones with the worst connections - and the most active, since activity is
what drives flushes.

Fixed by pinning `ActiveChallengeIssuedEpoch` when the challenge is issued and
validating against that, so the answer is judged against the state the client
was actually shown.

Verified live: a session driving repeated rerolls advanced the epoch 18 times
- the exact condition that previously banned the account - and the account
stayed clean with zero challenge rejections. The same activity pattern
quarantined it before the fix.

A regression test asserts the hash genuinely depends on the epoch, which is
why pinning is required rather than optional.

### 49. Remaining subsystems: multitasking, friendlist, guild discovery, 2026-08-01

**Multitasking - audited clean.** The concern was resource duplication or
state lockout across concurrent character slots. `ProcessMultiSlotTick` swaps
a slot into the active register, runs `ProcessSubTick`, and swaps back - a
symmetric pair, so the register is always restored. Account-wide work
(`ProcessAccountTick`) runs once per tick OUTSIDE the slot loop, so it cannot
be applied three times. Slots 2 and 3 are skipped unless a real character
occupies them, with slot 0 deliberately exempt so injected virtual players
still run. Per-slot gold and loot accrue into account-wide fields, which is
the intended behaviour of multitasking rather than duplication.

One latent fragility worth noting rather than fixing: if `ProcessSubTick`
ever threw, the second swap would not run and the register would keep the
wrong slot's data while the payload believed it held slot 0. A try/finally
around the pair would make the restore unconditional. Not urgent - an
exception on the tick thread is already catastrophic - but it is a cheap
guarantee.

**Guild discovery - does not exist.** This is a feature gap, not a bug, and
the spec's "search functionality for players/guilds" has no implementation on
either side. The server exposes create, join, roster and the three
application endpoints; there is no guild list, browse or search endpoint
anywhere. Joining is BY EXACT NAME, so a player who has not been told a
guild's precise name has no way to find one at all. The application/approval
half is complete and works; only discovery is missing.

Smallest useful addition would be a paged `/api/v1/guilds/list` with an
optional name filter, mirroring the leaderboard's skip/take shape which is
already proven correct.

**Friendlist - only partially audited.** `/api/v1/friends/list` exists and
name resolution goes through the shared `HandlePlayerNames` path rather than
a second implementation, which is the right shape. Add/remove/invite flows
and online-status accuracy were NOT verified in this pass - recorded as
uncovered rather than claimed, since context ran out before they could be
exercised live.


### 49b. Item 49 follow-ups - SHIPPED

- **Slot register hardened.** The swap-back around `ProcessSubTick` is now in
  a `finally`, so an exception cannot leave the active register holding one
  slot's character while every later reader assumes slot 0. The corruption
  would have outlived the exception and been blamed on something else.
- **Dev fixture rolls real affixes.** Was a literal `{}` on every seeded item,
  so the reroll, affix and rarity UI could not be exercised without
  hand-writing a payload into the database first - which is exactly what the
  2026-08-01 live session had to do. Now goes through
  `AffixRegistry.RollAffixes` like a real drop. Verified: the fixture produced
  `attack_speed_pct@1` on a weapon, `crit_chance_pct@2` on a helmet and
  `flat_hp@3` on a chest - slot-legal, with per-affix rarities.
- **Guild discovery now exists.** `GET /api/v1/guilds/list` with `skip`/`take`
  and an optional case-insensitive `name` filter, ordered by active members.
  Paging deliberately mirrors the leaderboard's shape rather than inventing a
  second convention. Returns enough per guild - members, tier, MMR, tax, join
  type, minimum level - to decide whether to apply without a second
  round-trip. Verified live: 401 unauthenticated, and a genuinely unmatched
  path still 400s, so the route is real rather than a catch-all.

**Still open from item 49:** the client has no UI for the new endpoint yet -
this pass added the capability, not the screen. And the friendlist
add/remove/invite and online-status flows remain unaudited.

**Noted while testing, not a product bug:** deleting `EquipmentInstances` rows
leaves `characters.Equipped*Id` pointing at them, and the fixture's
"already equipped" guard then refuses to re-seed. Only reachable by editing
the database by hand, but worth knowing before the next fixture reset.

### 50. Friendlist audit, 2026-08-02

**Verified correct.** `RelationshipEngine.AddFriendAsync` validates properly
before touching anything: rejects self-friending and non-positive ids, checks
the target actually exists, and takes the existing relationship row `FOR
UPDATE` inside a Serializable transaction. Its raw SQL quotes identifiers
correctly. The command handlers dispatch through `SafeDispatchAsync` with no
tick-thread blocking. Validation living in the engine rather than the handler
is the right split - the handler cannot reach the database.

**Fixed: the friend list carried no online status at all.**
`FriendEntryResponse` returned PlayerId, Username, Level and IsBlocked, so the
list could not answer the one question a friend list exists to answer - who is
around right now. Neither the server DTO nor the client cache had the field.
Added on both sides, sourced from the live connection table rather than a
persisted column, since a stored flag goes stale the instant a process dies
without a clean logout and would then claim someone is online forever.

**Known limitation, deliberately not papered over:** the online answer is
POD-LOCAL. It reads this pod's own WebSocket table, so a friend connected to a
different pod reads as offline. That is correct for the single-pod deployment
this runs as today. A multi-pod answer needs a Redis presence key, and
inventing one inline would have created a second source of truth about who is
online - the exact class of split this codebase has already been bitten by
twice (diamonds, gold).

**Still not exercised live.** Add, remove, block and the online flag itself
were audited by reading, not by running two accounts against each other. The
mechanics are simple and the validation is visibly correct, but "looks right"
and "works" have diverged three times in this project within two days, so this
is recorded as unverified rather than passed.

### 51. Achievement rewards were silently refunded for online players, 2026-08-02

Same shape as the reroll diamond bug from 2026-08-01, found by looking for it
deliberately rather than by accident.

`AchievementEngine` credits `PlayerRecords."PremiumDiamonds"` and stops there.
For an ONLINE player the live payload owns `PremiumCurrency`, and
`StateCheckpointManager` writes it back with plain assignment
(`player.PremiumDiamonds = state.PremiumCurrency`), so the next flush
overwrote the reward with the payload's stale balance. The diamonds simply
disappeared.

Offline players were unaffected, since no payload existed to overwrite them -
which is precisely what made this hard to notice, and why the earlier session
saw an achievement grant survive while a different one would not have.

Fixed by enqueuing `BillingSyncNotification`, the same hand-off the reroll fix
uses: the tick thread is the only thread allowed to touch the payload.

**Checked and NOT affected:** `DailyLoginRewardEngine` grants at
`/api/v1/auth/login` and `/api/v1/auth/register`, both of which run before the
session payload is hydrated - so hydration reads the already-updated column.
Verified rather than assumed.

**Unresolved:** `StateCheckpointManager.EvaluateAndAwardTierAsync` also does
`player.PremiumDiamonds += diamondsAwarded`. It runs inside the checkpoint
itself and is a static method with no payload reference, so whether its award
survives the next flush depends on ordering this pass could not trace safely.
Recorded as open rather than guessed at. It is the last known instance of this
pattern.

### 52. Registration needs the backend running - not a bug

The user could not register from a self-launched Unity session. Cause: no
backend. The client alone cannot register; it needs Postgres, and the server
process listening on 8080. Verified working once started - POST
/api/v1/auth/register returned 200 with a JWT.

To play locally:
1. `docker start folk-idle-db folk-idle-redis` (Redis is optional but chat and
   leaderboards need it - and the server must start AFTER Redis, or see item
   43, now fixed by the retry loop).
2. `FOLKIDLE_DB_CONN="Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres" dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj`
3. Then enter Play Mode.

Worth considering a startup check in the client that says "cannot reach
server" plainly, since the current failure gives no indication that the
backend is simply absent.

### 53. Combat audit, 2026-08-02

**CRITICAL, FIXED: attack speed bonuses made the player attack SLOWER.**

Both attack timers used `(tickAccumulator * 100) % intervalMs == 0`, which
only fires when elapsed time lands EXACTLY on the interval. Any interval that
is not a divisor of a multiple of the 100ms tick therefore fired at the least
common multiple instead:

| bonus | intended | actual  | effect      |
|-------|----------|---------|-------------|
| 0%    | 1500ms   | 1500ms  | correct     |
| 5%    | 1425ms   | 5700ms  | 4x slower   |
| 10%   | 1350ms   | 2700ms  | 2x slower   |
| 20%   | 1200ms   | 1200ms  | correct     |
| 33%   | 1004ms   | 25100ms | 25x slower  |

Only bonuses landing on a multiple of 100 worked. `attack_speed_pct` is one of
the twelve rerollable affixes, so players were spending gold and diamonds to
make their character worse - and the same fault applied to any monster whose
authored `AttackIntervalMs` was not a multiple of 100.

Replaced with a boundary-crossing test (`HasCrossedInterval`): an attack is due
when elapsed/interval increases across the tick. Exact for any interval and
needs no extra payload state.

**Combat is NOT turn-based alternating.** Player and monster run independent
cadences off the same tick clock, which is the right model for an idle game -
attack speed only means something if the two sides are not locked in
lockstep - but it is worth stating plainly since the request assumed
alternation.

**Already working, verified:** session loot appears under the monster with
rarity in square brackets exactly as requested (`Iron Ore x3`,
`Doomblade [Legendary]`), skipping tier-0 materials. Inventory
(`UiInventoryPanel`), the village bank vault (`UiBankVaultWindow`,
`UiBankDepositCandidateRow`) and `WithdrawFromBank` all exist.

**Missing: per-monster drop preview.** Selecting a monster does not show what
it drops or the chance. The data cannot reach the client today: loot tables
are a hardcoded `_lootEntries` array inside the server's `ContentRegistry`,
and while monsters carry a `LootTableId` client-side, no loot table file ships
in StreamingAssets. Needs either a `/api/v1/monsters/{id}/loot` endpoint or a
generated `loot_tables.json` alongside the other content files. The endpoint is
probably better - drop rates are balance data and should not be a client asset
that can drift from the server's table.

**Not verified this pass:** that the bank vault round-trip actually works end
to end - deposit from inventory, then market or equip straight from the vault.
The screens and the command exist; the flow was not exercised.
