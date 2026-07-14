# FolkIdle Game Design Specification

Status: living document, generated from the authoritative server implementation.
Scope: finalized numeric formulas that govern core simulation systems. Where the
implementation and any prior prompt/chat-history description disagree, this
document reflects what the code actually does, since the code is the source of
truth for runtime behavior.

All file references are relative to `server/FolkIdle.Server/`.

## 1. Combat Resolution Model

Combat is resolved deterministically inside the 10 Hz tick, not via a single
"survival probability" roll. Each tick both combatants may act, gated by their
own attack interval; each individual attack then rolls hit chance and crit
chance independently. See `Engine/SimulationEngine.cs`, the combat block
starting at `ProcessSubTick` (player-attacks-monster around line 2870,
monster-attacks-player around line 2909).

### 1.1 Attack cadence

- Player attack interval (ms): `max(200, 1500 * (1 - AttackSpeedPct))`
- Monster attack interval (ms): `activeMonster.AttackIntervalMs` (per-monster
  content data)
- A combatant acts on any tick where
  `(CombatTargetTickAccumulator * 100) % intervalMs == 0`.

### 1.2 Hit determination

```
hitChance = clamp(attackerAccuracy / defenderDodge, 0.05, 0.95)
```

- Player attacking: `attackerAccuracy = 100`, `defenderDodge = 100`
  (accuracy stat is not yet implemented; hit chance is effectively fixed at
  the midpoint until it is).
- Monster attacking: `attackerAccuracy = 100`,
  `defenderDodge = 100 + combatStats.DodgeChancePct`. A higher DodgeChancePct
  (defensive potions, Vila's innate racial passive) directly lowers the
  monster's chance to connect.

### 1.3 Player damage output

```
effectiveMilliAttack = 15000
                      + 15000 * lineage.DamageScalePerLevelPct * CurrentLevel / 100
                      + FlatMeleeDamage * 1000
critMult   = 1.5 if (roll <= CritChancePct / 100) else 1.0
rawDamage  = effectiveMilliAttack * critMult
netDamage  = max(1000, rawDamage - defenderArmor * 1000)   // defenderArmor is currently 0 (monster armor not modeled)
netDamage *= CachedCodexDamageMultiplier
```

Damage values are stored in "milli" units (x1000) internally and only
converted to whole numbers at the HP-subtraction boundary; `netDamage` floors
at 1000 (i.e. 1 whole point) so a hit can never deal zero damage.

Lifesteal (if `LifestealPct > 0`): `PlayerHp += netDamage * LifestealPct`,
capped at `effectiveMaxHp`.

### 1.4 Monster damage output and crit mitigation

```
monsterRegionTier  = ((CurrentMonsterId - 1) % 30) / 6 + 1
monsterCritChance  = 0.05 + monsterRegionTier * 0.005
monsterCritMult    = max(1.0, 1.5 - CritMitigationPct / 100)   if crit roll succeeds, else 1.0
rawDamage          = activeMonster.AttackPower * 1000 * monsterCritMult
netDamage          = max(1000, rawDamage - FlatPhysicalArmor * 1000)
finalDamage        = max(0, netDamage - blockStrength * 1000)   // blockStrength is currently 0 (shields not modeled)
PlayerHp -= finalDamage
```

`CritMitigationPct` (Vodnik's innate racial passive, see
`Engine/StatsCalculator.cs`) subtracts directly from the monster's crit
multiplier and is floored at 1.0, so mitigation can reduce a crit down to a
normal-hit multiplier but never below it.

### 1.5 Auto-Eat survival mechanic

There is no explicit "survival probability" formula; survival is a
consequence of the Auto-Eat threshold and food stock. Each tick, if
`0 < PlayerHp <= (AutoEatThreshold / 100) * effectiveMaxHp`, the engine
consumes the highest-value available food slot (each configured food heals a
flat 50000 milli-HP, i.e. 50 whole HP) and clamps the result to
`effectiveMaxHp`. If no food remains, the character's active activity is
force-stopped (`ActiveActivityId = 0`) and a
`KpiAutoEatDepletedHaltHash` telemetry event is recorded.

If `PlayerHp` reaches `<= 0` regardless (e.g. a single hit exceeds the
Auto-Eat trigger window), the character is fully healed
(`PlayerHp = effectiveMaxHp`), combat state is reset
(`CurrentMonsterId = 0`, `CurrentMonsterHp = 0`), and the active activity is
cleared. There is no permadeath, gold loss, or item loss on defeat.

## 2. Gathering Loot Luck (reweighted, non-multiplicative model)

`LootLuckPct` no longer multiplies gathering roll *count* (an earlier design
that inflated every table entry, common and rare alike, in fixed proportion
and never actually shifted rarity odds). It instead adds a flat weight bonus
to every loot-table entry:

```
luckWeightBonus = max(0, floor(LootLuckPct * 0.1))
totalWeight      = sum(entry.Weight + luckWeightBonus for entry in lootTable)
```

Because the bonus is additive and constant across entries, it represents a
much larger *relative* increase for a low-weight (rare) entry than a
high-weight (common/trash) entry, so higher LootLuckPct genuinely shifts the
effective drop distribution toward rarer items rather than just producing
more of everything. See `Engine/SimulationEngine.cs` around line 2761 (live
gathering tick) and line 2153 (offline/warp catch-up path, which mirrors the
same model as `FinalChance = BaseChance * (1 + LootLuckPct / 100)`).

Roll *count* itself is driven independently by monolith/race/event bonuses
plus `LocusYield` (a bred genetic trait): `+4` percentage points of roll
count per point of LocusYield.

## 3. Market Volatility Corridor and Progressive Taxation

See `Engine/MarketOrderBookEngine.cs`, `Engine/MarketEscrowEngine.cs`.

### 3.1 Reference price

For a given `(BaseItemId, QualityTier)` pair, the reference price is the
mean `ExecutionPrice` of all `HistoricalMarketArchives` rows for that pair
within the trailing 7 days. If no such rows exist (an untraded or newly
introduced item), the reference price falls back to a deterministic
baseline computed from static content data:

```
QualityTierMultiplier = 1.0 + QualityTier * 0.5
fallbackPrice          = ItemDefinition.BaseValueGold * QualityTierMultiplier
```

(`ItemDefinition` and the `BaseItemId -> ItemDefinition` reverse lookup live
in `Engine/ContentRegistry.cs`.) The corridor check is skipped entirely only
if the item has neither trade history nor a recognized ContentRegistry
entry, which should not occur for any real game item.

### 3.2 Volatility corridor

```
P_min = referencePrice * 0.80
P_max = referencePrice * 3.00
```

Any BUY or SELL order (via the order-book path in `MarketOrderBookEngine`)
or direct listing (via `MarketEscrowEngine.ListItemAsync`) priced outside
`[P_min, P_max]` is rejected before any row mutation.

### 3.3 Progressive (wealth-scaled) taxation

Applied at trade settlement, based on the seller's current gold balance:

```
totalFeeRate = 0.05                     if sellerWealth <  500,000
             = 0.08                     if 500,000 <= sellerWealth <= 5,000,000
             = 0.15                     if sellerWealth >  5,000,000

fee              = floor(executionPrice * totalFeeRate)
sellerProceeds   = executionPrice - fee
```

This bracket set is authoritative and is applied identically in
`MarketOrderBookEngine.MatchOrdersAsync`, `MarketEscrowEngine.BuyItemAsync`,
and mirrored for client display in
`client/Assets/Scripts/UI/UiMarketDataBinder.cs`.

## 4. Character Aging Phases

Each of a player's up to three active character slots ages independently in
real tick-time (10 Hz). See `Engine/SimulationEngine.cs`,
`ProcessAgeSlot` (line 2606) and the per-phase stat penalty application in
`Engine/StatsCalculator.cs` (line 191).

### 4.1 Phase thresholds

| Phase | Name   | AgeTicks range          | Real-time at 10 Hz |
|-------|--------|--------------------------|---------------------|
| 0     | Child  | 0 - 35,999               | 0 - 1 hour           |
| 1     | Adult  | 36,000 - 71,999          | 1 - 2 hours          |
| 2     | Senior | 72,000 - 107,999         | 2 - 3 hours          |
| 3     | Old    | 108,000+                 | 3+ hours             |

### 4.2 Stat effect

Phases 0 and 1 apply no modifier (Adult is the implicit baseline for combat
math when no character slot is active - see `warpActiveAgePhase`/
`activeAgePhase` defaulting to `1` when `Slot1_CharacterId` is empty).

```
Phase 2 (Senior): FlatMeleeDamage *= 0.9, FlatRangedDamage *= 0.9, MaxHp *= 0.9, AttackSpeedPct *= 0.9
Phase 3 (Old):    FlatMeleeDamage *= 0.8, FlatRangedDamage *= 0.8, MaxHp *= 0.8, AttackSpeedPct *= 0.8
```

A phase transition sets `IsDirty = true` so the next network broadcast
reflects the character's updated combat capability without requiring
re-login.

## 5. Legacy Shard Calculation (Seasonal Reset Prestige Currency)

Computed once per player during `SeasonalRotationEngine.ExecutePlayerRolloversAsync`
at the close of a seasonal era, see `Engine/SeasonalRotationEngine.cs`,
`CalculateLegacyShards` (line 253).

```
goldTerm      = 12.5 * log10(max(0, totalGold) + 1)
levelTerm     = 0.05 * max(0, characterLevelSquareSum)
inventoryTerm = 1.50 * max(0, inventoryScore)

shardsEarned  = floor(goldTerm + levelTerm + inventoryTerm + 1e-9)   // epsilon guards against floating-point floor-down at exact integer boundaries
              clamped to [0, int.MaxValue]
```

Where:
- `totalGold` is the player's gold `CommodityRecord` quantity at reset time.
- `characterLevelSquareSum` is the sum of `CurrentLevel^2` across the
  player's characters (rewards a few high-level characters more than many
  low-level ones, superlinearly).
- `inventoryScore` is computed by `CalculateInventoryScore` (line 264): for
  every equipped, banked, and market-listed equipment instance, add
  `max(1, QualityTier)` - so even a QualityTier-0 item contributes at least 1
  point, and higher-tier items contribute proportionally more.

The result is added to the player's `PlayerLegacyLedger.LegacyShardBalance`
for the new era and used to unlock permanent account-wide progression
(Citizen multi-slots, Legacy Store purchases).
