# Breeding Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make breeding reachable for every player, give the Inn and the Breeding Grounds a real job each, stop aging from being a three-hour treadmill, and explain all of it on screen and in the Wiki.

**Architecture:** The gate moves off a dead `characters.Level` column onto the village buildings the player can see. Aging's duplicated thresholds collapse into one `AgePhaseCurve` and stretch. The Breeding Grounds level buys *selection* — a chosen aptitude takes the better parent's value instead of a weighted coin — travelling on a new dedicated `ClientCommandPacket` field. Characters gain names so a player can find their own hero.

**Tech Stack:** C# / .NET 8 server, EF Core + Postgres, Svelte 5 (runes) client, xUnit + Testcontainers, vitest.

**Spec:** `docs/superpowers/specs/2026-09-12-breeding-rework-design.md`

## Global Constraints

- Aging curve: Child 0–1 h, Adult 1–40 h, Senior 40–80 h (−5%), Elder 80 h+ (−10%). One hour = 36,000 age ticks at 10 Hz.
- Villager aptitude roll: `2 + rand(0..InnLevel * 3 / 2)`, clamped to `VillagerCeiling = 20`.
- Selection count by Breeding Grounds level: 1–3 → 0, 4–6 → 1, 7–9 → 2, 10–12 → 3.
- Up-mutation chance: `25 + BreedingGroundsLevel` percent. Down stays 10. Inbreeding still inverts the two.
- `characters.Level` is deleted, not repurposed.
- Never hand-write `client_web/src/lib/net/protocol.generated.ts` — run `npm run generate:protocol`.
- Stop the running server before `dotnet build`; `dotnet test` needs Docker.
- Every new `RollbackAsync` path enqueues a `CommandResultCode`.

---

### Task 1: One aging curve, stretched

**Files:**
- Create: `server/FolkIdle.Server/Domain/Progression/AgePhaseCurve.cs`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:4796-4814` (`ProcessAgeSlot`)
- Modify: `server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs:180-187`
- Modify: `server/FolkIdle.Server/Engine/StatsCalculator.cs:349-363` (penalty bands)
- Test: `server/FolkIdle.Server.Tests/AgePhaseCurveTests.cs`

**Interfaces:**
- Produces: `AgePhaseCurve.PhaseFor(long ageTicks) -> int`, `AgePhaseCurve.TicksPerHour = 36000L`, `AgePhaseCurve.ChildEndTicks`, `AdultEndTicks`, `SeniorEndTicks`, `AgePhaseCurve.PenaltyMultiplier(int phase) -> float`.

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData(0, 0)]
[InlineData(35_999, 0)]
[InlineData(36_000, 1)]
[InlineData(1_439_999, 1)]
[InlineData(1_440_000, 2)]
[InlineData(2_879_999, 2)]
[InlineData(2_880_000, 3)]
public void PhaseForLandsOnTheDocumentedBoundaries(long ticks, int expected)
    => Assert.Equal(expected, AgePhaseCurve.PhaseFor(ticks));

[Fact]
public void TheLivePathAndTheOfflinePathCannotDrift()
{
    // Both callers must go through the curve; neither may carry a literal.
    string live = File.ReadAllText(PathTo("Domain/Combat/SimulationEngine.cs"));
    string offline = File.ReadAllText(PathTo("Engine/OfflineSimulationEngine.cs"));
    Assert.DoesNotContain("108000", live);
    Assert.DoesNotContain("108000L", offline);
    Assert.Contains("AgePhaseCurve.PhaseFor", live);
    Assert.Contains("AgePhaseCurve.PhaseFor", offline);
}

[Theory]
[InlineData(0, 1.0f)]
[InlineData(1, 1.0f)]
[InlineData(2, 0.95f)]
[InlineData(3, 0.90f)]
public void PenaltiesAreTheStretchedOnes(int phase, float expected)
    => Assert.Equal(expected, AgePhaseCurve.PenaltyMultiplier(phase));
```

- [ ] **Step 2: Run it and watch it fail**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~AgePhaseCurveTests"` — expect a compile failure, `AgePhaseCurve` does not exist.

- [ ] **Step 3: Write `AgePhaseCurve`**

Constants `TicksPerHour = 36_000L`, `ChildEndTicks = TicksPerHour`, `AdultEndTicks = 40 * TicksPerHour`, `SeniorEndTicks = 80 * TicksPerHour`. `PhaseFor` descends through them. `PenaltyMultiplier` returns 1.0 / 1.0 / 0.95 / 0.90. Carry a `// Modul:` comment recording that these thresholds used to be literals in two files and that aging's only exit was a breeding path that had never worked.

- [ ] **Step 4: Point both callers and the stat penalty at it**

`ProcessAgeSlot` becomes `int newPhase = AgePhaseCurve.PhaseFor(ageTicks);`. `OfflineSimulationEngine` loses its four-line ladder the same way. `StatsCalculator` replaces the two hard-coded `0.9f` / `0.8f` blocks with one multiply by `AgePhaseCurve.PenaltyMultiplier(activeAgePhase)`.

- [ ] **Step 5: Run the tests**

Expect PASS, and run `--filter "FullyQualifiedName~ProgressionRateTests"` too — the health-pool projections read age phase.

- [ ] **Step 6: Commit**

```bash
git add server/FolkIdle.Server/Domain/Progression/AgePhaseCurve.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs server/FolkIdle.Server/Engine/OfflineSimulationEngine.cs server/FolkIdle.Server/Engine/StatsCalculator.cs server/FolkIdle.Server.Tests/AgePhaseCurveTests.cs
git commit -m "fix(aging): one curve, and a hero that lasts a week rather than an evening"
```

---

### Task 2: Delete the gate that could never open

**Files:**
- Modify: `server/FolkIdle.Server/Engine/BreedingEngine.cs:90` and `:305`
- Modify: `server/FolkIdle.Server/Models/CharacterRecord.cs:19` (delete `Level`)
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` — roster entry, both preview handlers
- Modify: `server/FolkIdle.Server/Models/DevFixtureSeeder.cs:459`
- Create: migration `DropCharacterLevel`
- Test: `server/FolkIdle.Server.Tests/BreedingGateTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a breeding path that accepts any adult character.

- [ ] **Step 1: Write the failing test** — a level-less adult pair breeds successfully; a child (`AgePhase = 0`) is refused; the roster response carries no `Level`.

- [ ] **Step 2: Run it, watch it fail** (the fixture's characters are level 50, so assert on a fresh pair created at the default).

- [ ] **Step 3: Remove `|| pChar.Level < 50 || mChar.Level < 50` and `|| hero.Level < 50`**, leaving the `AgePhase` half. Delete the `Level` property, the `BreedingRosterEntryResponse.Level`, the preview handlers' reads, and the seeder's write.

- [ ] **Step 4: Generate the migration**

```powershell
dotnet ef migrations add DropCharacterLevel --project server/FolkIdle.Server/FolkIdle.Server.csproj
```

Check the generated `Down` restores the column with its old default. Apply locally — migrations do not run on `run-dev.ps1`.

- [ ] **Step 5: Run the tests, then commit**

```bash
git commit -m "fix(breeding): the level gate read a column nothing ever wrote"
```

---

### Task 3: Every refusal says something

**Files:**
- Modify: `server/FolkIdle.Server/Network/StateUpdatePacket.cs` (`CommandResultCode` 26–33)
- Modify: `server/FolkIdle.Server/Engine/BreedingEngine.cs` (all twenty rollbacks)
- Modify: `client_web/src/routes/Breeding.svelte` (`refusal()`)
- Test: `server/FolkIdle.Server.Tests/BreedingGateTests.cs` (extend)

**Interfaces:**
- Produces: `BreedingNoGrounds = 26`, `BreedingParentNotAdult = 27`, `BreedingParentResting = 28`, `BreedingParentInEscrow = 29`, `BreedingSexRoles = 30`, `BreedingRaceMismatch = 31`, `BreedingPartnerSpent = 32`, `BreedingInsufficientGold = 33`.

- [ ] **Step 1: Write the failing test** — one assertion per code, driving a real refusal through the engine and reading the command-result ring.

- [ ] **Step 2: Run it, watch it fail** (the ring stays zero — every refusal is silent today).

- [ ] **Step 3: Add the codes and enqueue one at each rollback.** Enum only, so no protocol regeneration; confirm with `node scripts/generate-protocol.mjs --check`.

- [ ] **Step 4: Teach the client's `refusal()` the new codes**, and delete the now-dead `hero_not_mature` / `parent_not_mature` strings.

- [ ] **Step 5: Run tests, commit**

```bash
git commit -m "fix(breeding): twenty rollbacks, and not one of them said a word"
```

---

### Task 4: The Inn reaches the twenty it documents

**Files:**
- Modify: `server/FolkIdle.Server/Engine/BreedingAptitudes.cs` (`RollVillager`)
- Test: `server/FolkIdle.Server.Tests/BreedingClimbTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void AMaxedInnCanActuallyReachTheVillagerCeiling()
{
    int best = 0;
    var rng = new Random(1);
    for (int i = 0; i < 20_000; i++)
        foreach (int v in BreedingAptitudes.RollVillager(12, rng))
            best = Math.Max(best, v);

    Assert.Equal(BreedingAptitudes.VillagerCeiling, best);
}
```

- [ ] **Step 2: Run it** — fails at 14, the Town Hall ceiling of 12 plus the base 2.

- [ ] **Step 3: Change the reach** to `innLevel * 3 / 2` and update the doc comment to state that a maxed Inn reaches 20 and why the old form could not.

- [ ] **Step 4: Run, commit**

```bash
git commit -m "fix(breeding): the village ceiling was a number the Inn could not roll"
```

---

### Task 5: The Breeding Grounds buys selection

**Files:**
- Modify: `server/FolkIdle.Server/Engine/BreedingAptitudes.cs`
- Test: `server/FolkIdle.Server.Tests/BreedingSelectionTests.cs`

**Interfaces:**
- Produces: `BreedingAptitudes.SelectableCount(int groundsLevel) -> int`, `BreedingAptitudes.ClampSelection(int mask, int groundsLevel) -> int`, `BreedingAptitudes.Breed(int[] father, int[] mother, bool isInbred, bool isEpic, int selectionMask, int groundsLevel, Random rng) -> int[]`, `BreedingAptitudes.UpMutationPercentFor(int groundsLevel) -> int`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(0, 0)] [InlineData(3, 0)] [InlineData(4, 1)] [InlineData(6, 1)]
[InlineData(7, 2)] [InlineData(9, 2)] [InlineData(10, 3)] [InlineData(12, 3)]
public void SelectableCountByGroundsLevel(int level, int expected)
    => Assert.Equal(expected, BreedingAptitudes.SelectableCount(level));

[Fact]
public void ASelectedAptitudeNeverTakesTheWorseParent()
{
    var father = new[] { 12, 4, 4, 4 };
    var mother = new[] { 4, 4, 4, 4 };
    var rng = new Random(7);
    for (int i = 0; i < 500; i++)
    {
        var child = BreedingAptitudes.Breed(father, mother, false, false, 0b0001, 4, rng);
        Assert.True(child[0] >= 11, $"selected Strength fell to {child[0]}");
    }
}

[Fact]
public void TheMaskIsClampedToWhatTheGroundsPermit()
    => Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)BreedingAptitudes.ClampSelection(0b1111, 4)));
```

- [ ] **Step 2: Run them, watch them fail.**

- [ ] **Step 3: Implement.** `Breed` takes the mask; for a selected index use `Math.Max(father[i], mother[i])` instead of `InheritOne`, then mutate as usual. `Mutate` takes the up-percent so the Grounds level feeds it. `ClampSelection` keeps the lowest set bits up to `SelectableCount`.

- [ ] **Step 4: Update both call sites** in `BreedingEngine` to pass the mask and the Grounds level.

- [ ] **Step 5: Add the printed-and-asserted climb measurement** to `BreedingClimbTests` — generations to reach 20 at Inn 5 and Inn 12, drift per generation at Grounds 1 and 12. Assert bands; a printed number that is not asserted is decoration.

- [ ] **Step 6: Run, commit**

```bash
git commit -m "feat(breeding): the Grounds level finally does something, and it is the choice"
```

---

### Task 6: Carry the selection over the wire

**Files:**
- Modify: `server/FolkIdle.Server/Network/ClientCommandPacket.cs`
- Modify: `server/FolkIdle.Server/Engine/ClientCommandValidator.cs:599-663`
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs:2256-2296`
- Modify: `server/FolkIdle.Server/Engine/BreedingEngine.cs` (both signatures)
- Regenerate: `client_web/src/lib/net/protocol.generated.ts`
- Modify: `client_web/src/lib/net/commands.ts`

**Interfaces:**
- Produces: `ClientCommandPacket.BreedingSelectionMask` (uint); `ExecuteBreedingAsync(long, Guid, Guid, int selectionMask)`, `ExecuteHeroVillagerBreedingAsync(long, Guid, long, int selectionMask)`.

- [ ] **Step 1: Invoke the `add-command` skill** — the wire is generated and hand-editing it breaks start-up.

- [ ] **Step 2: Add the field** with a `// Modul:` comment saying why it is dedicated rather than riding `TargetVillagerSlot` (one field, two meanings, already a recorded trap here).

- [ ] **Step 3: Remove `BreedingSelectionMask` from both validators' zero-field lists** — they disconnect the player on any non-zero stray field, so leaving it in makes the feature a disconnect.

- [ ] **Step 4: Regenerate and verify**

```powershell
cd client_web; npm run generate:protocol; node scripts/generate-protocol.mjs --check
```

- [ ] **Step 5: Thread the mask through the dispatch to both engine methods.**

- [ ] **Step 6: Run the server suite, commit**

```bash
git commit -m "feat(breeding): the chosen aptitude reaches the server on its own field"
```

---

### Task 7: Characters get names

**Files:**
- Create: `server/FolkIdle.Server/Engine/FolkNameRegistry.cs`
- Modify: `server/FolkIdle.Server/Models/CharacterRecord.cs`
- Modify: `server/FolkIdle.Server/Engine/BreedingEngine.cs` (both birth sites), `CharacterGrantEngine.cs:92`, `DevFixtureSeeder.cs`
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (roster, hall, previews)
- Create: migration `AddCharacterName` with a deterministic backfill
- Test: `server/FolkIdle.Server.Tests/CharacterNameTests.cs`

**Interfaces:**
- Produces: `FolkNameRegistry.For(Guid characterId, bool isFemale) -> string`, deterministic in the id.

- [ ] **Step 1: Write the failing test** — every character on a seeded fixture has a non-empty name; `For` is stable across calls; a female id never draws a male name.

- [ ] **Step 2: Run it, watch it fail.**

- [ ] **Step 3: Write `FolkNameRegistry`** — two arrays of Slavic/Czech given names split by sex, indexed by a stable hash of the Guid. No database, no session; pure like `BreedingAptitudes`, so it tests in a millisecond.

- [ ] **Step 4: Add the column, set it at all three birth sites, backfill in the migration** using the same hash so the backfill and new births agree.

- [ ] **Step 5: Put the name on the roster, hall and preview responses.**

- [ ] **Step 6: Run, commit**

```bash
git commit -m "feat(characters): a name, because a GUID prefix is not an identity"
```

---

### Task 8: One flow, and it explains itself

**Files:**
- Modify: `client_web/src/routes/Breeding.svelte`
- Modify: `client_web/src/lib/net/rest.ts` (name on the candidate types)
- Test: `client_web/tests/breeding.test.ts`, `client_web/tests/serverMirrors.test.ts`

- [ ] **Step 1: Write the failing tests** — the Grounds→selection-count table mirrors the server exactly (element by element, the way `KNOWN_AFFIX_IDS` is pinned); the partner list contains both villagers and own-line candidates; a blocked partner carries its reason.

- [ ] **Step 2: Run them, watch them fail.**

- [ ] **Step 3: Collapse the tabs into one hero + one grouped partner `<select>`** using `<optgroup>`. Labels lead with the name. Drop every mention of a character level.

- [ ] **Step 4: Add the selection control** — one checkbox per aptitude, at most `SelectableCount(groundsLevel)` checked, disabled with the reason when the Grounds is below 4. State in a sentence what selection does and what the Inn and the Grounds each control.

- [ ] **Step 5: Add the "what happens next" line** — the child goes to the Hall of Ancestors, field it, it matures in an hour, the villager is spent.

- [ ] **Step 6: Check it in a browser.** `<Snippet />` type-checks and throws at runtime; `svelte-check` is silent on a shadowed `derived`. Load the page.

- [ ] **Step 7: `npm test`, `npm run check:ratchet`, `npm run check:touch`, commit**

```bash
git commit -m "feat(breeding): one question instead of two tabs, and it says what it does"
```

---

### Task 9: The Wiki page

**Files:**
- Modify: `client_web/src/routes/Wiki.svelte`
- Test: `client_web/tests/wiki.test.ts` if one exists, else assert via `smoke:screens`

- [ ] **Step 1: Write the page** — the loop end to end, the four aptitudes and what a point is worth (the three bands, +45% at the cap), the two-phase climb with its real numbers, what the Inn level and the Grounds level each buy, the aging table, inbreeding, and what survives a season rollover.

- [ ] **Step 2: Check the sidebar is reachable at 390px** — the Wiki's sidebar is declared `position: sticky` and does not stick at that width; pin by scrolling and looking.

- [ ] **Step 3: Commit**

```bash
git commit -m "docs(wiki): breeding, explained where a player will look for it"
```

---

### Task 10: Prove it in a browser, then ship

**Files:**
- Modify: `client_web/scripts/exercise.mjs`

- [ ] **Step 1: Add a breeding step** — read the roster count, marry a villager, assert the roster grew and the newcomer is spent. Make it round-trip: a check that spends fixture state passes once and fails forever, and the villager pool refills only on `--seed-dev`.

- [ ] **Step 2: Run the whole verification**

```powershell
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
dotnet test  server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
cd client_web; npm test; npm run check:ratchet; npm run exercise
```

- [ ] **Step 3: Update the docs** — `NEXT_STEPS_BACKLOG.md` handoff, and `CURRENT_IMPLEMENTATION_STATE.md` for the dropped column and the new curve.

- [ ] **Step 4: Commit, push, deploy** via the `deploy` skill, then `smoke:screens` against production read-only.

---

## Self-review

- **Spec coverage.** Gate → Task 2. Aging → Task 1. Inn lever → Task 4. Grounds lever → Tasks 5 and 6. Names → Task 7. One flow → Task 8. Result codes → Task 3. Explanation → Tasks 8 and 9. Tests → each task, plus Task 10.
- **Type consistency.** `SelectableCount`, `ClampSelection`, `UpMutationPercentFor`, `Breed(..., int selectionMask, int groundsLevel, Random)` and `AgePhaseCurve.PhaseFor` are used under those exact names in every task that references them.
- **Ordering.** Task 6 depends on Task 5's signature; Task 8 depends on Tasks 6 and 7. Tasks 1–4 are independent of each other.
