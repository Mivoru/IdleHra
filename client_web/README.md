# client_web

The browser client. Phase 1 of `docs/architecture/WEB_CLIENT_PORT_PLAN.md` -
a vertical slice, not a port: log in, pick a monster, fight it, watch health,
loot and progress. Four screens of forty-nine.

**The Unity client in `client/` is still the shipping client.** Nothing here
may break it, and nothing here is a reason to change it.

## Running it

```bash
docker start folk-idle-db folk-idle-redis

cd server && FOLKIDLE_WEB_ORIGINS="http://localhost:5173" \
  FOLKIDLE_DB_CONN="Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres" \
  dotnet run --project FolkIdle.Server/FolkIdle.Server.csproj

cd client_web && npm install && npm run dev
```

The dev port is pinned to 5173 (`strictPort`). The server's CORS allow-list is
exact-match, so a dev server that quietly moved to 5174 would fail every
request with an opaque browser CORS error.

## The protocol types are generated, never hand-written

`src/lib/net/protocol.generated.ts` comes from the server's own
`--dump-protocol` output, which is produced from the same reflected field plan
`PacketJsonCodec` encodes with. A hand-written mirror of `StateUpdatePacket`'s
159 fields would be the largest two-sources-of-truth surface in this project,
and that is this codebase's dominant bug class.

```bash
npm run generate:protocol                  # regenerate (committed, so a fresh
                                           # checkout builds without the SDK)
node scripts/generate-protocol.mjs --check # CI: fails if a struct changed
```

## Two wire obligations that are written down nowhere else

Both kill a session with the **same** symptom: WebSocket close 1008 "Violent
termination", and no server log line at all. If a session dies that way, it is
one of these, not the transport.

1. **Every `ClientCommand` must echo `LogicEpochCounter` from the most recent
   `StateUpdate`.** `GameConnection.send` stamps it so this cannot be
   forgotten at a call site. A brand-new account hides the bug - its counter is
   0, which is what a forgetful client sends anyway - so only a played-in
   account fails fast.
2. **Anti-cheat challenges must be answered** with opcode 31 within 15 s. Four
   consecutive misses quarantines the account, persistently, in about 60
   seconds. A 25-second test session survives and looks perfectly healthy.

`src/lib/net/antiCheat.ts` mirrors the server's hash and is tested against
vectors the **server itself** computed (`CHALLENGE_VECTORS`), because a wrong
answer is worse than no answer - it gets a real player flagged as a cheater.

A quarantined account is fed spoofed data and drops nothing, with no error
anywhere, so it looks like the loot code is broken. Lift it with the tool that
already exists - it also unfreezes the market listings the shadow ban froze,
which a bare `UPDATE PlayerRecords` does not:

```bash
dotnet server/FolkIdle.Server/bin/Debug/net8.0/FolkIdle.Server.dll --lift-quarantine <playerId>
```

## Tests

```bash
npm test        # fast: pure logic, no server needed
npm run check   # svelte-check
npm run build

# Slow, needs the server, Postgres and Redis running:
FOLKIDLE_LIVE=1 npx vitest run tests/live.integration.test.ts

# Drives the real UI in a real browser and writes smoke-*.png:
node scripts/smoke-ui.mjs smoke
```

The live test deliberately runs for 75 seconds. The anti-cheat obligation only
fires after four missed challenges, so a fast test cannot see it.

## Snapshot interpolation, and why the delay is adaptive

The port plan says `StateUpdate` arrives "~10/sec". It does not. 10 Hz is the
tick rate; `SimulationEngine` dirty-checks before dispatching, so what actually
arrives - measured in the browser with a `MutationObserver` on the health bar -
is:

```text
gaps between monster-HP changes: 2183, 1100, 2183, 1084, 2182, 1100 ms
mean 1637 ms
```

A first version took the plan's number literally: a fixed 100 ms render delay
and a fixed 1000 ms reconnect threshold. Both are wrong against a 1637 ms
cadence, and they compounded - the delay pinned `t` at 1 so nothing was ever
interpolated, and the threshold then classified ordinary combat as a reconnect
and discarded the previous snapshot anyway. **The unit tests passed and the bar
stepped in the browser**, which is the whole argument for driving the real UI.

The delay is now estimated from observed arrivals and capped at
`MAX_RENDER_DELAY_MS`. Measured after the fix: 122 distinct bar widths over 4
seconds, up from 3.

The cost is staleness - the smoothed value trails the server by up to the cap.
That is safe **only** because the authoritative snapshot is a separate store:
anything that decides reads `playerState`, and only things that animate read
`visualState`. Never decide from an interpolated value.

## What is deliberately not here

Per the plan: no `ObfuscatedValue` (security theatre in a browser), no
zero-allocation discipline, no object pooling, no Addressables, no scene
builder. The two RenderTexture viewers become plain images - there are no 3D
assets in this project at all.
