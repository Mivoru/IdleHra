---
name: run-stack
description: Start the FolkIdle stack locally (Postgres, Redis, game server, web client) and get to a playable browser session. Use when asked to run the game, start the server, reproduce a bug by hand, or when a screen "looks broken" and needs looking at.
---

# Running FolkIdle locally

## The one command

```powershell
.\run-dev.ps1
```

It starts the `folk-idle-db` and `folk-idle-redis` containers, kills stale
`dotnet` processes, sets `FOLKIDLE_WEB_ORIGINS` and `FOLKIDLE_DB_CONN`, starts
the server on `:8080`, **polls `/gamedata` until the gateway actually answers**,
then starts Vite on `:5173` and opens a browser.

Prefer it over starting the halves by hand. Each thing it does exists because
its absence produced a confusing failure once.

Sign in as the dev fixture: `dev@folkidle.local` / `FolkIdleDev123!`
A guest account owns nothing, so most features answer "there is nothing to do
here" and read as broken.

If that account has never been seeded on this machine:

```powershell
$env:FOLKIDLE_ALLOW_DEV_SEED=1
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --seed-dev
```

`--seed-dev` seeds and then **exits** — it is not a way to start the server.

## When it does not come up

Work down this list before reading code. Every entry has cost someone hours.

**"Could not reach the server" in the UI, server log looks healthy.**
`FOLKIDLE_WEB_ORIGINS` is unset or wrong, so every request is refused by CORS.
This reads exactly like a dead server. It must be `http://localhost:5173`.

**Your change is not in the running build.** A server still holding the output
directory makes `dotnet build` *succeed* while emitting a stale DLL. Kill
`dotnet` first — this is why `run-dev.ps1` does it unconditionally:

```powershell
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
```

**The server never answers `/gamedata`.** It reconstructs every stored session
before opening the gateway, so process-exists is not readiness. Read its own
window; the script already waited two minutes.

**Docker is down.** Recovery is `docker desktop stop` then `docker desktop start`.

**A screen renders but is empty.** Suspect the dev fixture before the screen.
It has silently lacked a building, a `character_lineage_registry` row and
character sexes before, none of which log anything.
`DevFixtureInvariantTests` is where that suspicion belongs once confirmed.

## Pointing tools at something else

`exercise.mjs` and the smoke scripts take `FOLKIDLE_E2E_BASE`; it defaults to
`http://localhost:5173/`. The live game is at https://folkidle.duckdns.org —
do not run write-heavy scripts against it.
