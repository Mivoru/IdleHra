# Breeding Traits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the three invisible genes (Speed/Crit/Yield) and the hidden -25% inbreeding growth penalty with fourteen named, visible, heritable traits, and fix offline combat ignoring the Strength aptitude on the way.

**Architecture:** Traits are defined in code (`TraitRegistry`), stored as one `TraitMask` bigint on each lineage row and each village newcomer, cached on `TickStatePayload`, summed into a capped `TraitTotals`, and applied at the same points as aptitudes through one shared `BloodlineBonuses` class used by both the live and the offline simulation. The client reads the catalogue and the masks over REST; `StateUpdatePacket` does not change.

**Tech Stack:** C# / .NET 8, EF Core + Npgsql, xUnit + Testcontainers; Svelte 5 + TypeScript, TanStack Query, vitest, Playwright (`exercise.mjs`).

**Spec:** `docs/superpowers/specs/2026-09-13-breeding-traits-design.md`

## Global Constraints

- Trait bit positions are permanent: 0-10 positive, 16-18 flaws. Never renumber.
- At most 3 traits per character (`TraitRegistry.MaxTraitsPerCharacter = 3`), no duplicates.
- Per-stat trait totals are clamped to `+20` (`TraitTotals.PositiveCap`) and `-15` (`TraitTotals.NegativeFloor`).
- Inheritance: 50% from one parent, 90% when both carry it. Related pair: 60% flaw. Mutation: 4% + 1% per Breeding Grounds level. Epic: guaranteed Rare (80) or Legendary (20).
- Newcomers: 20% + 3% per Inn level, capped at 50%; 15% of those are flaws; Legendary only from Inn level 6 (weights 60/32/8, else 75/25/0).
- Existing characters get no traits; nothing is backfilled.
- `StateUpdatePacket` is not modified.
- Stop any running `FolkIdle.Server.exe` / `dotnet` before `dotnet build` or `dotnet test` (the stale-build hook blocks the whole command otherwise - run the stop in a SEPARATE command).
- `dotnet test` needs Docker running.
- Commit messages: write the message file with the Write tool and use `git commit -F <file>` - never PowerShell `Set-Content`/`Out-File` (it adds a UTF-8 BOM).
- Comments follow the repo's `// Modul:` convention: explain why, not what.
- All player-facing prose in English. Trait text keys `trait.<key>.name` / `trait.<key>.description` are reserved for round 3 (translation); this plan ships the English strings only.

## File Map

**Create (server)**
- `server/FolkIdle.Server/Engine/TraitRegistry.cs` - the catalogue, bit helpers.
- `server/FolkIdle.Server/Engine/TraitTotals.cs` - summed, capped effects of a mask.
- `server/FolkIdle.Server/Engine/BreedingTraits.cs` - inheritance, mutation, newcomer rolls, preview odds.
- `server/FolkIdle.Server/Engine/BloodlineBonuses.cs` - the four stat applications shared by live and offline.
- `server/FolkIdle.Server/Migrations/<timestamp>_AddBreedingTraits.cs` (+ Designer, snapshot update) - generated.
- `server/FolkIdle.Server.Tests/TraitRegistryTests.cs`
- `server/FolkIdle.Server.Tests/BreedingTraitsTests.cs`
- `server/FolkIdle.Server.Tests/BloodlineBonusesTests.cs`
- `server/FolkIdle.Server.Tests/BreedingTraitsIntegrationTests.cs`

**Modify (server)**
- `Models/CharacterLineageRegistry.cs`, `Models/VillageNewcomer.cs` - `TraitMask`.
- `Engine/TickStatePayload.cs` - `TraitMask`; remove `LocusSpeed/LocusCrit/LocusYield/IsInbred`.
- `Domain/Shared/StateCheckpointManager.cs` - hydrate `TraitMask`.
- `Engine/StatsCalculator.cs` - `TraitTotals traits` replaces `locusSpeed/locusCrit`.
- `Domain/Combat/SimulationEngine.cs`, `Engine/OfflineSimulationEngine.cs` - callers + `BloodlineBonuses`.
- `Domain/Combat/EquipmentSlotEngine.cs`, `Engine/GuildWarSnapshotEngine.cs` - callers.
- `Engine/RaceAttributeGrowth.cs` - drop loci and inbred terms.
- `Engine/BreedingEngine.cs`, `Engine/VillageArrivalEngine.cs` - roll traits.
- `Network/NetworkBroadcastSystem.cs` - traits endpoint, masks, preview odds, drop Speed/Crit/Yield loci.
- Tests: `HardenedEngineIntegrationTests.cs`, `GatheringShareTests.cs`, `ProgressionRateTests.cs`, `PowerCeilingTests.cs`.

**Create (client)**
- `client_web/src/lib/ui/traits.ts`, `client_web/src/lib/ui/TraitBadge.svelte`
- `client_web/tests/traits.test.ts`

**Modify (client)**
- `src/lib/net/rest.ts`, `src/lib/ui/breedingPicker.ts`, `src/lib/ui/PersonPicker.svelte`, `src/lib/ui/ChildPreview.svelte`, `src/lib/ui/breeding.ts`, `src/routes/Breeding.svelte`, `src/routes/Ancestors.svelte`, `src/lib/ui/VillageFolk.svelte`, `src/lib/ui/WikiVillage.svelte`, `tests/breedingPicker.test.ts`, `scripts/exercise.mjs`, `docs/breeding_model.md`.

---

### Task 1: The trait catalogue

**Files:**
- Create: `server/FolkIdle.Server/Engine/TraitRegistry.cs`
- Test: `server/FolkIdle.Server.Tests/TraitRegistryTests.cs`

**Interfaces:**
- Produces: `enum TraitRarity { Common, Rare, Legendary, Flaw }`; `enum TraitEffect { MaxHpPct, AttackPct, GatherSpeedPct, GatherYieldPct, CritChancePoints, AttackSpeedPct, DodgePoints, LifestealPct, RarityElevationPoints }`; `readonly record struct TraitDefinition(int Bit, string Key, string Name, TraitRarity Rarity, TraitEffect Effect, int Value, string Description)`; `static class TraitRegistry` with constants `StoutHeart=0 ... ClumsyHands=18`, `All`, `MaxTraitsPerCharacter`, `KnownBitsMask`, `TryGet(int, out TraitDefinition)`, `Has(long,int)`, `CountOf(long)`, `BitsOf(long)`, `MaskOf(params int[])`, `OfRarity(TraitRarity)`, `IsFlaw(int)`.

- [ ] **Step 1: Write the failing test**

```csharp
// server/FolkIdle.Server.Tests/TraitRegistryTests.cs
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE BIT TABLE IS A PERSISTED FORMAT. A character's traits are
    /// stored as positions in a long, so moving a trait to another bit silently
    /// swaps every live character's traits. This pins every position.
    /// </summary>
    public class TraitRegistryTests
    {
        [Fact]
        public void EveryTraitKeepsItsBit()
        {
            var expected = new (string Key, int Bit, TraitRarity Rarity)[]
            {
                ("stout_heart", 0, TraitRarity.Common),
                ("keen_edge", 1, TraitRarity.Common),
                ("quick_hands", 2, TraitRarity.Common),
                ("green_thumb", 3, TraitRarity.Common),
                ("iron_blood", 4, TraitRarity.Rare),
                ("hawk_eye", 5, TraitRarity.Rare),
                ("swift_blood", 6, TraitRarity.Rare),
                ("nimble", 7, TraitRarity.Rare),
                ("blood_of_kings", 8, TraitRarity.Legendary),
                ("wolfs_hunger", 9, TraitRarity.Legendary),
                ("fae_touched", 10, TraitRarity.Legendary),
                ("thin_blood", 16, TraitRarity.Flaw),
                ("faint_heart", 17, TraitRarity.Flaw),
                ("clumsy_hands", 18, TraitRarity.Flaw),
            };

            Assert.Equal(expected.Length, TraitRegistry.All.Length);
            foreach (var (key, bit, rarity) in expected)
            {
                var def = TraitRegistry.All.Single(t => t.Key == key);
                Assert.Equal(bit, def.Bit);
                Assert.Equal(rarity, def.Rarity);
            }
        }

        [Fact]
        public void BitsAreUniqueAndFlawsLiveAboveSixteen()
        {
            Assert.Equal(TraitRegistry.All.Length, TraitRegistry.All.Select(t => t.Bit).Distinct().Count());
            Assert.All(TraitRegistry.All.Where(t => t.Rarity == TraitRarity.Flaw), t => Assert.True(t.Bit >= 16));
            Assert.All(TraitRegistry.All.Where(t => t.Rarity != TraitRarity.Flaw), t => Assert.True(t.Bit < 16));
            Assert.All(TraitRegistry.All, t => Assert.InRange(t.Bit, 0, 62));
        }

        [Fact]
        public void FlawsHurtAndEverythingElseHelps()
        {
            Assert.All(TraitRegistry.All, t =>
            {
                if (t.Rarity == TraitRarity.Flaw) Assert.True(t.Value < 0, t.Key);
                else Assert.True(t.Value > 0, t.Key);
                Assert.False(string.IsNullOrWhiteSpace(t.Name));
                Assert.False(string.IsNullOrWhiteSpace(t.Description));
            });
        }

        [Fact]
        public void MaskHelpersRoundTrip()
        {
            long mask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.ThinBlood);
            Assert.True(TraitRegistry.Has(mask, TraitRegistry.IronBlood));
            Assert.False(TraitRegistry.Has(mask, TraitRegistry.HawkEye));
            Assert.Equal(2, TraitRegistry.CountOf(mask));
            Assert.Equal(new[] { TraitRegistry.IronBlood, TraitRegistry.ThinBlood }, TraitRegistry.BitsOf(mask).ToArray());
            Assert.True(TraitRegistry.IsFlaw(TraitRegistry.ThinBlood));
            Assert.False(TraitRegistry.IsFlaw(TraitRegistry.IronBlood));
            Assert.Equal(0L, TraitRegistry.KnownBitsMask & (1L << 11));
            Assert.Equal(4, TraitRegistry.OfRarity(TraitRarity.Rare).Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Stop the server first (separate command): `Get-Process FolkIdle.Server, dotnet -ErrorAction SilentlyContinue | Stop-Process -Force`
Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~TraitRegistryTests"`
Expected: build FAILS - `The name 'TraitRegistry' does not exist in the current context`.

- [ ] **Step 3: Write minimal implementation**

```csharp
// server/FolkIdle.Server/Engine/TraitRegistry.cs
using System;
using System.Collections.Generic;
using System.Numerics;

namespace FolkIdle.Server.Engine
{
    public enum TraitRarity { Common = 0, Rare = 1, Legendary = 2, Flaw = 3 }

    public enum TraitEffect
    {
        MaxHpPct,
        AttackPct,
        GatherSpeedPct,
        GatherYieldPct,
        CritChancePoints,
        AttackSpeedPct,
        DodgePoints,
        LifestealPct,
        RarityElevationPoints,
    }

    public readonly record struct TraitDefinition(
        int Bit, string Key, string Name, TraitRarity Rarity, TraitEffect Effect, int Value, string Description);

    /// <summary>
    /// THE HERITABLE TRAITS. See docs/superpowers/specs/2026-09-13-breeding-traits-design.md.
    ///
    /// Modul: they replace three genes nobody could see - Speed, Crit and Yield
    /// paid hundredths of a percent, a village partner carried zeroes, and a
    /// "mutation" XOR'd the low bits. A trait has a name, a rarity and one
    /// effect the game demonstrably reads.
    ///
    /// THE BIT IS A PERSISTED FORMAT. A character stores its traits as positions
    /// in character_lineage_registry.TraitMask, so a trait may be ADDED on a free
    /// bit and never moved. TraitRegistryTests pins every one. Flaws start at 16
    /// so positive traits have room to grow.
    ///
    /// PURE AND STATIC like BreedingAptitudes: no database, no session.
    /// </summary>
    public static class TraitRegistry
    {
        public const int MaxTraitsPerCharacter = 3;

        public const int StoutHeart = 0;
        public const int KeenEdge = 1;
        public const int QuickHands = 2;
        public const int GreenThumb = 3;
        public const int IronBlood = 4;
        public const int HawkEye = 5;
        public const int SwiftBlood = 6;
        public const int Nimble = 7;
        public const int BloodOfKings = 8;
        public const int WolfsHunger = 9;
        public const int FaeTouched = 10;
        public const int ThinBlood = 16;
        public const int FaintHeart = 17;
        public const int ClumsyHands = 18;

        public static readonly TraitDefinition[] All =
        {
            new(StoutHeart, "stout_heart", "Stout Heart", TraitRarity.Common, TraitEffect.MaxHpPct, 5, "+5% max HP"),
            new(KeenEdge, "keen_edge", "Keen Edge", TraitRarity.Common, TraitEffect.AttackPct, 4, "+4% attack damage"),
            new(QuickHands, "quick_hands", "Quick Hands", TraitRarity.Common, TraitEffect.GatherSpeedPct, 5, "+5% gathering speed"),
            new(GreenThumb, "green_thumb", "Green Thumb", TraitRarity.Common, TraitEffect.GatherYieldPct, 5, "+5% gathering yield"),
            new(IronBlood, "iron_blood", "Iron Blood", TraitRarity.Rare, TraitEffect.MaxHpPct, 8, "+8% max HP"),
            new(HawkEye, "hawk_eye", "Hawk Eye", TraitRarity.Rare, TraitEffect.CritChancePoints, 3, "+3 crit chance"),
            new(SwiftBlood, "swift_blood", "Swift Blood", TraitRarity.Rare, TraitEffect.AttackSpeedPct, 6, "+6% attack speed"),
            new(Nimble, "nimble", "Nimble", TraitRarity.Rare, TraitEffect.DodgePoints, 4, "+4 dodge"),
            new(BloodOfKings, "blood_of_kings", "Blood of Kings", TraitRarity.Legendary, TraitEffect.AttackPct, 10, "+10% attack damage"),
            new(WolfsHunger, "wolfs_hunger", "Wolf's Hunger", TraitRarity.Legendary, TraitEffect.LifestealPct, 3, "+3% lifesteal"),
            new(FaeTouched, "fae_touched", "Fae Touched", TraitRarity.Legendary, TraitEffect.RarityElevationPoints, 4, "+4 chance a drop is a rarity higher"),
            new(ThinBlood, "thin_blood", "Thin Blood", TraitRarity.Flaw, TraitEffect.MaxHpPct, -6, "-6% max HP"),
            new(FaintHeart, "faint_heart", "Faint Heart", TraitRarity.Flaw, TraitEffect.AttackPct, -5, "-5% attack damage"),
            new(ClumsyHands, "clumsy_hands", "Clumsy Hands", TraitRarity.Flaw, TraitEffect.GatherSpeedPct, -6, "-6% gathering speed"),
        };

        /// <summary>Every bit that names a trait. A stray bit from a bad write is ignored, never applied.</summary>
        public static readonly long KnownBitsMask = BuildKnownMask();

        private static long BuildKnownMask()
        {
            long mask = 0L;
            foreach (var t in All) mask |= 1L << t.Bit;
            return mask;
        }

        public static bool TryGet(int bit, out TraitDefinition definition)
        {
            foreach (var t in All)
            {
                if (t.Bit == bit)
                {
                    definition = t;
                    return true;
                }
            }
            definition = default;
            return false;
        }

        public static bool Has(long mask, int bit) => bit is >= 0 and < 63 && (mask & (1L << bit)) != 0;

        public static int CountOf(long mask) => BitOperations.PopCount((ulong)(mask & KnownBitsMask));

        public static IEnumerable<int> BitsOf(long mask)
        {
            long known = mask & KnownBitsMask;
            for (int bit = 0; bit < 63; bit++)
            {
                if ((known & (1L << bit)) != 0) yield return bit;
            }
        }

        public static long MaskOf(params int[] bits)
        {
            long mask = 0L;
            foreach (int bit in bits) mask |= 1L << bit;
            return mask;
        }

        public static IReadOnlyList<TraitDefinition> OfRarity(TraitRarity rarity) => Array.FindAll(All, t => t.Rarity == rarity);

        public static bool IsFlaw(int bit) => TryGet(bit, out var def) && def.Rarity == TraitRarity.Flaw;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~TraitRegistryTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

Write `commit-msg.txt` with the Write tool (subject `feat(breeding): the trait catalogue, with bit positions pinned`, body explaining the persisted-format rule, then the two attribution lines), then:
```bash
git add server/FolkIdle.Server/Engine/TraitRegistry.cs server/FolkIdle.Server.Tests/TraitRegistryTests.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 2: Summed, capped trait effects

**Files:**
- Create: `server/FolkIdle.Server/Engine/TraitTotals.cs`
- Test: `server/FolkIdle.Server.Tests/TraitRegistryTests.cs` (append)

**Interfaces:**
- Consumes: `TraitRegistry.All`, `TraitRegistry.Has`.
- Produces: `readonly struct TraitTotals` with `int` properties `MaxHpPct, AttackPct, GatherSpeedPct, GatherYieldPct, CritChancePoints, AttackSpeedPct, DodgePoints, LifestealPct, RarityElevationPoints`; constants `PositiveCap = 20`, `NegativeFloor = -15`; `static TraitTotals From(long mask)`.

- [ ] **Step 1: Write the failing test** (append inside `TraitRegistryTests`)

```csharp
        [Fact]
        public void TotalsSumAndCap()
        {
            Assert.Equal(0, TraitTotals.From(0L).MaxHpPct);

            var hp = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.StoutHeart, TraitRegistry.IronBlood));
            Assert.Equal(13, hp.MaxHpPct);

            var mixed = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.ThinBlood));
            Assert.Equal(2, mixed.MaxHpPct);

            var attack = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.KeenEdge, TraitRegistry.BloodOfKings));
            Assert.Equal(14, attack.AttackPct);
            Assert.InRange(attack.AttackPct, TraitTotals.NegativeFloor, TraitTotals.PositiveCap);

            var others = TraitTotals.From(TraitRegistry.MaskOf(
                TraitRegistry.HawkEye, TraitRegistry.SwiftBlood, TraitRegistry.Nimble,
                TraitRegistry.WolfsHunger, TraitRegistry.FaeTouched, TraitRegistry.GreenThumb, TraitRegistry.ClumsyHands));
            Assert.Equal(3, others.CritChancePoints);
            Assert.Equal(6, others.AttackSpeedPct);
            Assert.Equal(4, others.DodgePoints);
            Assert.Equal(3, others.LifestealPct);
            Assert.Equal(4, others.RarityElevationPoints);
            Assert.Equal(5, others.GatherYieldPct);
            Assert.Equal(-6, others.GatherSpeedPct);
        }

        [Fact]
        public void UnknownBitsAreIgnored()
        {
            Assert.Equal(0, TraitTotals.From(1L << 40).MaxHpPct);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~TraitRegistryTests"`
Expected: build FAILS - `The name 'TraitTotals' does not exist`.

- [ ] **Step 3: Write minimal implementation**

```csharp
// server/FolkIdle.Server/Engine/TraitTotals.cs
using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a trait mask adds up to, capped per stat.
    ///
    /// Modul: EVERY MULTIPLIER DECLARES A CAP (CLAUDE.md, PowerCeilingTests).
    /// Three traits could stack two attack traits; the cap keeps a bloodline
    /// "noticeable, not decisive" beside aptitudes that top out at +45%.
    /// A value type computed on demand: fourteen definitions and a few ifs, so
    /// no cache that could go stale when a character is fielded.
    /// </summary>
    public readonly struct TraitTotals
    {
        public const int PositiveCap = 20;
        public const int NegativeFloor = -15;

        public int MaxHpPct { get; init; }
        public int AttackPct { get; init; }
        public int GatherSpeedPct { get; init; }
        public int GatherYieldPct { get; init; }
        public int CritChancePoints { get; init; }
        public int AttackSpeedPct { get; init; }
        public int DodgePoints { get; init; }
        public int LifestealPct { get; init; }
        public int RarityElevationPoints { get; init; }

        public static TraitTotals From(long mask)
        {
            if ((mask & TraitRegistry.KnownBitsMask) == 0L) return default;

            int hp = 0, attack = 0, gatherSpeed = 0, gatherYield = 0, crit = 0, speed = 0, dodge = 0, lifesteal = 0, rarity = 0;
            foreach (var t in TraitRegistry.All)
            {
                if (!TraitRegistry.Has(mask, t.Bit)) continue;
                switch (t.Effect)
                {
                    case TraitEffect.MaxHpPct: hp += t.Value; break;
                    case TraitEffect.AttackPct: attack += t.Value; break;
                    case TraitEffect.GatherSpeedPct: gatherSpeed += t.Value; break;
                    case TraitEffect.GatherYieldPct: gatherYield += t.Value; break;
                    case TraitEffect.CritChancePoints: crit += t.Value; break;
                    case TraitEffect.AttackSpeedPct: speed += t.Value; break;
                    case TraitEffect.DodgePoints: dodge += t.Value; break;
                    case TraitEffect.LifestealPct: lifesteal += t.Value; break;
                    case TraitEffect.RarityElevationPoints: rarity += t.Value; break;
                }
            }

            return new TraitTotals
            {
                MaxHpPct = Clamp(hp),
                AttackPct = Clamp(attack),
                GatherSpeedPct = Clamp(gatherSpeed),
                GatherYieldPct = Clamp(gatherYield),
                CritChancePoints = Clamp(crit),
                AttackSpeedPct = Clamp(speed),
                DodgePoints = Clamp(dodge),
                LifestealPct = Clamp(lifesteal),
                RarityElevationPoints = Clamp(rarity),
            };
        }

        private static int Clamp(int value) => Math.Clamp(value, NegativeFloor, PositiveCap);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~TraitRegistryTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

Subject: `feat(breeding): trait totals, summed and capped per stat`.
```bash
git add server/FolkIdle.Server/Engine/TraitTotals.cs server/FolkIdle.Server.Tests/TraitRegistryTests.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 3: Inheritance, mutation and newcomer rules

**Files:**
- Create: `server/FolkIdle.Server/Engine/BreedingTraits.cs`
- Test: `server/FolkIdle.Server.Tests/BreedingTraitsTests.cs`

**Interfaces:**
- Consumes: `TraitRegistry` (Task 1).
- Produces:
  - `static long BreedingTraits.Inherit(long fatherMask, long motherMask, bool isRelated, bool isEpic, int groundsLevel, Random rng)`
  - `static long BreedingTraits.RollNewcomerTrait(int innLevel, Random rng)`
  - `static IReadOnlyList<TraitOdds> BreedingTraits.PreviewOdds(long heroMask, long partnerMask)`
  - `static int BreedingTraits.MutationPercentFor(int groundsLevel)`, `NewcomerTraitPercentFor(int innLevel)`, `FlawPercentFor(bool isRelated)`
  - `readonly record struct TraitOdds(int Bit, int ChancePct, string Source)` where Source is `"hero"`, `"partner"` or `"both"`.

- [ ] **Step 1: Write the failing tests**

```csharp
// server/FolkIdle.Server.Tests/BreedingTraitsTests.cs
using System;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    public class BreedingTraitsTests
    {
        /// <summary>Returns the given values in order (each taken modulo maxValue), then repeats the last.</summary>
        private sealed class SequenceRandom : Random
        {
            private readonly int[] _values;
            private int _index;
            public SequenceRandom(params int[] values) => _values = values;
            public override int Next(int maxValue)
            {
                int value = _values[Math.Min(_index, _values.Length - 1)];
                _index++;
                return maxValue <= 0 ? 0 : value % maxValue;
            }
        }

        [Fact]
        public void MutationScalesWithTheGrounds()
        {
            Assert.Equal(4, BreedingTraits.MutationPercentFor(0));
            Assert.Equal(9, BreedingTraits.MutationPercentFor(5));
            Assert.Equal(14, BreedingTraits.MutationPercentFor(10));
        }

        [Fact]
        public void NewcomerChanceScalesWithTheInnAndStopsAtFifty()
        {
            Assert.Equal(20, BreedingTraits.NewcomerTraitPercentFor(0));
            Assert.Equal(35, BreedingTraits.NewcomerTraitPercentFor(5));
            Assert.Equal(50, BreedingTraits.NewcomerTraitPercentFor(10));
            Assert.Equal(50, BreedingTraits.NewcomerTraitPercentFor(20));
        }

        [Fact]
        public void OneParentPassesHalfTheTimeAndBothNinetyPercent()
        {
            var rng = new Random(20260913);
            long father = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);
            long mother = TraitRegistry.MaskOf(TraitRegistry.HawkEye);
            int iron = 0, hawk = 0;
            const int runs = 20_000;
            for (int i = 0; i < runs; i++)
            {
                long child = BreedingTraits.Inherit(father, mother, isRelated: false, isEpic: false, groundsLevel: 0, rng);
                if (TraitRegistry.Has(child, TraitRegistry.IronBlood)) iron++;
                if (TraitRegistry.Has(child, TraitRegistry.HawkEye)) hawk++;
            }
            Assert.InRange(iron * 100.0 / runs, 47.0, 53.0);
            Assert.InRange(hawk * 100.0 / runs, 87.0, 93.0);
        }

        [Fact]
        public void NoChildEverCarriesMoreThanThree()
        {
            var rng = new Random(7);
            long everything = TraitRegistry.KnownBitsMask;
            for (int i = 0; i < 5_000; i++)
            {
                long child = BreedingTraits.Inherit(everything, everything, isRelated: true, isEpic: true, groundsLevel: 10, rng);
                Assert.InRange(TraitRegistry.CountOf(child), 0, TraitRegistry.MaxTraitsPerCharacter);
            }
        }

        [Fact]
        public void TheCapKeepsFlawsFirstThenTheRarest()
        {
            // Every roll 0: every trait passes, the mutation fires and picks the
            // first Common not already present (Stout Heart).
            var rng = new SequenceRandom(0);
            long father = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.BloodOfKings, TraitRegistry.WolfsHunger, TraitRegistry.FaeTouched);
            long mother = TraitRegistry.MaskOf(TraitRegistry.ThinBlood);

            long child = BreedingTraits.Inherit(father, mother, isRelated: false, isEpic: false, groundsLevel: 0, rng);

            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.ThinBlood, TraitRegistry.BloodOfKings, TraitRegistry.WolfsHunger), child);
        }

        [Fact]
        public void NothingPassesWhenEveryRollFails()
        {
            var rng = new SequenceRandom(99);
            long parents = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);
            Assert.Equal(0L, BreedingTraits.Inherit(parents, parents, isRelated: false, isEpic: false, groundsLevel: 0, rng));
        }

        [Fact]
        public void ARelatedPairCanGiveAFlawAndAnEpicGivesARareOrBetter()
        {
            // 0 = related flaw fires (first flaw: Thin Blood); mutation roll 99 fails;
            // epic rarity roll 0 of 100 -> Rare pool, index 0 -> Iron Blood.
            var rng = new SequenceRandom(0, 0, 99, 0, 0);
            long child = BreedingTraits.Inherit(0L, 0L, isRelated: true, isEpic: true, groundsLevel: 0, rng);
            Assert.True(TraitRegistry.Has(child, TraitRegistry.ThinBlood));
            Assert.True(TraitRegistry.Has(child, TraitRegistry.IronBlood));

            var real = new Random(11);
            for (int i = 0; i < 2_000; i++)
            {
                long epicChild = BreedingTraits.Inherit(0L, 0L, isRelated: false, isEpic: true, groundsLevel: 0, real);
                Assert.Contains(TraitRegistry.BitsOf(epicChild), bit =>
                    TraitRegistry.TryGet(bit, out var def) && def.Rarity is TraitRarity.Rare or TraitRarity.Legendary);
            }
        }

        [Fact]
        public void NewcomersNeverBringALegendaryBelowInnSix()
        {
            var rng = new Random(3);
            for (int i = 0; i < 20_000; i++)
            {
                long mask = BreedingTraits.RollNewcomerTrait(5, rng);
                Assert.InRange(TraitRegistry.CountOf(mask), 0, 1);
                foreach (int bit in TraitRegistry.BitsOf(mask))
                {
                    Assert.True(TraitRegistry.TryGet(bit, out var def));
                    Assert.NotEqual(TraitRarity.Legendary, def.Rarity);
                }
            }
        }

        [Fact]
        public void NewcomerRollsFollowTheInn()
        {
            // has a trait (0 < 35), not a flaw (50 >= 15), rarity 99 at Inn 5 -> Rare, index 0 -> Iron Blood
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.IronBlood), BreedingTraits.RollNewcomerTrait(5, new SequenceRandom(0, 50, 99, 0)));
            // the same rolls at Inn 6 reach the Legendary band -> Blood of Kings
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.BloodOfKings), BreedingTraits.RollNewcomerTrait(6, new SequenceRandom(0, 50, 99, 0)));
            // flaw branch
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.ThinBlood), BreedingTraits.RollNewcomerTrait(0, new SequenceRandom(0, 0, 0)));
            // no trait at all
            Assert.Equal(0L, BreedingTraits.RollNewcomerTrait(0, new SequenceRandom(99)));
        }

        [Fact]
        public void PreviewOddsNameEachTraitAndItsSource()
        {
            long hero = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);
            long partner = TraitRegistry.MaskOf(TraitRegistry.HawkEye, TraitRegistry.ThinBlood);

            var odds = BreedingTraits.PreviewOdds(hero, partner).ToDictionary(o => o.Bit);

            Assert.Equal(3, odds.Count);
            Assert.Equal((50, "hero"), (odds[TraitRegistry.IronBlood].ChancePct, odds[TraitRegistry.IronBlood].Source));
            Assert.Equal((90, "both"), (odds[TraitRegistry.HawkEye].ChancePct, odds[TraitRegistry.HawkEye].Source));
            Assert.Equal((50, "partner"), (odds[TraitRegistry.ThinBlood].ChancePct, odds[TraitRegistry.ThinBlood].Source));
            Assert.Equal(60, BreedingTraits.FlawPercentFor(true));
            Assert.Equal(0, BreedingTraits.FlawPercentFor(false));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BreedingTraitsTests"`
Expected: build FAILS - `The name 'BreedingTraits' does not exist`.

- [ ] **Step 3: Write minimal implementation**

```csharp
// server/FolkIdle.Server/Engine/BreedingTraits.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Engine
{
    public readonly record struct TraitOdds(int Bit, int ChancePct, string Source);

    /// <summary>
    /// How traits pass from parents to a child, and what a newcomer brings.
    /// See docs/superpowers/specs/2026-09-13-breeding-traits-design.md section 4.
    ///
    /// PURE AND STATIC, and every roll comes from the Random passed in - so each
    /// rule is a millisecond test, and a test can script the dice exactly.
    /// </summary>
    public static class BreedingTraits
    {
        public const int SingleParentPassPercent = 50;
        public const int BothParentsPassPercent = 90;
        public const int RelatedFlawPercent = 60;
        public const int BaseMutationPercent = 4;
        public const int MutationPercentPerGroundsLevel = 1;
        public const int NewcomerBasePercent = 20;
        public const int NewcomerPercentPerInnLevel = 3;
        public const int NewcomerCapPercent = 50;
        public const int NewcomerFlawPercent = 15;
        public const int LegendaryInnLevel = 6;

        public static int MutationPercentFor(int groundsLevel)
            => BaseMutationPercent + Math.Max(0, groundsLevel) * MutationPercentPerGroundsLevel;

        public static int NewcomerTraitPercentFor(int innLevel)
            => Math.Min(NewcomerCapPercent, NewcomerBasePercent + Math.Max(0, innLevel) * NewcomerPercentPerInnLevel);

        public static int FlawPercentFor(bool isRelated) => isRelated ? RelatedFlawPercent : 0;

        /// <summary>
        /// A child's traits: inherit, then a related pair's flaw, then a mutation,
        /// then the epic guarantee, then the cap of three.
        /// </summary>
        public static long Inherit(long fatherMask, long motherMask, bool isRelated, bool isEpic, int groundsLevel, Random rng)
        {
            var candidates = new List<(int Bit, bool IsNew)>();

            foreach (int bit in TraitRegistry.BitsOf(fatherMask | motherMask))
            {
                bool both = TraitRegistry.Has(fatherMask, bit) && TraitRegistry.Has(motherMask, bit);
                if (rng.Next(100) < (both ? BothParentsPassPercent : SingleParentPassPercent))
                {
                    candidates.Add((bit, false));
                }
            }

            if (isRelated && rng.Next(100) < RelatedFlawPercent)
            {
                AddRandom(candidates, TraitRegistry.OfRarity(TraitRarity.Flaw), rng);
            }

            if (rng.Next(100) < MutationPercentFor(groundsLevel))
            {
                AddRandom(candidates, PoolByWeight(rng, common: 70, rare: 25, legendary: 5), rng);
            }

            if (isEpic)
            {
                AddRandom(candidates, PoolByWeight(rng, common: 0, rare: 80, legendary: 20), rng);
            }

            return Cap(candidates, rng);
        }

        /// <summary>Zero or one trait for somebody arriving at the Inn.</summary>
        public static long RollNewcomerTrait(int innLevel, Random rng)
        {
            if (rng.Next(100) >= NewcomerTraitPercentFor(innLevel)) return 0L;

            IReadOnlyList<TraitDefinition> pool = rng.Next(100) < NewcomerFlawPercent
                ? TraitRegistry.OfRarity(TraitRarity.Flaw)
                : innLevel >= LegendaryInnLevel
                    ? PoolByWeight(rng, common: 60, rare: 32, legendary: 8)
                    : PoolByWeight(rng, common: 75, rare: 25, legendary: 0);

            return 1L << pool[rng.Next(pool.Count)].Bit;
        }

        /// <summary>What each trait in the pair passes with - exact, not sampled.</summary>
        public static IReadOnlyList<TraitOdds> PreviewOdds(long heroMask, long partnerMask)
        {
            var odds = new List<TraitOdds>();
            foreach (int bit in TraitRegistry.BitsOf(heroMask | partnerMask))
            {
                bool hero = TraitRegistry.Has(heroMask, bit);
                bool partner = TraitRegistry.Has(partnerMask, bit);
                odds.Add(hero && partner
                    ? new TraitOdds(bit, BothParentsPassPercent, "both")
                    : new TraitOdds(bit, SingleParentPassPercent, hero ? "hero" : "partner"));
            }
            return odds;
        }

        private static IReadOnlyList<TraitDefinition> PoolByWeight(Random rng, int common, int rare, int legendary)
        {
            int roll = rng.Next(common + rare + legendary);
            TraitRarity rarity = roll < common ? TraitRarity.Common
                : roll < common + rare ? TraitRarity.Rare
                : TraitRarity.Legendary;
            return TraitRegistry.OfRarity(rarity);
        }

        private static void AddRandom(List<(int Bit, bool IsNew)> candidates, IReadOnlyList<TraitDefinition> pool, Random rng)
        {
            var open = pool.Where(t => !candidates.Any(c => c.Bit == t.Bit)).ToList();
            if (open.Count == 0) return;
            candidates.Add((open[rng.Next(open.Count)].Bit, true));
        }

        /// <summary>
        /// Flaws always stay - the way out of a flaw is breeding it out, never the
        /// cap. Then rarity; a NEW trait only displaces an inherited one it
        /// outranks, and inherited ties are a coin flip.
        /// </summary>
        private static long Cap(List<(int Bit, bool IsNew)> candidates, Random rng)
        {
            long mask = 0L;
            int count = 0;

            foreach (var c in candidates.Where(c => TraitRegistry.IsFlaw(c.Bit)))
            {
                mask |= 1L << c.Bit;
                count++;
            }

            var ranked = candidates
                .Where(c => !TraitRegistry.IsFlaw(c.Bit))
                .Select(c =>
                {
                    TraitRegistry.TryGet(c.Bit, out var def);
                    int key = (int)def.Rarity * 4 + (c.IsNew ? 0 : 2) + rng.Next(2);
                    return (c.Bit, Key: key);
                })
                .ToList()
                .OrderByDescending(c => c.Key);

            foreach (var c in ranked)
            {
                if (count >= TraitRegistry.MaxTraitsPerCharacter) break;
                mask |= 1L << c.Bit;
                count++;
            }

            return mask;
        }
    }
}
```

Note on `TheCapKeepsFlawsFirstThenTheRarest`: with every roll 0 the coin-flip term is 0 for all, so Legendary ties keep candidate order (bit ascending) - Blood of Kings (8), Wolf's Hunger (9) - because `OrderByDescending` is stable.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BreedingTraitsTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

Subject: `feat(breeding): trait inheritance, mutation and newcomer rules`.
```bash
git add server/FolkIdle.Server/Engine/BreedingTraits.cs server/FolkIdle.Server.Tests/BreedingTraitsTests.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 4: Persist the masks and cache the active character's

**Files:**
- Modify: `server/FolkIdle.Server/Models/CharacterLineageRegistry.cs` (after `IsInbred`, line ~38)
- Modify: `server/FolkIdle.Server/Models/VillageNewcomer.cs` (after `IsElder`, line ~61)
- Create: migration `AddBreedingTraits` (generated)
- Modify: `server/FolkIdle.Server/Engine/TickStatePayload.cs:113` (add field)
- Modify: `server/FolkIdle.Server/Domain/Shared/StateCheckpointManager.cs:1161-1170`
- Test: `server/FolkIdle.Server.Tests/BreedingTraitsIntegrationTests.cs` (create)

**Interfaces:**
- Produces: `CharacterLineageRegistry.TraitMask` (long), `VillageNewcomer.TraitMask` (long), `TickStatePayload.TraitMask` (long).

- [ ] **Step 1: Write the failing test**

```csharp
// server/FolkIdle.Server.Tests/BreedingTraitsIntegrationTests.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Domain.Progression;

namespace FolkIdle.Server.Tests
{
    [Collection("Postgres collection")]
    public class BreedingTraitsIntegrationTests
    {
        private readonly PostgresTestFixture _fixture;

        public BreedingTraitsIntegrationTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static long HumanGenome()
        {
            var genome = new GeneticVector(0);
            genome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            return genome.RawValue;
        }

        [Fact]
        public async Task TraitMasksRoundTrip()
        {
            const long playerId = 970013201L;
            var characterId = Guid.NewGuid();
            long newcomerId;
            long mask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.ThinBlood);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.Add(new CharacterRecord { Id = characterId, PlayerId = playerId, AgePhase = 1 });
                db.CharacterLineages.Add(new CharacterLineageRegistry { CharacterId = characterId, GeneticVector = HumanGenome(), TraitMask = mask });
                var newcomer = new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, TraitMask = TraitRegistry.MaskOf(TraitRegistry.HawkEye) };
                db.VillageNewcomers.Add(newcomer);
                await db.SaveChangesAsync();
                newcomerId = newcomer.Id;
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(mask, (await verify.CharacterLineages.AsNoTracking().SingleAsync(l => l.CharacterId == characterId)).TraitMask);
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.HawkEye), (await verify.VillageNewcomers.AsNoTracking().SingleAsync(v => v.Id == newcomerId)).TraitMask);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BreedingTraitsIntegrationTests"`
Expected: build FAILS - `'CharacterLineageRegistry' does not contain a definition for 'TraitMask'`.

- [ ] **Step 3: Add the properties**

In `CharacterLineageRegistry.cs`, directly after `public bool IsInbred { get; set; }`:

```csharp

        // Modul: HERITABLE TRAITS, one bit each - see TraitRegistry for the
        // table, which is a persisted format and never renumbered. Zero for
        // every character that existed before traits; nothing is backfilled.
        public long TraitMask { get; set; }
```

In `VillageNewcomer.cs`, directly after `public bool IsElder { get; set; }`:

```csharp

        /// <summary>
        /// Zero or one trait, rolled on arrival against the Inn - see
        /// BreedingTraits.RollNewcomerTrait. It passes to a child like any
        /// parent's, which is what makes outside blood a source of traits.
        /// </summary>
        public long TraitMask { get; set; }
```

- [ ] **Step 4: Generate the migration**

Stop the server (separate command). Then:
```powershell
dotnet ef --version
# if that fails: dotnet tool install --global dotnet-ef --version 8.*
dotnet ef migrations add AddBreedingTraits --project server/FolkIdle.Server/FolkIdle.Server.csproj
```
Open the generated `Migrations/*_AddBreedingTraits.cs` and confirm `Up` is exactly two `AddColumn<long>` calls - `TraitMask`, type `bigint`, `nullable: false`, `defaultValue: 0L` - on `character_lineage_registry` and `village_newcomers`, and `Down` drops both. Add a class summary: `/// Adds TraitMask to lineages and newcomers. Additive: every existing row gets 0, which means "no traits" - the intended state for characters that predate traits.`

- [ ] **Step 5: Cache the active character's mask**

In `TickStatePayload.cs`, directly after `public bool IsEpicMutation;`:

```csharp
        // Modul: the active character's trait bits, hydrated beside the
        // aptitudes and read through TraitTotals.From on the tick - never from
        // the database on the hot path.
        public long TraitMask;
```

In `StateCheckpointManager.cs`, inside `if (slot1Lineage != null) { ... }` (line ~1162), add after `payload.IsInbred = slot1Lineage.IsInbred;`:

```csharp
                    payload.TraitMask = slot1Lineage.TraitMask;
```

- [ ] **Step 6: Run tests and apply the migration locally**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BreedingTraitsIntegrationTests"`
Expected: PASS.
Then:
```powershell
$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj -- --migrate
```
Expected: `Database migrations applied successfully.`

- [ ] **Step 7: Commit**

Subject: `feat(breeding): persist trait masks on lineages and newcomers`.
```bash
git add server/FolkIdle.Server/Models/CharacterLineageRegistry.cs server/FolkIdle.Server/Models/VillageNewcomer.cs server/FolkIdle.Server/Migrations server/FolkIdle.Server/Engine/TickStatePayload.cs server/FolkIdle.Server/Domain/Shared/StateCheckpointManager.cs server/FolkIdle.Server.Tests/BreedingTraitsIntegrationTests.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 5: StatsCalculator reads traits instead of genes

**Files:**
- Modify: `server/FolkIdle.Server/Engine/StatsCalculator.cs:111` (signature) and `:316-323`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs` (lines 3613, 4983-4989, 5497, 5633)
- Modify: `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs` (lines 484, 539)
- Modify: `server/FolkIdle.Server/Domain/Combat/EquipmentSlotEngine.cs:754-776`
- Modify: `server/FolkIdle.Server/Engine/GuildWarSnapshotEngine.cs:171-237`
- Modify tests: `HardenedEngineIntegrationTests.cs:1160-1168`, `GatheringShareTests.cs:321-323`, `ProgressionRateTests.cs:273, 361, 400, 549`

**Interfaces:**
- Consumes: `TraitTotals` (Task 2), `TickStatePayload.TraitMask` (Task 4).
- Produces: `StatsCalculator.Calculate(int str, int dex, int con, int lck, int activeOffensivePotionId = 0, int activeDefensivePotionId = 0, int activeAgePhase = 1, int completedAreaFlags = 0, int activeRaceId = 0, int humanMastery = 0, int vilaMastery = 0, int draugrMastery = 0, EquippedAffixTotals equippedAffixTotals = default, bool isEpicMutation = false, TraitTotals traits = default, EquippedSetIds equippedSetIds = default)`.

- [ ] **Step 1: Write the failing test** - replace `Test_StatsCalculator_GeneticLociScaleCritAndAttackSpeed` in `HardenedEngineIntegrationTests.cs` (lines 1160-1168) with:

```csharp
        [Fact]
        public void Test_StatsCalculator_TraitsReachCombatStats()
        {
            // Modul: genes were replaced by traits (2026-09-13). The same five
            // combat stats the genes used to touch - and three they never did -
            // must move by exactly the trait's value.
            CombatStats baseline = StatsCalculator.Calculate(str: 50, dex: 50, con: 50, lck: 50);
            var traits = TraitTotals.From(TraitRegistry.MaskOf(
                TraitRegistry.HawkEye, TraitRegistry.SwiftBlood, TraitRegistry.Nimble));
            CombatStats withTraits = StatsCalculator.Calculate(str: 50, dex: 50, con: 50, lck: 50, traits: traits);

            Assert.Equal(baseline.CritChancePct + 3f, withTraits.CritChancePct, 3);
            Assert.Equal(baseline.AttackSpeedPct + 6f, withTraits.AttackSpeedPct, 3);
            Assert.Equal(baseline.DodgeChancePct + 4f, withTraits.DodgeChancePct, 3);

            var legendary = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.WolfsHunger, TraitRegistry.FaeTouched));
            CombatStats withLegendary = StatsCalculator.Calculate(str: 50, dex: 50, con: 50, lck: 50, traits: legendary);
            Assert.Equal(baseline.LifestealPct + 3f, withLegendary.LifestealPct, 3);
            Assert.Equal(baseline.RarityElevationPct + 4f, withLegendary.RarityElevationPct, 3);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_StatsCalculator_TraitsReachCombatStats"`
Expected: build FAILS - `The best overload for 'Calculate' does not have a parameter named 'traits'`.

- [ ] **Step 3: Change the signature and the application**

`StatsCalculator.cs` line 111: replace `bool isEpicMutation = false, int locusSpeed = 0, int locusCrit = 0, EquippedSetIds equippedSetIds = default)` with `bool isEpicMutation = false, TraitTotals traits = default, EquippedSetIds equippedSetIds = default)`.

Replace lines 316-323 (the `Modul 13.4.3: inherited genetic loci` comment and the two `+=` lines) with:

```csharp
            // Modul: HERITABLE TRAITS, which replaced the Speed and Crit genes on
            // 2026-09-13. Points and percents on the same 0-100 scale as the
            // stats they join, in the same additive block as equipped gear and
            // before the age-phase falloff below. Already capped by TraitTotals.
            stats.CritChancePct += traits.CritChancePoints;
            stats.AttackSpeedPct += traits.AttackSpeedPct;
            stats.DodgeChancePct += traits.DodgePoints;
            stats.LifestealPct += traits.LifestealPct;
            stats.RarityElevationPct += traits.RarityElevationPoints;
```

- [ ] **Step 4: Update every caller**

`SimulationEngine.cs`: replace ALL occurrences of `payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds` with `payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds` (lines 3613, 5497, 5633). Then replace, at lines 4988-4989,
```csharp
                    payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation,
                    payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds);
```
with
```csharp
                    payload.DraugrMasteryLevel, payload.CachedAffixTotals, payload.IsEpicMutation,
                    TraitTotals.From(payload.TraitMask), payload.CachedSetIds);
```

`OfflineSimulationEngine.cs`: replace ALL occurrences of `payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit, payload.CachedSetIds` with `payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds` (lines 484, 539).

`EquipmentSlotEngine.cs` lines 756-776: replace
```csharp
            bool isEpicMutation = false;
            int locusSpeed = 0;
            int locusCrit = 0;

            if (character.Lineage != null)
            {
                activeRaceId = (int)(character.Lineage.GeneticVector & 0xFF);
                isEpicMutation = character.Lineage.IsEpicMutation;
                var geneVec = new GeneticVector(character.Lineage.GeneticVector);
                locusSpeed = geneVec.LocusSpeed.Dominant;
                locusCrit = geneVec.LocusCrit.Dominant;
            }
```
with
```csharp
            bool isEpicMutation = false;
            TraitTotals traits = default;

            if (character.Lineage != null)
            {
                activeRaceId = (int)(character.Lineage.GeneticVector & 0xFF);
                isEpicMutation = character.Lineage.IsEpicMutation;
                traits = TraitTotals.From(character.Lineage.TraitMask);
            }
```
and `totals, isEpicMutation, locusSpeed, locusCrit, setIds);` with `totals, isEpicMutation, traits, setIds);`.

`GuildWarSnapshotEngine.cs` lines 173-187: replace
```csharp
            bool isEpicMutation = false;
            int locusSpeed = 0;
            int locusCrit = 0;
```
with
```csharp
            bool isEpicMutation = false;
            TraitTotals traits = default;
```
and inside `if (character.Lineage != null)` replace
```csharp
                    var geneVec = new GeneticVector(character.Lineage.GeneticVector);
                    locusSpeed = geneVec.LocusSpeed.Dominant;
                    locusCrit = geneVec.LocusCrit.Dominant;
```
with
```csharp
                    traits = TraitTotals.From(character.Lineage.TraitMask);
```
and line 237 `isEpicMutation, locusSpeed, locusCrit, equippedSetIds);` with `isEpicMutation, traits, equippedSetIds);`.

Tests: in `GatheringShareTests.cs` line 323 and `ProgressionRateTests.cs` lines 273, 361, 400, 549 replace `false, 0, 0, ` with `false, default, `.

- [ ] **Step 5: Verify no gene reads remain in callers**

Run: `rg -n "LocusSpeed|LocusCrit|locusSpeed|locusCrit" server/FolkIdle.Server --glob "!Migrations/**"`
Expected: only `GeneticSplicingEngine.cs`, `ContentRegistry.cs`, `NetworkBroadcastSystem.cs` (roster/preview, handled in Task 7), `StateCheckpointManager.cs:1165-1166`, `TickStatePayload.cs` (handled in Task 7). None in `StatsCalculator`, `SimulationEngine`, `OfflineSimulationEngine`, `EquipmentSlotEngine`, `GuildWarSnapshotEngine`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~StatsCalculator|FullyQualifiedName~GatheringShareTests|FullyQualifiedName~ProgressionRateTests|FullyQualifiedName~PowerCeilingTests|FullyQualifiedName~AttackSpeedTests|FullyQualifiedName~AttributeSystemTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

Subject: `feat(breeding): combat stats read traits instead of the Speed and Crit genes`.
```bash
git add -u server/
git commit -F <path-to>/commit-msg.txt
```

---

### Task 6: One bloodline bonus path for live and offline (fixes offline Strength)

**Files:**
- Create: `server/FolkIdle.Server/Engine/BloodlineBonuses.cs`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs` (5085-5091, 5470-5473, 5536-5540, 5650-5656)
- Modify: `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs` (456-461, 472-487, 552, 599-604)
- Test: `server/FolkIdle.Server.Tests/BloodlineBonusesTests.cs`

**Interfaces:**
- Consumes: `BreedingAptitudes.BonusPercentFor`, `TraitTotals`.
- Produces: `static long BloodlineBonuses.ApplyAttack(long milliAttack, int strengthAptitude, in TraitTotals traits)`, `static long ApplyMaxHp(long milliHp, int enduranceAptitude, in TraitTotals traits)`, `static int GatherSpeedBonusPct(int skillAptitude, in TraitTotals traits)`, `static int GatherYieldBonusPct(in TraitTotals traits)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// server/FolkIdle.Server.Tests/BloodlineBonusesTests.cs
using System.IO;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: ONE FORMULA PER STAT, used by the live tick AND the offline
    /// projection. Offline combat never applied the Strength aptitude at all
    /// (found 2026-09-13) - SimulationEngine added it after
    /// ComputeEffectiveMilliAttack and OfflineSimulationEngine stopped there, so
    /// a bred line killed more slowly away than online. The third instance of
    /// "three paths grow a level" (CLAUDE.md).
    /// </summary>
    public class BloodlineBonusesTests
    {
        [Fact]
        public void AttackAddsTheAptitudeThenTheTrait()
        {
            Assert.Equal(100_000L, BloodlineBonuses.ApplyAttack(100_000L, 0, default));
            // Strength 20 is +30% (BreedingAptitudes band one)
            Assert.Equal(130_000L, BloodlineBonuses.ApplyAttack(100_000L, 20, default));
            var blood = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.BloodOfKings));
            Assert.Equal(143_000L, BloodlineBonuses.ApplyAttack(100_000L, 20, blood));
            var faint = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.FaintHeart));
            Assert.Equal(95_000L, BloodlineBonuses.ApplyAttack(100_000L, 0, faint));
        }

        [Fact]
        public void HealthAddsTheAptitudeThenTheTrait()
        {
            var iron = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.IronBlood));
            Assert.Equal(140_400L, BloodlineBonuses.ApplyMaxHp(100_000L, 20, iron));
            var thin = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.ThinBlood));
            Assert.Equal(94_000L, BloodlineBonuses.ApplyMaxHp(100_000L, 0, thin));
        }

        [Fact]
        public void GatheringSumsAptitudeAndTraits()
        {
            var quick = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.QuickHands, TraitRegistry.GreenThumb));
            Assert.Equal(35, BloodlineBonuses.GatherSpeedBonusPct(20, quick));
            Assert.Equal(5, BloodlineBonuses.GatherYieldBonusPct(quick));
            Assert.Equal(0, BloodlineBonuses.GatherYieldBonusPct(default));
        }

        [Fact]
        public void LiveAndOfflineBothUseTheSharedFormulas()
        {
            foreach (string path in new[] { "Domain/Combat/SimulationEngine.cs", "Engine/OfflineSimulationEngine.cs" })
            {
                string source = SourceOf(path);
                Assert.Contains("BloodlineBonuses.ApplyAttack(", source);
                Assert.Contains("BloodlineBonuses.ApplyMaxHp(", source);
                Assert.Contains("BloodlineBonuses.GatherSpeedBonusPct(", source);
                Assert.Contains("BloodlineBonuses.GatherYieldBonusPct(", source);
                Assert.DoesNotContain("BonusPercentFor(payload.Aptitude_", source);
                Assert.DoesNotContain("LocusYield", source);
            }
        }

        private static string SourceOf(string relativePath)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            string full = Path.Combine(dir!.FullName, "server", "FolkIdle.Server", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"{full} not found");
            return File.ReadAllText(full);
        }
    }
}
```

(ApplyMaxHp check: 100,000 + 30% = 130,000; + 8% of 130,000 = 10,400 -> 140,400.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BloodlineBonusesTests"`
Expected: build FAILS - `The name 'BloodlineBonuses' does not exist`.

- [ ] **Step 3: Write the shared class**

```csharp
// server/FolkIdle.Server/Engine/BloodlineBonuses.cs
using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a bloodline adds to a character's attack, health and gathering -
    /// aptitudes first, then traits - in ONE place for the live tick and the
    /// offline projection.
    ///
    /// Modul: these were hand-written twice, and the copies had drifted: the
    /// offline projection never applied the Strength aptitude at all. Both
    /// engines call this now, and BloodlineBonusesTests fails if either stops.
    /// </summary>
    public static class BloodlineBonuses
    {
        public static long ApplyAttack(long milliAttack, int strengthAptitude, in TraitTotals traits)
        {
            long effective = milliAttack;
            if (strengthAptitude > 0)
            {
                effective += (long)(effective * BreedingAptitudes.BonusPercentFor(strengthAptitude) / 100f);
            }
            if (traits.AttackPct != 0)
            {
                effective += effective * traits.AttackPct / 100;
            }
            return Math.Max(0L, effective);
        }

        public static long ApplyMaxHp(long milliHp, int enduranceAptitude, in TraitTotals traits)
        {
            long effective = milliHp;
            effective += (long)(effective * BreedingAptitudes.BonusPercentFor(enduranceAptitude) / 100f);
            if (traits.MaxHpPct != 0)
            {
                effective += effective * traits.MaxHpPct / 100;
            }
            return Math.Max(1L, effective);
        }

        public static int GatherSpeedBonusPct(int skillAptitude, in TraitTotals traits)
            => (int)BreedingAptitudes.BonusPercentFor(skillAptitude) + traits.GatherSpeedPct;

        public static int GatherYieldBonusPct(in TraitTotals traits) => traits.GatherYieldPct;
    }
}
```

- [ ] **Step 4: Wire the live tick**

`SimulationEngine.cs` lines 5085-5091 - replace
```csharp
            // Modul: Strength, the bloodline's combat aptitude. On the
            // pre-armour figure, alongside the inheritance bonus that
            // ComputeEffectiveMilliAttack already folded in.
            if (payload.Aptitude_Strength > 0)
            {
                effective += (long)(effective * BreedingAptitudes.BonusPercentFor(payload.Aptitude_Strength) / 100f);
            }
```
with
```csharp
            // Modul: the bloodline - the Strength aptitude, then attack traits -
            // on the pre-armour figure. Shared with the offline projection, which
            // used to skip Strength entirely: see BloodlineBonuses.
            effective = BloodlineBonuses.ApplyAttack(effective, payload.Aptitude_Strength, TraitTotals.From(payload.TraitMask));
```

Line 5473 - replace `+ (int)BreedingAptitudes.BonusPercentFor(payload.Aptitude_Skill));` with `+ BloodlineBonuses.GatherSpeedBonusPct(payload.Aptitude_Skill, TraitTotals.From(payload.TraitMask)));`

Lines 5536-5540 - replace
```csharp
                        // Modul 13.4.3: LocusYield (bred genetic trait, see
                        // GeneticSplicingEngine/BreedingEngine) adds +4 percentage
                        // points of extra harvest roll count per point, same units
                        // as the race-mastery bonuses above.
                        additionalYieldBonus += payload.LocusYield * 4;
```
with
```csharp
                        // Modul: yield traits, which replaced the Yield gene on
                        // 2026-09-13 - percentage points of extra harvest rolls, the
                        // same units as the race-mastery bonuses above.
                        additionalYieldBonus += BloodlineBonuses.GatherYieldBonusPct(TraitTotals.From(payload.TraitMask));
```

Line ~5547, in the comment directly below - replace `// by monolith/race/event/LocusYield bonuses; luck` with `// by monolith/race/event/trait bonuses; luck`. (The source test in Step 1 rejects the word `LocusYield` anywhere in the file, comments included.)

Lines 5650-5656 - replace
```csharp
            // Modul: Endurance, the bloodline's health aptitude. Layered the
            // same additive-percent way as inheritance and the tree above it,
            // and diminishing at the high end - see BreedingAptitudes for why
            // a flat rate to a cap of fifty would make the leaderboard a
            // function of account age.
            effectiveMilliHp += (long)(effectiveMilliHp
                * BreedingAptitudes.BonusPercentFor(payload.Aptitude_Endurance) / 100f);
```
with
```csharp
            // Modul: the bloodline's health - Endurance, then health traits -
            // layered the same additive-percent way as inheritance and the tree
            // above. Shared with the offline projection: see BloodlineBonuses.
            effectiveMilliHp = BloodlineBonuses.ApplyMaxHp(effectiveMilliHp, payload.Aptitude_Endurance, TraitTotals.From(payload.TraitMask));
```

- [ ] **Step 5: Wire the offline projection**

`OfflineSimulationEngine.cs` line 461 - replace `+ (int)BreedingAptitudes.BonusPercentFor(payload.Aptitude_Skill));` with `+ BloodlineBonuses.GatherSpeedBonusPct(payload.Aptitude_Skill, TraitTotals.From(payload.TraitMask)));`

Lines 472-476 - replace the comment's first line `// Modul: LocusYield (+4% harvest rolls per point) still scales roll` with `// Modul: yield traits (BloodlineBonuses) still scale roll`.

Lines 485-487 - replace
```csharp
            double locusYieldFactor = 1.0 + (payload.LocusYield * 0.04);

            int lootRolls = (int)(allowedActions * payload.CachedCodexYieldMultiplier * locusYieldFactor);
```
with
```csharp
            double traitYieldFactor = 1.0 + BloodlineBonuses.GatherYieldBonusPct(TraitTotals.From(payload.TraitMask)) / 100.0;

            int lootRolls = (int)(allowedActions * payload.CachedCodexYieldMultiplier * traitYieldFactor);
```

Line 552 - after
```csharp
            long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in combatStats, lineage.DamageScalePerLevelPct, payload.CurrentLevel, InheritanceRegistry.GetBonusPct(payload.Inherit_Damage));
```
insert
```csharp
            // Modul: THE STRENGTH APTITUDE WAS MISSING HERE until 2026-09-13 -
            // the live tick added it and this projection did not, so a bred line
            // killed more slowly while away. Attack traits ride along.
            effectiveMilliAttack = BloodlineBonuses.ApplyAttack(effectiveMilliAttack, payload.Aptitude_Strength, TraitTotals.From(payload.TraitMask));
```

Lines 599-604 - replace
```csharp
            // Modul: Endurance offline as well as live. The two health formulas
            // diverging is a bug this codebase has already shipped once, in
            // gathering - the offline path used a stale private formula for
            // months while the live one moved on.
            effectiveMilliHp += (long)(effectiveMilliHp
                * BreedingAptitudes.BonusPercentFor(payload.Aptitude_Endurance) / 100f);
```
with
```csharp
            // Modul: the same bloodline health formula as the live tick - see
            // BloodlineBonuses. The two health formulas diverging is a bug this
            // codebase has already shipped once, in gathering.
            effectiveMilliHp = BloodlineBonuses.ApplyMaxHp(effectiveMilliHp, payload.Aptitude_Endurance, TraitTotals.From(payload.TraitMask));
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BloodlineBonusesTests|FullyQualifiedName~ProgressionRateTests|FullyQualifiedName~GatheringShareTests|FullyQualifiedName~GatheringEconomyTests|FullyQualifiedName~AttributeGrowthTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

Subject: `fix(combat): offline kills apply the Strength aptitude, through one bloodline formula`.
```bash
git add server/FolkIdle.Server/Engine/BloodlineBonuses.cs server/FolkIdle.Server.Tests/BloodlineBonusesTests.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 7: Retire the gene consumers and the hidden inbreeding penalty

**Files:**
- Modify: `server/FolkIdle.Server/Engine/RaceAttributeGrowth.cs:47-62`
- Modify: `server/FolkIdle.Server/Engine/TickStatePayload.cs:108-121`
- Modify: `server/FolkIdle.Server/Domain/Shared/StateCheckpointManager.cs:1157-1171`
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (roster response 4277-4282 and 4704-4709; preview loci 4866-4868 and 4959-4961)
- Test: `server/FolkIdle.Server.Tests/BloodlineBonusesTests.cs` (append)

- [ ] **Step 1: Write the failing test** (append inside `BloodlineBonusesTests`)

```csharp
        [Fact]
        public void GrowthNoLongerReadsGenesOrTheHiddenInbreedingPenalty()
        {
            string growth = SourceOf("Engine/RaceAttributeGrowth.cs");
            Assert.DoesNotContain("LocusSpeed", growth);
            Assert.DoesNotContain("IsInbred", growth);

            string payload = SourceOf("Engine/TickStatePayload.cs");
            Assert.DoesNotContain("public int LocusSpeed", payload);
            Assert.DoesNotContain("public bool IsInbred", payload);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~GrowthNoLongerReadsGenes"`
Expected: FAIL - `Assert.DoesNotContain() Failure: Sub-string found ... LocusSpeed`.

- [ ] **Step 3: Remove the growth terms**

`RaceAttributeGrowth.cs` lines 47-62 - replace from `// Modul 13.4.3: an Epic-mutated lineage grants +5% growth per level,` through `if (payload.IsInbred) geneticMultiplier *= 0.75f;` with:

```csharp
            // Modul 13.4.3: an Epic-mutated lineage grants +5% growth per level,
            // matching StatsCalculator's flat attribute bonus.
            //
            // Modul: TWO TERMS WENT, 2026-09-13. The Speed/Crit/Yield genes that
            // added a sliver here were replaced by traits, and the -25% for an
            // inbred lineage was a lifetime penalty no screen ever mentioned - a
            // related pair now shows a 60% chance of a visible flaw instead
            // (BreedingTraits).
            float geneticMultiplier = payload.IsEpicMutation ? 1.05f : 1.0f;
```

- [ ] **Step 4: Remove the payload fields and their hydration**

`TickStatePayload.cs` - replace lines 108-121 (from `// Modul 13.4.3: cached inherited genetic loci` through `public bool IsInbred;`) with:

```csharp
        // Modul 13.4.3: cached lineage flags, hydrated at login from the active
        // character's CharacterLineages row (see StateCheckpointManager.
        // LoadPlayerState). The Speed/Crit/Yield loci and IsInbred that lived
        // here were retired on 2026-09-13 - traits replaced both.
        public bool IsEpicMutation;
```
(`TraitMask`, added in Task 4 directly after `IsEpicMutation`, stays.)

`StateCheckpointManager.cs` lines 1161-1170 - replace
```csharp
                var slot1Lineage = characters[0].Lineage;
                if (slot1Lineage != null)
                {
                    var slot1GeneVec = new GeneticVector(slot1Lineage.GeneticVector);
                    payload.LocusSpeed = slot1GeneVec.LocusSpeed.Dominant;
                    payload.LocusCrit = slot1GeneVec.LocusCrit.Dominant;
                    payload.LocusYield = slot1GeneVec.LocusYield.Dominant;
                    payload.IsEpicMutation = slot1Lineage.IsEpicMutation;
                    payload.IsInbred = slot1Lineage.IsInbred;
                    payload.TraitMask = slot1Lineage.TraitMask;
                }
```
with
```csharp
                var slot1Lineage = characters[0].Lineage;
                if (slot1Lineage != null)
                {
                    payload.IsEpicMutation = slot1Lineage.IsEpicMutation;
                    payload.TraitMask = slot1Lineage.TraitMask;
                }
```
Update the comment above it (`// Modul 13.4.3: inherited genetic loci for the active (Slot1)`) to `// Modul 13.4.3: lineage flags and traits for the active (Slot1)`.

- [ ] **Step 5: Stop sending the retired loci**

`NetworkBroadcastSystem.cs`:
- In `BreedingRosterEntryResponse` delete the six properties `LocusSpeedDominant` ... `LocusYieldRecessive` (lines 4277-4282) and add `public long TraitMask { get; set; }` in their place.
- In `HandleBreedingRosterSnapshot` replace the six `LocusSpeed... = geneVec...` assignments (lines 4704-4709) with `TraitMask = lineage.TraitMask` (keep the comma on the preceding `LocusRaceRecessive` line).
- In `HandleBreedingPreview` delete the three lines `AddLocusPreview(response.Loci, "Speed"|"Crit"|"Yield", ...)` (4866-4868); keep the `"Race"` line.
- In `HandleVillagerBreedingPreview` delete the same three lines (4959-4961).

- [ ] **Step 6: Build and run the affected tests**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BloodlineBonusesTests|FullyQualifiedName~AttributeGrowthTests|FullyQualifiedName~Breeding|FullyQualifiedName~StateUpdatePacketFieldCoverageTests"`
Expected: PASS. If the build reports another reader of a removed field, route it through `TraitTotals.From(payload.TraitMask)` or delete it - do not re-add the field.

- [ ] **Step 7: Commit**

Subject: `refactor(breeding): retire the gene effects and the hidden -25% inbreeding growth`.
```bash
git add -u server/
git commit -F <path-to>/commit-msg.txt
```

---

### Task 8: Births and arrivals roll traits

**Files:**
- Modify: `server/FolkIdle.Server/Engine/BreedingEngine.cs` (roster pairing after `childAptitudes`; villager pairing after `childAptitudes`)
- Modify: `server/FolkIdle.Server/Engine/VillageArrivalEngine.cs:204`
- Test: `server/FolkIdle.Server.Tests/BreedingTraitsIntegrationTests.cs` (append)

**Interfaces:**
- Consumes: `BreedingTraits.Inherit`, `BreedingTraits.RollNewcomerTrait` (Task 3); `TraitMask` columns (Task 4).

- [ ] **Step 1: Write the failing tests** (append inside `BreedingTraitsIntegrationTests`)

```csharp
        [Fact]
        public async Task ABirthStoresATraitMaskDrawnFromItsParents()
        {
            const long playerId = 970013202L;
            var father = Guid.NewGuid();
            var mother = Guid.NewGuid();
            long fatherMask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.BloodOfKings);
            long motherMask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 1 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 10_000L });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = father, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, IsFemale = false },
                    new CharacterRecord { Id = mother, PlayerId = playerId, AgePhase = 1, SlotIndex = 1, IsFemale = true });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = father, GeneticVector = HumanGenome(), TraitMask = fatherMask },
                    new CharacterLineageRegistry { CharacterId = mother, GeneticVector = HumanGenome(), TraitMask = motherMask });
                await db.SaveChangesAsync();
            }

            var engine = new BreedingEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            await engine.ExecuteBreedingAsync(playerId, father, mother);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var child = await verify.CharacterLineages.AsNoTracking()
                .SingleAsync(l => l.ParentPaternalId == father && l.ParentMaternalId == mother);

            Assert.Equal(0L, child.TraitMask & ~TraitRegistry.KnownBitsMask);
            Assert.InRange(TraitRegistry.CountOf(child.TraitMask), 0, TraitRegistry.MaxTraitsPerCharacter);
            // Anything outside the parents' traits can only have come from the
            // mutation or epic roll, and those add non-flaw traits only.
            foreach (int bit in TraitRegistry.BitsOf(child.TraitMask & ~(fatherMask | motherMask)))
            {
                Assert.False(TraitRegistry.IsFlaw(bit));
            }
        }

        [Fact]
        public async Task ANewcomerArrivesWithAtMostOneTrait()
        {
            const long playerId = 970013203L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000L });
                await db.SaveChangesAsync();
            }

            for (int i = 0; i < 5; i++)
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                var player = await db.PlayerRecords.SingleAsync(p => p.Id == playerId);
                Assert.Null(await VillageArrivalEngine.RecruitAsync(db, player, innLevel: 10, nowEpoch: 1_800_000_000L + i));
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var masks = await verify.VillageNewcomers.AsNoTracking().Where(v => v.PlayerId == playerId).Select(v => v.TraitMask).ToListAsync();
            Assert.Equal(5, masks.Count);
            Assert.All(masks, m => Assert.InRange(TraitRegistry.CountOf(m), 0, 1));
        }
```

- [ ] **Step 2: Confirm the engines do not write traits yet**

These two tests assert invariants (a mask of 0 satisfies them), so they cannot fail on their own before the change - the proof that the change is needed is that nothing writes the column yet.
Run: `rg -n "TraitMask" server/FolkIdle.Server/Engine/BreedingEngine.cs server/FolkIdle.Server/Engine/VillageArrivalEngine.cs`
Expected: no matches.
Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BreedingTraitsIntegrationTests"`
Expected: PASS (they compile and the invariants hold for an all-zero mask). Step 5 re-runs them against engines that do write traits, and the `rg` check there must then show the writes.

- [ ] **Step 3: Roll traits at birth**

`BreedingEngine.cs`, roster pairing - directly after the `int[] childAptitudes = BreedingAptitudes.Breed(...)` statement (the one using `pLineage.AptitudeVector()`), insert:

```csharp

                // Modul: TRAITS, from the same pairing and the same dice - see
                // BreedingTraits. A related pair risks a visible flaw here, which
                // replaced the -25% growth penalty nobody could see.
                long childTraits = BreedingTraits.Inherit(
                    pLineage.TraitMask, mLineage.TraitMask, isInbred, isEpicMutation, breedingLevel, Random.Shared);
```
and in the `newLineage` initializer of that method add `TraitMask = childTraits,` after `IsInbred = isInbred`. (Add a comma after `IsInbred = isInbred`.)

Villager pairing - directly after the `int[] childAptitudes = heroIsFather ? ... : ...;` statement, insert:

```csharp

                long childTraits = heroIsFather
                    ? BreedingTraits.Inherit(heroLineage.TraitMask, newcomer.TraitMask, isInbred, isEpicMutation, breedingLevel, Random.Shared)
                    : BreedingTraits.Inherit(newcomer.TraitMask, heroLineage.TraitMask, isInbred, isEpicMutation, breedingLevel, Random.Shared);
```
and add `TraitMask = childTraits,` to that method's `newLineage` initializer after `IsInbred = isInbred`.

- [ ] **Step 4: Roll traits on arrival**

`VillageArrivalEngine.cs` line 204 - after `newcomer.SetAptitudeVector(BreedingAptitudes.RollVillager(innLevel, Random.Shared));` insert:

```csharp
            // Modul: outside blood brings traits as well as numbers - a better
            // Inn, a better chance and a rarer trait. See BreedingTraits.
            newcomer.TraitMask = BreedingTraits.RollNewcomerTrait(innLevel, Random.Shared);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~BreedingTraitsIntegrationTests|FullyQualifiedName~BreedingRoundOneTests|FullyQualifiedName~Test_HeroVillager|FullyQualifiedName~Test_Breeding|FullyQualifiedName~Test_VillageArrival"`
Expected: PASS; `rg -n "TraitMask" server/FolkIdle.Server/Engine/BreedingEngine.cs server/FolkIdle.Server/Engine/VillageArrivalEngine.cs` now shows the writes.

- [ ] **Step 6: Commit**

Subject: `feat(breeding): births and newcomers roll traits`.
```bash
git add server/FolkIdle.Server/Engine/BreedingEngine.cs server/FolkIdle.Server/Engine/VillageArrivalEngine.cs server/FolkIdle.Server.Tests/BreedingTraitsIntegrationTests.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 9: REST - the catalogue, masks and preview odds

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Produces (JSON): `GET /api/v1/breeding/traits` -> `[{ Id, Key, Name, Description, Rarity, Effect, Value }]`; roster entries gain `TraitMask`; Hall members gain `TraitMask`; newcomers gain `TraitMask`; both previews gain `TraitOdds: [{ TraitId, ChancePct, Source }]`, `MutationChancePct`, `FlawChancePct`.

- [ ] **Step 1: Route and handler for the catalogue**

After the `/api/v1/breeding/roster` route block (line ~1096-1100) insert:

```csharp
                    // Modul: the trait catalogue. Served, never copied: the client
                    // renders what this says, so a new trait is a server change only.
                    if (requestPath == "/api/v1/breeding/traits" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBreedingTraits(context);
                        continue;
                    }
```

Add the handler beside `HandleBreedingRosterSnapshot`:

```csharp
        private async Task HandleBreedingTraits(HttpListenerContext context)
        {
            try
            {
                var catalogue = Engine.TraitRegistry.All.Select(t => new
                {
                    Id = t.Bit,
                    t.Key,
                    t.Name,
                    t.Description,
                    Rarity = t.Rarity.ToString(),
                    Effect = t.Effect.ToString(),
                    t.Value,
                }).ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, catalogue);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding traits error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
```

- [ ] **Step 2: Masks on the Hall and the newcomers**

In `HandleAncestorsHall`'s member object add `TraitMask = lineage?.TraitMask ?? 0L,` after `IsInbred = lineage?.IsInbred ?? false,`.
In `HandleVillageNewcomers`'s newcomer object add `v.TraitMask,` after `v.IsElder,`.
(The roster already carries `TraitMask` from Task 7.)

- [ ] **Step 3: Preview odds**

Add beside `AptitudePreviewResponse`:

```csharp
        private sealed class TraitOddsResponse
        {
            public int TraitId { get; set; }
            public int ChancePct { get; set; }
            public string Source { get; set; } = string.Empty;
        }
```

Add to `BreedingPreviewResponse`:

```csharp
            public System.Collections.Generic.List<TraitOddsResponse> TraitOdds { get; set; } = new();
            public int MutationChancePct { get; set; }
            public int FlawChancePct { get; set; }
```

Add beside `AddAptitudePreviews`:

```csharp
        private static void AddTraitPreviews(BreedingPreviewResponse response, long heroMask, long partnerMask, int groundsLevel)
        {
            foreach (var odds in Engine.BreedingTraits.PreviewOdds(heroMask, partnerMask))
            {
                response.TraitOdds.Add(new TraitOddsResponse { TraitId = odds.Bit, ChancePct = odds.ChancePct, Source = odds.Source });
            }
            response.MutationChancePct = Engine.BreedingTraits.MutationPercentFor(groundsLevel);
            response.FlawChancePct = Engine.BreedingTraits.FlawPercentFor(response.IsInbredRisk);
        }

        private static async Task<int> BreedingGroundsLevelAsync(FolkIdleDbContext db, long playerId)
            => await db.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId && v.BuildingId == Domain.Progression.VillageManagementEngine.BreedingGroundsBuildingId)
                .Select(v => (int?)v.CurrentLevel)
                .SingleOrDefaultAsync() ?? 0;
```

In `HandleBreedingPreview`, after `AddAptitudePreviews(response.Aptitudes, pLineage.AptitudeVector(), mLineage.AptitudeVector());` insert:
```csharp
                AddTraitPreviews(response, pLineage.TraitMask, mLineage.TraitMask, await BreedingGroundsLevelAsync(db, playerId));
```
In `HandleVillagerBreedingPreview`, after its `AddAptitudePreviews(...)` insert:
```csharp
                AddTraitPreviews(response, heroLineage.TraitMask, newcomer.TraitMask, await BreedingGroundsLevelAsync(db, playerId));
```

- [ ] **Step 4: Verify against the running server**

Stop the server, build, start the stack (`.\run-dev.ps1`), sign in as the dev fixture in a browser, then in the browser console:
```js
const t = sessionStorage.getItem('folkidle.token') ?? localStorage.getItem('folkidle.token');
await (await fetch('http://localhost:8080/api/v1/breeding/traits', { headers: { Authorization: `Bearer ${t}` } })).json();
```
Expected: 14 entries, `Id` 0-10 and 16-18.

- [ ] **Step 5: Run the server suite**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`
Expected: all pass.

- [ ] **Step 6: Commit**

Subject: `feat(breeding): serve the trait catalogue, masks and preview odds`.
```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 10: Traits in the power ledgers

**Files:**
- Modify: `server/FolkIdle.Server.Tests/PowerCeilingTests.cs`

- [ ] **Step 1: Add the levers**

In `TheDamageLedgerStaysInsideItsBand`, before `double product = 1.0;` (line ~138), insert:

```csharp
            // 9. Traits. Every positive attack trait at once - more than the
            //    three-trait cap allows, so a pessimistic reading - clamped by
            //    TraitTotals.PositiveCap.
            long everyPositive = 0L;
            foreach (var t in TraitRegistry.All)
            {
                if (t.Rarity != TraitRarity.Flaw) everyPositive |= 1L << t.Bit;
            }
            var bestTraits = TraitTotals.From(everyPositive);
            levers.Add(new Lever("traits: attack", 1.0 + bestTraits.AttackPct / 100.0,
                $"every attack trait, capped at {TraitTotals.PositiveCap}"));
```

In `TheYieldLedgerStaysInsideItsBand`, add to the `levers` list initializer:

```csharp
                new("traits: gathering yield",
                    1.0 + TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.GreenThumb)).GatherYieldPct / 100.0,
                    "Green Thumb, the only yield trait"),
```

- [ ] **Step 2: Run the ledger**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~PowerCeilingTests" --logger "console;verbosity=detailed"`
Expected: PASS, and the printed ledger shows `traits: attack` at 1.14x and `traits: gathering yield` at 1.05x.

- [ ] **Step 3: Commit**

Subject: `test(balance): traits are levers in both power ledgers`.
```bash
git add server/FolkIdle.Server.Tests/PowerCeilingTests.cs
git commit -F <path-to>/commit-msg.txt
```

---

### Task 11: Client data layer for traits

**Files:**
- Modify: `client_web/src/lib/net/rest.ts`
- Create: `client_web/src/lib/ui/traits.ts`
- Test: `client_web/tests/traits.test.ts`

**Interfaces:**
- Produces: `type TraitRarity = 'Common' | 'Rare' | 'Legendary' | 'Flaw'`; `interface TraitDefinition { Id: number; Key: string; Name: string; Description: string; Rarity: TraitRarity; Effect: string; Value: number }`; `interface TraitOddsEntry { TraitId: number; ChancePct: number; Source: 'hero' | 'partner' | 'both' }`; `fetchTraits(): Promise<TraitDefinition[]>`; `queryKeys.traits`; `TraitMask: number` on `BreedingCandidate`, `HallMember`, `VillageNewcomer`; `TraitOdds`, `MutationChancePct`, `FlawChancePct` on `BreedingPreview`. From `traits.ts`: `traitBitsOf(mask: number): number[]`, `traitsOf(mask: number, catalogue: readonly TraitDefinition[]): TraitDefinition[]`, `rarityClass(rarity: TraitRarity): string`, `oddsSourceLabel(source: TraitOddsEntry['Source'], mode: 'village' | 'roster'): string`.

- [ ] **Step 1: Write the failing test**

```ts
// client_web/tests/traits.test.ts
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { TraitDefinition } from '../src/lib/net/rest';
import { traitBitsOf, traitsOf, rarityClass, oddsSourceLabel } from '../src/lib/ui/traits';

const catalogue: TraitDefinition[] = [
  { Id: 4, Key: 'iron_blood', Name: 'Iron Blood', Description: '+8% max HP', Rarity: 'Rare', Effect: 'MaxHpPct', Value: 8 },
  { Id: 16, Key: 'thin_blood', Name: 'Thin Blood', Description: '-6% max HP', Rarity: 'Flaw', Effect: 'MaxHpPct', Value: -6 },
];

describe('traits', () => {
  it('reads bits without 32-bit bitwise overflow', () => {
    expect(traitBitsOf(0)).toEqual([]);
    expect(traitBitsOf(2 ** 4 + 2 ** 16)).toEqual([4, 16]);
    expect(traitBitsOf(2 ** 40)).toEqual([40]);
  });

  it('maps a mask to catalogue entries and skips unknown bits', () => {
    expect(traitsOf(2 ** 4 + 2 ** 16 + 2 ** 30, catalogue).map((t) => t.Name)).toEqual(['Iron Blood', 'Thin Blood']);
  });

  it('names rarity classes and odds sources for both pairings', () => {
    expect(rarityClass('Legendary')).toBe('trait-legendary');
    expect(oddsSourceLabel('both', 'roster')).toBe('both parents');
    expect(oddsSourceLabel('hero', 'village')).toBe('you');
    expect(oddsSourceLabel('partner', 'village')).toBe('them');
    expect(oddsSourceLabel('hero', 'roster')).toBe('the father');
    expect(oddsSourceLabel('partner', 'roster')).toBe('the mother');
  });

  // Modul: THE CATALOGUE IS SERVED, NEVER COPIED. A second copy of the table
  // is the two-sources-of-truth bug class this codebase keeps paying for.
  it('keeps no copy of the catalogue in client source', () => {
    const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
    const offenders: string[] = [];
    const walk = (dir: string) => {
      for (const name of readdirSync(dir).sort()) {
        const full = join(dir, name);
        if (statSync(full).isDirectory()) walk(full);
        else if (/\.(ts|svelte)$/.test(name) && /blood_of_kings|Blood of Kings|stout_heart/.test(readFileSync(full, 'utf8'))) {
          offenders.push(full);
        }
      }
    };
    walk(src);
    expect(offenders).toEqual([]);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd client_web; npx vitest run tests/traits.test.ts`
Expected: FAIL - `Failed to resolve import "../src/lib/ui/traits"`.

- [ ] **Step 3: Types and fetch in rest.ts**

After `export interface VillageNewcomer { ... }` field `IsElder: boolean;` add `TraitMask: number;`. In `BreedingCandidate` after `IsInbred: boolean;` add `TraitMask: number;`. In `HallMember` after `IsInbred: boolean;` add `TraitMask: number;`. Replace `export interface BreedingPreview { ... }` with:

```ts
export interface TraitOddsEntry {
  TraitId: number;
  ChancePct: number;
  Source: 'hero' | 'partner' | 'both';
}

export interface BreedingPreview {
  IsEligible: boolean;
  IneligibleReason: string;
  IsInbredRisk: boolean;
  BreedingCostGold: number;
  HasSufficientGold: boolean;
  Loci: GeneLocusPreview[];
  Aptitudes: AptitudePreview[];
  TraitOdds: TraitOddsEntry[];
  MutationChancePct: number;
  FlawChancePct: number;
}
```

Add near `fetchBreedingRoster`:

```ts
export type TraitRarity = 'Common' | 'Rare' | 'Legendary' | 'Flaw';

/** One heritable trait, as GET /api/v1/breeding/traits describes it. The client never keeps its own copy. */
export interface TraitDefinition {
  Id: number;
  Key: string;
  Name: string;
  Description: string;
  Rarity: TraitRarity;
  Effect: string;
  Value: number;
}

export function fetchTraits(): Promise<TraitDefinition[]> {
  return authedGet<TraitDefinition[]>('/api/v1/breeding/traits');
}
```

In `queryKeys` add `traits: ['meta', 'traits'] as const,` after `breedingRoster`.

- [ ] **Step 4: traits.ts**

```ts
// client_web/src/lib/ui/traits.ts
// Modul: HERITABLE TRAITS, as the client shows them. The catalogue comes from
// the server (fetchTraits) and is never copied here - tests/traits.test.ts
// fails if a trait name appears in client source.
import type { TraitDefinition, TraitOddsEntry, TraitRarity } from '../net/rest';

/**
 * The bits set in a mask. Arithmetic, not `&`: JavaScript bitwise operators
 * truncate to 32 bits, and a mask is a C# long.
 */
export function traitBitsOf(mask: number): number[] {
  const bits: number[] = [];
  for (let bit = 0; bit < 53; bit++) {
    if (Math.floor(mask / 2 ** bit) % 2 === 1) bits.push(bit);
  }
  return bits;
}

export function traitsOf(mask: number, catalogue: readonly TraitDefinition[]): TraitDefinition[] {
  const byId = new Map(catalogue.map((t) => [t.Id, t]));
  return traitBitsOf(mask)
    .map((bit) => byId.get(bit))
    .filter((t): t is TraitDefinition => t !== undefined);
}

export function rarityClass(rarity: TraitRarity): string {
  return `trait-${rarity.toLowerCase()}`;
}

export function oddsSourceLabel(source: TraitOddsEntry['Source'], mode: 'village' | 'roster'): string {
  if (source === 'both') return 'both parents';
  if (mode === 'village') return source === 'hero' ? 'you' : 'them';
  return source === 'hero' ? 'the father' : 'the mother';
}
```

- [ ] **Step 5: Run tests and the type check**

Run: `cd client_web; npx vitest run tests/traits.test.ts; npm run check:ratchet`
Expected: 4 tests PASS; `svelte-check errors: 4 (baseline 4)`.

- [ ] **Step 6: Commit**

Subject: `feat(client): trait types, catalogue fetch and mask helpers`.
```bash
git add client_web/src/lib/net/rest.ts client_web/src/lib/ui/traits.ts client_web/tests/traits.test.ts
git commit -F <path-to>/commit-msg.txt
```

---

### Task 12: Trait badges on every breeding surface

**Files:**
- Create: `client_web/src/lib/ui/TraitBadge.svelte`
- Modify: `client_web/src/lib/ui/breedingPicker.ts`, `client_web/tests/breedingPicker.test.ts`
- Modify: `client_web/src/lib/ui/PersonPicker.svelte`, `client_web/src/routes/Breeding.svelte`
- Modify: `client_web/src/lib/ui/ChildPreview.svelte`, `client_web/src/lib/ui/breeding.ts`
- Modify: `client_web/src/routes/Ancestors.svelte`, `client_web/src/lib/ui/VillageFolk.svelte`, `client_web/src/lib/ui/WikiVillage.svelte`

**Interfaces:**
- Consumes: `TraitDefinition`, `fetchTraits`, `queryKeys.traits`, `traitsOf`, `rarityClass`, `oddsSourceLabel` (Task 11).
- Produces: `PickerPerson.traits: readonly TraitDefinition[]`; `heroPerson(c, now, catalogue = [])`, `partnerCharacterPerson(hero, c, now, catalogue = [])`, `villagerPerson(hero, p, catalogue = [])`; `<TraitBadge trait interactive? />`; `<ChildPreview preview mode generation catalogue />`.

- [ ] **Step 1: Write the failing test** (append to `client_web/tests/breedingPicker.test.ts`)

Add `TraitMask: 0,` to the object returned by both `candidate()` and `newcomer()` fixtures, then append:

```ts
describe('picker traits', () => {
  const catalogue = [
    { Id: 4, Key: 'iron_blood', Name: 'Iron Blood', Description: '+8% max HP', Rarity: 'Rare' as const, Effect: 'MaxHpPct', Value: 8 },
  ];

  it('carries a person\'s traits from the catalogue', () => {
    expect(heroPerson(candidate({ TraitMask: 2 ** 4 }), 0, catalogue).traits.map((t) => t.Name)).toEqual(['Iron Blood']);
    expect(villagerPerson(undefined, newcomer({ TraitMask: 2 ** 4 }), catalogue).traits).toHaveLength(1);
    expect(heroPerson(candidate(), 0).traits).toEqual([]);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd client_web; npx vitest run tests/breedingPicker.test.ts`
Expected: FAIL - `Cannot read properties of undefined (reading 'map')` (no `traits` on `PickerPerson`).

- [ ] **Step 3: breedingPicker.ts**

Add `import type { TraitDefinition } from '../net/rest';` and `import { traitsOf } from './traits';`. Add to `PickerPerson`:

```ts
  /** Heritable traits, resolved against the served catalogue. */
  traits: readonly TraitDefinition[];
```

Change `characterBase(candidate: BreedingCandidate)` to `characterBase(candidate: BreedingCandidate, catalogue: readonly TraitDefinition[])` and add `traits: traitsOf(candidate.TraitMask, catalogue),` to its returned object. Change the three builders:

```ts
export function heroPerson(
  candidate: BreedingCandidate,
  nowSeconds: number,
  catalogue: readonly TraitDefinition[] = [],
): PickerPerson {
  return { ...characterBase(candidate, catalogue), blocked: heroBlockedReason(candidate, nowSeconds) };
}

export function partnerCharacterPerson(
  hero: BreedingCandidate | undefined,
  candidate: BreedingCandidate,
  nowSeconds: number,
  catalogue: readonly TraitDefinition[] = [],
): PickerPerson {
  return { ...characterBase(candidate, catalogue), blocked: characterPartnerBlockedReason(hero, candidate, nowSeconds) };
}
```
and in `villagerPerson(hero, person, catalogue: readonly TraitDefinition[] = [])` add `traits: traitsOf(person.TraitMask, catalogue),` to the returned object.

- [ ] **Step 4: TraitBadge.svelte**

```svelte
<script lang="ts">
  // Modul: ONE TRAIT, as a coloured badge. Tap (or hover) for its effect -
  // `title` alone does nothing on a phone. `interactive={false}` renders a plain
  // span for places already inside a button (PersonPicker's option cards),
  // because a button inside a button is invalid and swallows the tap.
  import type { TraitDefinition } from '../net/rest';
  import { rarityClass } from './traits';

  interface Props {
    trait: TraitDefinition;
    interactive?: boolean;
  }

  const { trait, interactive = true }: Props = $props();
  let open = $state(false);
</script>

{#if interactive}
  <span class="wrap">
    <button
      type="button"
      class="trait {rarityClass(trait.Rarity)}"
      title={trait.Description}
      aria-expanded={open}
      onclick={(event) => {
        event.stopPropagation();
        open = !open;
      }}>{trait.Name}</button
    >
    {#if open}<span class="desc">{trait.Description}</span>{/if}
  </span>
{:else}
  <span class="trait {rarityClass(trait.Rarity)}" title={trait.Description}>{trait.Name}</span>
{/if}

<style>
  .wrap {
    display: inline-flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.15rem;
    min-width: 0;
  }

  .trait {
    display: inline-flex;
    align-items: center;
    flex-shrink: 0;
    font: inherit;
    font-size: 0.68rem;
    font-weight: 600;
    line-height: 1.2;
    padding: 0.1rem 0.4rem;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: var(--bg);
    color: var(--text);
    cursor: default;
    white-space: nowrap;
  }

  button.trait {
    cursor: pointer;
  }

  .trait-common {
    border-color: var(--border);
    color: var(--text-dim);
  }
  .trait-rare {
    border-color: #4a7fc1;
    color: #6f9fd8;
  }
  .trait-legendary {
    border-color: var(--brass, #c9a227);
    color: var(--brass-lit, #e0b93a);
  }
  .trait-flaw {
    border-color: var(--danger);
    color: var(--danger);
  }

  .desc {
    font-size: 0.7rem;
    color: var(--text-dim);
  }
</style>
```

- [ ] **Step 5: Picker cards and Breeding.svelte**

`PersonPicker.svelte`: import `TraitBadge`; inside `{#snippet card(person)}`, after `<span class="detail">{person.detail}</span>`, add:

```svelte
      {#if person.traits.length > 0}
        <span class="traits">
          {#each person.traits as trait (trait.Id)}<TraitBadge {trait} interactive={false} />{/each}
        </span>
      {/if}
```
and in its `<style>`:
```css
  .traits {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem;
    margin-top: 0.15rem;
  }
```

`Breeding.svelte`: add `fetchTraits` to the rest import, then after the `village` query:

```ts
  const traitCatalogue = createQuery(() => ({ queryKey: queryKeys.traits, queryFn: fetchTraits, staleTime: Infinity }));
  const catalogue = $derived(traitCatalogue.data ?? []);
```
Pass `catalogue` as the last argument in `heroPerson(c, nowSeconds, catalogue)`, `villagerPerson(hero, p, catalogue)` and `partnerCharacterPerson(hero, c, nowSeconds, catalogue)`, and add `{catalogue}` to `<ChildPreview ... />`.

- [ ] **Step 6: ChildPreview.svelte**

Add `catalogue: readonly TraitDefinition[];` to `Props` (import `type TraitDefinition` from `../net/rest`), destructure it, import `TraitBadge` and `oddsSourceLabel`. Remove `geneBlurb` from the `./breeding` import, delete `const genes = ...`, and delete the whole `{#if genes.length > 0} ... {/if}` block ("And its genes"). Insert before `<h3>And what it will be</h3>`:

```svelte
<h3>Traits the child can inherit</h3>
{#if preview.TraitOdds.length === 0}
  <p class="dim tiny">Neither parent carries a trait.</p>
{:else}
  <ul class="apts">
    {#each preview.TraitOdds as odds (odds.TraitId)}
      {@const trait = catalogue.find((t) => t.Id === odds.TraitId)}
      <li>
        <div class="head">
          {#if trait}<TraitBadge {trait} />{:else}<span class="name">Trait {odds.TraitId}</span>{/if}
          <span class="band">{odds.ChancePct}%</span>
        </div>
        <p class="why dim tiny">from {oddsSourceLabel(odds.Source, mode)}</p>
      </li>
    {/each}
  </ul>
{/if}
<p class="dim tiny arrival">
  <strong>{preview.MutationChancePct}%</strong> chance of a new trait from the Breeding Grounds.
  A child keeps at most three; flaws are never lost to that limit.
</p>
{#if preview.FlawChancePct > 0}
  <p class="warn-line tiny">
    These two are related: <strong>{preview.FlawChancePct}% chance of a flaw</strong>.
  </p>
{/if}
```
In the existing `{#if inbred}` paragraph, replace `Related pairs also lose a quarter of every gene below.` with `Related pairs also risk a flaw - see the traits below.`

`breeding.ts`: delete `GENE_BLURBS` and `geneBlurb` (and their comment block).

- [ ] **Step 7: Hall, village and Wiki**

`Ancestors.svelte`: import `fetchTraits` and `TraitBadge` and `traitsOf`; add
```ts
  const traitCatalogue = createQuery(() => ({ queryKey: queryKeys.traits, queryFn: fetchTraits, staleTime: Infinity }));
```
Inside `.who`, after the `<span class="dim tiny">...</span>` parentage line add:
```svelte
                {#if m.TraitMask > 0 && traitCatalogue.data}
                  <span class="traits">
                    {#each traitsOf(m.TraitMask, traitCatalogue.data) as trait (trait.Id)}<TraitBadge {trait} />{/each}
                  </span>
                {/if}
```
and the same `.traits` CSS rule as PersonPicker.

`VillageFolk.svelte`: identical query and imports; inside `.who` after the woman/man line add the same block with `person.TraitMask`, and the same CSS.

`WikiVillage.svelte`: import `createQuery`, `queryKeys`, `fetchTraits`, `TraitBadge`; add the same query; append at the end of the markup:
```svelte
<h3 id="traits">Heritable traits</h3>
<p class="dim small">
  A character carries at most three. Each parent's trait passes to a child half the
  time, nine times in ten when both parents carry it. A newcomer may bring one - a
  better Inn brings rarer ones, and Legendary traits only from Inn level 6. The
  Breeding Grounds adds a chance of a new trait at birth, an epic child always gets a
  Rare or Legendary one, and a related pair risks a flaw. Flaws are never dropped by
  the limit of three; the way out is marrying clean blood.
</p>
{#if traitCatalogue.data}
  <div class="scroll">
    <table>
      <thead><tr><th>Trait</th><th>Rarity</th><th>Effect</th></tr></thead>
      <tbody>
        {#each traitCatalogue.data as trait (trait.Id)}
          <tr><td><TraitBadge {trait} interactive={false} /></td><td>{trait.Rarity}</td><td>{trait.Description}</td></tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}
```

- [ ] **Step 8: Run tests and checks**

Run: `cd client_web; npx vitest run; npm run check:ratchet`
Expected: all tests PASS; `svelte-check errors: 4 (baseline 4)`.

- [ ] **Step 9: Commit**

Subject: `feat(client): trait badges on pickers, preview, Hall, village and Wiki`.
```bash
git add client_web/src client_web/tests
git commit -F <path-to>/commit-msg.txt
```

---

### Task 13: End-to-end verification and docs

**Files:**
- Modify: `client_web/scripts/exercise.mjs` (breeding step, after `'the preview quotes a price'`)
- Modify: `docs/breeding_model.md`, `docs/architecture/NEXT_STEPS_BACKLOG.md`

- [ ] **Step 1: Assert the preview shows traits**

In `exercise.mjs`, directly after the `record('the preview quotes a price', ...)` call, add:

```js
    // Modul: TRAITS replaced the genes (2026-09-13). The section renders for
    // every pair - "Neither parent carries a trait." is an honest answer - and
    // the mutation chance comes from the Breeding Grounds, so both must be here.
    const traitsText = await page.evaluate(() => document.body.innerText);
    record(
      'the preview explains traits and the mutation chance',
      /Traits the child can inherit/.test(traitsText) && /chance of a new trait/.test(traitsText),
    );
    record('the preview no longer lists genes', !/And its genes/.test(traitsText));
```

- [ ] **Step 2: Run the full verification**

Stop the server (separate command), then:
```powershell
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
.\run-dev.ps1
cd client_web
npm run exercise
npm run check:touch
npm run check:overlap
npm run check:clipping
```
Expected: server suite all PASS; exercise passes every breeding, Hall and village check (the loot-panel check is a known timing flake - rerun once before treating it as real); geometry checks report no failure on Breeding, Ancestors or Village.

- [ ] **Step 3: Document**

`docs/breeding_model.md`: replace section "### The four genes" with a "### Traits" section summarising spec sections 3-4 (catalogue table, 50/90%, cap of 3 with flaws kept, mutation 4% + 1%/Grounds level, epic Rare+, related pair 60% flaw, newcomers 20% + 3%/Inn level to 50%, 15% flaws, Legendary from Inn 6). In section 2's inbreeding list replace "The Speed, Crit and Yield genes each lose 25% of both copies." with "A 60% chance the child carries a flaw.". In `NEXT_STEPS_BACKLOG.md` add a dated handoff at the top: traits shipped, genes and the hidden inbreeding penalty retired, offline Strength fixed, migration `AddBreedingTraits` is additive.

- [ ] **Step 4: Commit**

Subject: `test(breeding): exercise asserts the trait preview; docs describe traits`.
```bash
git add client_web/scripts/exercise.mjs docs/breeding_model.md docs/architecture/NEXT_STEPS_BACKLOG.md
git commit -F <path-to>/commit-msg.txt
```

- [ ] **Step 5: Deploy (only with the owner's go-ahead)**

Take a backup of `characters`, `character_lineage_registry` and `village_newcomers` on the box first (the round-1 backup command in the `deploy` skill notes), push to GitHub and the box, `docker compose up -d --build`, confirm `Database migrations applied successfully.` in the app log, then `FOLKIDLE_E2E_BASE=https://folkidle.cz/ npm run smoke:screens`.
