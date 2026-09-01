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
```

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

**Check which material namespace a feature needs before writing code.** Several
string spaces share one `CommodityRecords` table: gathering slugs (`raw_log`,
`wood`) with no `items.json` entry, catalogued items, and a `*_crafting_material`
space. Anything keyed on `ItemDefinitionId` silently rejects the first. ORE is
settled — one per region, common and rare, listed in
`VillageManagementEngine.TierMaterials` and matched by the gathering loot tables
and the guild's buff tiers. Six defects in one day came from confusing these.

**Do not edit multi-line C# initialisers by blind string replacement.** One
such edit inserted a field into the middle of a four-term sum and silently
re-parented three bonuses onto the wrong field. It compiled and every test
passed. Read the surrounding lines first.

**If a screen looks empty on the dev fixture, suspect the fixture.** It has
silently lacked a building, a lineage row and character sexes before now, none
of which log anything. `DevFixtureInvariantTests` is where that suspicion gets
written down.

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
is a rebuild, not a restart. Migrations run themselves on app start and some
are not additive.
