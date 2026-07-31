# FolkIdle Next Steps Backlog

Status: living document. Numbered items are independent units of work;
number order is priority order within a category, not a strict dependency
chain unless stated. Remove an item when it ships; do not renumber the
remaining items (a gap is fine and preserves historical references in
commit messages/PRs).

## Client UI Hook Points

### 1. Region-Completion Codex UI (no client visualization exists)

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

### 6. Remove or gate dead engine duplicates

- `Engine/SeasonEraEngine.cs`: orphaned duplicate of
  `Engine/SeasonalRotationEngine.cs` with a different, stale Legacy Shard
  formula. Confirm it is truly unreferenced, then delete it (do not merge
  it - `SeasonalRotationEngine` is the live, correct implementation).
- `Engine/PlayerChronoRegistry.cs` and the unused second
  `ChronoBufferEngine.ProcessLoginHandshake` overload: zero callers,
  candidates for deletion after a final reference check.

### 7. Fix ForgeSplicingEngine's dead BaseItemId parse

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

### 9. Set bonuses still collapse four armour slots into one set id

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

### 11. Per-character equipment loadouts for slots 2 and 3

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

### 13. Unity CI is skipped until a licence secret exists

`.github/workflows/unity_client.yml` now gates its test and build jobs on a
`licence-check` job that probes for `UNITY_LICENSE`. Without the secret the
Unity jobs report as skipped rather than failing the whole workflow. They are
genuinely not running: add `UNITY_LICENSE` (plus `UNITY_EMAIL` and
`UNITY_PASSWORD` for a Pro seat) as repository secrets to turn them on. Until
then, client-side verification is manual through the MCP Play Mode harness.

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

### 16. Test_MarketEscrow_ConcurrentListings is a load-dependent flake

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

### 17. Set bonuses and the offhand slot

`SetBonusEngine.Evaluate` still consumes a weapon/armour/leggings SetId triple,
so the four armour pieces collapse onto one armour set id (item 9) and the new
seventh slot contributes its base stats and affixes but no set id at all.

### 14. Play Mode harness needs a seeded fixture account

Verifying multi-character, equipment and progression behaviour in Play Mode
currently requires hand-seeding the database (Town Hall level, roster,
equipment) with a throwaway console app. A committed, idempotent dev-seed
entry point - alongside `--migrate` and `--lift-quarantine` - would make the
audit repeatable instead of improvised. Note it must stay clearly
non-production, guarded the way `--lift-quarantine` is.
