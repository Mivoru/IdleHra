# client_web

**The client.** Not "the direction this project is going" any more - the Unity
client in `client/` is retired, and this is what players actually run. The game
is live at https://92-5-0-94.sslip.io, served from the same origin as the API
(see `ops/oracle/README.md`).

Twenty-four screens, from combat and gathering through the forge, the market and
the guild to the Book of Deeds, the Hall of Ancestors and breeding. `client/`
survives only as the home of the shared artwork and audio, which this client
fetches from the server rather than duplicating.

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
~190 fields would be the largest two-sources-of-truth surface in this project,
and that is this codebase's dominant bug class.

```bash
npm run generate:protocol                  # regenerate (committed, so a fresh
                                           # checkout builds without the SDK)
node scripts/generate-protocol.mjs --check # CI: fails if a struct changed
```

## How a change here is verified

Four layers, and the last two are the ones that catch real defects:

```bash
npx vitest run                             # pure logic, 227 tests
npx svelte-check --tsconfig ./tsconfig.json
node scripts/exercise.mjs                  # drives a real browser, 73 checks
npm run check:overlap                      # hit-tests controls on every screen
```

`exercise.mjs` signs in as the dev fixture and CLICKS things, then asserts the
world changed - the child appeared on the roster, the slot freed, the next feast
cost more. `smoke-screens.mjs` only proves a screen renders, which a screen full
of dead buttons does perfectly.

`check:overlap` answers a question none of the others can: **is any control
sitting on top of another one**. Both elements exist, both are "visible", the
DOM is well formed, and only the geometry is wrong - so a type check and a
structural query both pass. It asks `elementFromPoint` at five points per
control, at desktop and phone widths.

Do not rewrite it to compare bounding boxes. That was the first version and it
reported 309 pairs, nearly all of them false: a list with `overflow: auto`
gives its scrolled-out children real rects that land on whatever is painted
below the list.

Its standing finding is the floating chat handle covering controls on narrow
screens - real, but a UX decision (move chat into the nav) rather than a
layout bug.

**Nine `svelte-check` errors are pre-existing** and unrelated to any current
work (Combat/Forge "used before declaration", Market/Forge index-signature
variance, one unused variable). A count of nine is not a regression.

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

## Wire details that are easy to get wrong

Each of these was got wrong first, and found in a real browser rather than by
reading. They are listed because the failure mode in every case was silence.

| Contract | The trap |
|---|---|
| An affix KEY is `id[#stack][@rarity]` | `crit_dmg_pct@2` is the id `crit_dmg_pct` at rarity 2. Testing the raw key for a `_pct` suffix fails, and a +6.0% affix renders as "+60". |
| Affix magnitudes carry TWO units | Flat affixes are whole points, `_pct` affixes are TENTHS of a percent. The discriminator is the `_pct` suffix, not a `flat_` prefix - `armor_pen_flat` exists. |
| `EquipItem` names the ITEM, `UnequipItem` names the SLOT | `TargetId` is an instance id for one and a slot index 0-6 for the other. Sending an instance id to unequip addresses slot 42 and is silently ignored. |
| `UpdateAutoEatThreshold` rides on `LimitPrice` | Not `TargetId`. And `LimitPrice` defaults to 0, so getting it wrong does not merely fail - it silently sets the threshold to zero. Out-of-range values DISCONNECT rather than clamp. |
| `resolveSlotIndex` test ORDER is the contract | 60 real items carry the generic `_armor_slot_` marker as well as their specific one. Testing the generic first files every helmet, glove, boot and legging into the chest slot. |
| Gathering ids are 1000-4999 | 101-412 was the pre-move numbering and those are MONSTERS now, because the two id spaces once collided and gathering moved. |
| `ProfessionType` is TWO different enums | Gathering: 0 Woodcutting…3 Herbalism. Crafting: 2 Smelting…5 Alchemy. Values 2 and 3 are valid in both and mean different things, so sharing a lookup labels a Copper Bar "Fishing". |
| `LimitPrice` carries THREE unrelated things | A market price, the auto-eat threshold, and the reroll's affix index. |

## The guarded command layer

`src/lib/net/commands.ts` exists because **the server's answer to an invalid
economy command is to disconnect you** - `TerminateSessionForSecurity`, not a
rejection code. A mis-typed price or two dropdowns defaulting to the same item
would end the session with no explanation.

So every Phase 3 command goes through a function that checks the server's own
precondition first and refuses to send. Screens never call `connection.send`
for these. The most dangerous is fusion: `ValidateFusionCommand` disconnects if
*any two of the three item ids match*, which is exactly what a naive
three-dropdown UI produces on first use.

## Command results

`StateUpdate` carries a four-slot ring buffer of `CommandResultCode`s, and the
client surfaces them as toasts. Without it every rejected command is a button
that does nothing and says nothing - which is the state the ring buffer was
added to end.

It is a RING, not a scalar, so a client that missed one broadcast while two
commands were rejected still sees both. `ResultTick` is monotonic and never
reset, so the client tracks a watermark; binding to "code != 0" would toast the
same rejection on every packet forever, and replaying the buffer on connect
would toast commands issued minutes ago.

## What is deliberately not here

Per the plan: no `ObfuscatedValue` (security theatre in a browser), no
zero-allocation discipline, no object pooling, no Addressables, no scene
builder. The two RenderTexture viewers become plain images - there are no 3D
assets in this project at all.
