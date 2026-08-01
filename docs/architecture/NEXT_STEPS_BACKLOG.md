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

### 26. StateUpdatePacket.TotalItemsCraftedCount is never assigned

The field exists on the wire and the client mirrors it into
`VisualTotalItemsCraftedCount`, but no server code ever writes it, so it
reads 0 forever. The Statistics screen shows a correct crafted count only
because it reads the REST endpoint instead.

Four dead bytes on a packet with five bytes of headroom under the 700-byte
ceiling. Either assign it from `PlayerRecord.TotalItemsCrafted` or delete it
and reclaim the space - but do not leave a zero-valued field on the wire for
the next person to bind a UI to.

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

### 28. CombatStats.SetCooldownReductionActive is redundant

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

### 30. Stale TODO in the AssignMentor command branch

`SimulationEngine.cs` around line 1872 carries a TODO asking whether a
validator check is needed. `ClientCommandValidator.ValidateMentorshipAssignment`
is called on the very next line. One line to delete. Noted only because it
is the single TODO marker in the entire codebase and reads as a gap when it
is not.

### 31. Character stat rows render bare numbers with no labels

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
