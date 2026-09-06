# FolkIdle

An idle/incremental RPG. A **C# server owns the entire simulation** and a
**Svelte web client renders it**. The client is a WebSocket peer, not a game —
it decides nothing. Any change to what the game *does* is a server change.

Live at https://folkidle.duckdns.org (and https://92-5-0-94.sslip.io, same
Oracle Ampere box). Postgres is external, on Supabase.

## Layout

| Path | What |
|---|---|
| `server/FolkIdle.Server/` | The game server. `Domain/` split into `Combat`, `Economy`, `Progression`, `Social`, `Shared`; `Engine/` is still flat. |
| `server/FolkIdle.Server.Tests/` | xUnit + Testcontainers (a real Postgres 16 per collection). |
| `server/GameData/*.json` | Content: monsters, items, gathering nodes, skills, balance, localizations. |
| `client_web/` | **The client players actually run.** Svelte 5 + Vite, 26 screens in `src/routes/`. |
| `client/` | Retired Unity project. **Do not write C# here.** Kept only for artwork/audio the web client fetches from the server. |
| `docs/architecture/` | The real documentation. See below. |
| `ops/oracle/` | Deployment: docker compose + Caddy on the Oracle box. |
| `scratch/` | One-shot patch scripts committed by accident. Not part of the build. |

There is **no `.sln`** — always target a `.csproj`.

## Commands

```powershell
.\run-dev.ps1        # the whole local stack; opens http://localhost:5173
```

That script starts Postgres+Redis containers, kills stale `dotnet`, sets the
two env vars, waits for a real endpoint, then starts Vite. Prefer it over
starting the halves by hand.

```powershell
# Build / test  (stop the running server first — see rules)
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
dotnet test  server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj   # needs Docker

# Client
cd client_web
npm run check          # svelte-check
npm run build          # regenerates protocol + sprites, then checks, then builds
npm run exercise       # THE verification — see below
npm run smoke:screens  # weaker: proves screens render
npm run check:clipping # content cut off, 25 screens × 3 widths
npm run check:overlap  # controls buried under other controls
```

All four read `FOLKIDLE_E2E_BASE`, but only **`smoke:screens` is safe to aim at
production** — it signs in as a throwaway guest and only navigates. `exercise`
*spends* (items, villagers, affix rerolls) and the two geometry checks sign in
as the dev fixture, which does not exist in production; those three are dev-box
tools. All four share the screen list in `client_web/scripts/screens.mjs` — add
a destination there, once.

Dev fixture login: `dev@folkidle.local` / `FolkIdleDev123!`. If never seeded:

```powershell
$env:FOLKIDLE_ALLOW_DEV_SEED=1
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --seed-dev
```

## Rules that are load-bearing

**Verify gameplay with `npm run exercise`, not with smoke tests.** A screen
full of buttons that all silently do nothing renders perfectly, and
`smoke:screens` will pass it. `exercise.mjs` clicks things and asserts the
world *changed* — the inventory shrank, the affix moved. This project's worst
shipped defects were all "the output side was never wired", and this script is
what catches that class. Point it elsewhere with `FOLKIDLE_E2E_BASE`.

**Stop the running server before `dotnet build`.** A server holding the output
directory makes the build *succeed* while producing a stale DLL, so the next
run is the previous build with none of your changes in it. `run-dev.ps1` kills
`dotnet` for exactly this reason.

**`dotnet test` needs a working Docker daemon.** Without it the whole suite
fails at once with `PostgresTestFixture` NullReferenceExceptions, which looks
nothing like a code error. Check Docker before debugging a mass failure.
Recovery is `docker desktop stop` then `start`.

**Never hand-write a wire type.** `client_web/src/lib/net/protocol.generated.ts`
is generated from the server's own `--dump-protocol`. Change a packet struct →
run `npm run generate:protocol`. A hand-mirrored `StateUpdatePacket` would be
the biggest two-sources-of-truth surface in the repo, and drift between two
copies of one truth is this codebase's dominant bug class.

**Raw SQL must match the table name.** EF defaults to PascalCase
(`"PlayerRecords"`, double-quoted), but a set of entities carry `[Table(...)]`
snake_case overrides — `characters`, `character_lineage_registry`,
`player_achievements`, `monster_codex_entries` and others. The full table is in
`docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` §3. Getting this wrong
throws a relation-does-not-exist at runtime, never at compile time.

**There are ELEVEN equipment slots, not eight.** 0-7 are the combat slots and
**8 Axe, 9 Pickaxe, 10 Rod** are tools. Every list that stopped at eight has
been a bug — a worn tool rendering as an empty slot, one axe counting across
the whole roster, a fixture that could not dress them. Grep for
`EquippedRingId`: it is the last of the eight, so every truncated list ends
there.

**Two gold paths, and mixing them pays the player twice.** Gold earned where no
database row was written (combat, auto-salvage) goes on the payload's
`CurrentGold` *and* `RedisPendingGoldDelta`, and the checkpoint banks it. Gold
earned where an engine already credited `CommodityRecords["gold"]` (any chest
sale) must move `CurrentGold` **only** — the checkpoint applies the pending
delta as an *increment* to the row that engine just credited, so banking it
again double-pays one checkpoint later, where nothing connects the two.
`AutoSalvageQueue` does the first, `ChestSaleGoldQueue` the second; both say so
at their struct.

**A list of owned items must be windowed.** `EquipmentInstances` grows with
playtime and had reached **17,836 rows on one live account**. `VirtualList`
renders only what is visible; its `rowHeight` is a **contract**, not a hint —
it positions by arithmetic, so a row that renders taller than the number passed
in overlaps its neighbour instead of pushing it down. `/api/v1/player/materials`
exists so the screens that only want stacks (63 rows) stop pulling the 3.2 MB
equipment blob; `invalidateOwnedItems` invalidates both keys, and a call site
that remembers one is a stale screen.

**Do not trust `<details>` to hide its own content.** An author `display` rule
on a direct child defeats the UA rule that hides a closed panel, and engines
differ on whether that rule even exists (newer ones use a `::details-content`
pseudo). The chest's collapsed sweep panel kept live, clickable buttons sitting
on top of the item list; `npm run check:overlap` found it and measuring
confirmed a 93x35 box on a panel reporting `open === false`. Use `{#if}` so the
controls are genuinely absent.

**Check which material namespace a feature needs before writing code.** Several
string spaces share one `CommodityRecords` table: gathering slugs (`raw_log`,
`wood`) with no `items.json` entry, catalogued items, and a `*_crafting_material`
space. Anything keyed on `ItemDefinitionId` silently rejects the first. ORE is
settled — one per region, common and rare, listed in
`VillageManagementEngine.TierMaterials` and matched by the gathering loot tables
and the guild's buff tiers. Six defects in one day came from confusing these.

**The wire carries combat EVENTS now, and the health bars have honest
maximums.** `ResponseCombatEventPacket` reports each resolved blow - hit, miss,
the monster's reply, lifesteal, kill - dispatched off the tick like loot drops.
It exists because the snapshot stream cannot describe a fast fight: measured,
`CurrentMonsterHp` took *one* value across 27 consecutive snapshots, because a
geared character kills an early monster between two samples. Anything that wants
to show what happened in a fight reads the feed; do not add a second inference
from a health difference. `stores/damage.ts` still infers the floating numbers
and is the one place that should eventually stop.

**A field on `StateUpdatePacket` must be loaded at login or declared
runtime-only.** `WorldBossAttemptCount` was written by one notification and
loaded by nothing, so after a relogin it read as zero and the screen offered
three spent attempts to a server that silently refused them.
`StateUpdatePacketFieldCoverageTests` now checks the whole wire both ways: every
field is copied into the packet AND either hydrated or on
`RuntimeOnlyByDesign` with a reason. Adding a wire field forces that decision.

**Silent rollback is this server's favourite way to lie.** `ExecuteAttackAsync`
alone had three: the attempt cap, an empty larder, and a five-minute battle
session nothing put on the wire - so the button stayed enabled and did nothing
for the rest of a seven-day encounter. When a handler rolls back, ask what the
player sees. If the answer is "nothing", that is the defect, not the rollback.

**A background worker that can throw is a feature that can vanish.** Every
`StartCron` loop runs in a bare `Task.Run`, so an exception anywhere in the loop
ends the task - no log, no restart, no other symptom. `CombatLootEngine` lost
its whole drain that way and equipment stopped dropping for every player on the
live server while kills, XP, gold, the codex and gathering all kept working; it
never reproduced locally because the trigger was Supabase's session pooler
refusing the sixteenth client (`EMAXCONNSESSION`, `pool_size: 15`) against
Npgsql's default pool of 100. Isolate every dequeued item in its own try/catch,
and bound the pool below the server's limit (`ConnectionStringDefaults
.WithBoundedPool`) so back-pressure is a queue rather than a throw. Five other
cron loops still have no catch at all - see
`docs/drop_rates_investigation_2026_09_05.md`.

**An unbounded drain in a worker loop is a starvation bug.** `CombatLootEngine`
drains two queues in one loop; the gathering half was `while (queue.TryDequeue)`
with an `await` inside, which terminates only when the producer pauses. It does
not: a harvest enqueues one grant PER ROLL, the roll count is scaled by the
codex yield multiplier, and that multiplier was **71.9x** on a live account -
about 290 grants a second against a worker that could write thirty. Equipment
stopped for everyone with a gatherer online and only arrived at a relogin, when
the queue finally emptied. Take a BUDGET (the depth read once at the top of the
cycle), coalesce what can be added up, and report every queue's depth in the
heartbeat. `GatheringGrantStarvationTests` guards the shape.

**Never name a local binding `derived` in a Svelte file.** It shadows the
`$derived` rune, so the compiler reads `$derived(...)` in the same file as a
store auto-subscription and the component throws `store_invalid_shape` at
RUNTIME. `svelte-check` passes it - it is legal TypeScript - so the only thing
that catches it is loading the page. It took out the whole Character screen
once, and the symptom was three unrelated exercise checks failing downstream.

**Every multiplier declares a cap or a curve, and `PowerCeilingTests` is the
ledger.** Nothing had ever computed what a maxed character multiplies up to, so
two runaways (codex yield 71.9x, codex damage 142.8x) were both found by a
player rather than by CI. The ledger prints every lever against its documented
maximum and asserts three things: total headroom stays within 0.5x-10x of the
monster ladder it has to climb, no single lever exceeds the product of all the
others, and any lever without a hard cap is a measurably diminishing curve.
Linear-and-uncapped is the one shape that is never allowed. It found uncapped
crit chance on its first run.

**An INDEX on the wire is a two-sources-of-truth surface.** Auto-reroll's
"stop on stat" travels as a 1-based index into `AffixRegistry.Definitions`
because `ClientCommandPacket` is fixed-layout, so the client's
`KNOWN_AFFIX_IDS` is that ordering written down twice. Ten of its twelve entries
had drifted: picking crit chance sent the index the server reads as
`range_dmg_pct`, weapon-only, so the run was refused before it rolled once and
the feature looked dead. `serverMirrors.test.ts` compares the two element by
element now. An ordered list that crosses the wire as a position needs a
mechanical guard, not a comment.

**Three paths grow a level, and each one has to be told separately.**
`ProgressionEngine` (a kill), `SimulationEngine.ApplyBulkExperience` (warp) and
`OfflineSimulationEngine.ApplyCombatXp` (away). That file's comments record the
XP formula diverging, then the skill point diverging - and the attributes were
the third: offline levels granted no STR/DEX/CON/LCK at all, which in an idle
game is most levels. The live account sat at level 86 with a fresh
registration's 50/50/50/25. `AttributeGrowthTests` guards all three.

**A stat that scales with the same term as its own constant is inert.** Monster
armour was `10 * regionTier` and its halving constant `30 * regionTier`, so the
tier cancelled out of `raw * K / (K + armour)` and every monster in the game
mitigated exactly 25% - authored, validated at start-up, read on every swing,
and incapable of distinguishing a Field Mouse from Malakor. Beside it,
`DodgeRating` was 0 on all 25 canonical monsters (the 68 legacy ones they
replaced had real values), so `HitChance` sat on its 0.95 ceiling and DEX bought
nothing. Both are derived from a monster's rank in its region now
(`MonsterDefenceCurve`, applied in `ContentRegistry.Initialize`), and
`CombatIdentityTests` fails if the canon ever goes flat again.

**A number a test PRINTS is not a number a test CHECKS.**
`ProgressionRateTests` had been printing "the strongest regular takes 104% of
the geared health bar per second" in region 5 for months. Player HP was linear
in level, monster attack is geometric per region, and the curves crossed in
region 3 - a region-5 regular two-shot a fully geared character and the boss's
single blow exceeded the whole bar. The fix is
`ProgressionEngine.BaseMilliHpForLevel`; the guard is that the same block now
asserts the share stays in a band. When you add a measurement to a test, assert
on it or it is decoration.

**A multiplier with no ceiling becomes the economy.** `CachedCodexYieldMultiplier`
is +0.5% per codex level, a codex level is ten kills, and nothing bounded the
sum - it reached 71.9x and made one character out-earn the entire material sink
of the game twice an hour. It is capped at 2.0x now
(`CodexEngine.MaxYieldMultiplier`); the DAMAGE multiplier beside it is the same
formula, is at 142x, and is deliberately still open. `GatheringEconomyTests`
prints supply and every sink in the same units - read it before touching a yield
or speed curve.

**Grep for a WRITER as well as a reader.** The recurring "computed but never
consumed" trap has an inverse that is just as bad: `IsAffixLocked` was read in
ten places - reroll, fusion, the validator, both removal paths - and set to true
by nothing, so none of it could ever run. A thoroughly wired read side is not
evidence a feature exists.

**Do not edit multi-line C# initialisers by blind string replacement.** One
such edit inserted a field into the middle of a four-term sum and silently
re-parented three bonuses onto the wrong field. It compiled and every test
passed. Read the surrounding lines first.

**If a screen looks empty on the dev fixture, suspect the fixture.** It has
silently lacked a building, a lineage row and character sexes before now, none
of which log anything. `DevFixtureInvariantTests` is where that suspicion gets
written down.

**The fixture cannot verify the new-player experience, by construction.** It is
level 40, geared, stocked and an *admin* — so it has already done everything
onboarding asks and is allowed everything the client asks about. It hid a closed
entrance for as long as it existed: a brand-new account that followed
onboarding's own first instruction died to the first monster in the game and the
tutorial stalled there forever. Anything a new player meets has to be checked by
registering one; `exercise.mjs` does that in its own browser context at the end.

**A check that spends fixture state passes once and fails forever.** Three
`exercise.mjs` steps had been red on a working game for a long time because they
marked a flag nothing clears, re-equipped the item already worn, or consumed the
last villager a later step needed. Make a check round-trip and restore what it
touched. The villager pool refills only on an explicit `--seed-dev`, which is
idempotent — re-seed when the breeding or village steps report a spent pool.

**Don't touch the monster ladder or the balance curve casually.** Both are
measured by tests that print their tables (`ProgressionRateTests`,
`GatheringShareTests`, `MonsterLadderTests`), and the reasoning behind every
number is recorded in the backlog. `MonsterLadderTests` pins the *shape*, not
the values — retune freely, but the ladder may never descend.

## Conventions

- **`// Modul:` comments** (1600+ of them) explain *why*, especially where
  something is counter-intuitive or was a bug once. Match this when you write
  non-obvious code. Don't add ordinary restate-the-code comments.
- **Four pre-existing `svelte-check` errors are not a regression** — `defend`,
  `attackShard`, `takeTurn` and `damageDelta` in `GuildOps.svelte`, the
  handlers of the Guild War UI that was hidden rather than removed. Deleting
  them is a product decision (Guild Wars is on the roadmap), not a cleanup.
  Treat the count as a weak guard: it sat at 9 for a long time and silently
  turned over into a different 9. CI fails only when it grows.
- Prose in the repo is English. `docs/CHANGELOG_ANTIGRAVITY.md` and
  `docs/guild_buffs_and_donations.md` are Czech and written by a different
  tool; the Czech changelog lags git and is not authoritative.
- Recorded test counts in the docs disagree (182 / 341 / 470 appear in
  different places). Run the suite; don't quote a number from a document.

## What is enforced for you

Three hooks in `.claude/hooks/` (Python — this machine has no `jq`) make the
rules above mechanical rather than remembered:

| Hook | Fires | Does |
|---|---|---|
| `guard_stale_build.py` | before any shell command | **Blocks** `dotnet build`/`dotnet test` while `FolkIdle.Server.exe` runs |
| `packet_struct_guard.py` | after editing a wire struct | Reminds you about the size guard and `generate:protocol` |
| `validate_content.py` | after editing `server/GameData/*.json` | **Blocks** on validation failure |

Both blocking hooks fail OPEN — if the check itself cannot run, the work
proceeds. A guard with no workaround is worse than the trap it guards.

CI (`.github/workflows/deploy.yml`) gates the image push on both the server
suite and a `client` job that runs `generate-protocol.mjs --check`. That check
is the mechanical answer to packet drift; treat a failure there as "someone
changed a struct and did not regenerate", not as a flaky step.

## Where the documentation is

- **`docs/architecture/NEXT_STEPS_BACKLOG.md`** — the living handoff. Read the
  top ~145 lines first: the current state, what's open, and "Standing traps".
  It's 134 KB; do not read it whole.
- `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` — technical layout, tick
  architecture, table-name overrides, known dead code.
- `docs/architecture/LONG_GAME_SPEC.md` — cross-season progression. §6 is
  status, §7 says which balance is deliberately left alone.
- **`docs/TASK_BOARD.md`** — the next block of work: seven scoped tasks with
  acceptance criteria. Several are half-built already, so read the "what is
  actually true" section of a task before estimating it.
- `docs/FUTURE_PLANS.md` — ideas only, nothing committed.
- `ops/oracle/README.md` — deployment, and the two firewalls that both must be
  open.

Update the relevant doc when a structural change lands. A half-built system
that looks finished is how this project has shipped its worst defects.

## Deploying

```bash
ssh folkidle-server
cd ~/folkidle && git pull && cd ops/oracle
docker compose up -d --build
docker compose logs -f app
```

**Vite inlines the server address into the bundle**, so changing the hostname
is a rebuild, not a restart.

**Migrations run on the container ENTRYPOINT (`--migrate && exec ...`), not on
app start** — so deploys apply them, and `run-dev.ps1` does not. After adding a
migration, apply it locally by hand or the next sign-in hangs with no shell
while `/gamedata` still answers 200:

```powershell
$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --migrate
```

Some migrations are not additive.
