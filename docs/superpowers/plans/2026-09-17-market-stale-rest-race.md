# Market stale-REST-over-WS race: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close `docs/TASK_BOARD.md` finding #23 — a REST `createQuery` cache
(`Market.svelte`'s `listings`/`inventory`/`statistics`/`history`, and the
same shape on Mailbox/Character/Larder) can be refreshed by a guessed
`setTimeout(600-700ms)` that races anything else touching the same cache, with
nothing to say which result is newer.

**The mechanism this plan recommends is already half-shipped.** Research for
this plan found that `client_web/src/lib/stores/game.ts` already has a
synchronous, command-acknowledgment-driven cache bust:
`StateUpdatePacket`'s four-slot `CommandResult` ring buffer is scanned on
every packet (`CommandResultFeed.accept`), and the moment a **new** result
appears — the server saying, on the very next tick's broadcast, "that command
is done" — the handler calls `queryClient.invalidateQueries()` with no key,
busting every REST cache the app has. The comment at `game.ts:577-593`
records why: it replaced "nine screens each guessed at this with a
setTimeout." This plan's job is therefore **not** to invent a new mechanism —
it is to (a) prove that mechanism actually beats the race, and (b) close the
specific gaps where an engine's *success* path never calls
`EnqueueCommandResult`, so a handful of commands (confirmed below) still fall
back to the old guessed-delay timer the comment describes as superseded.

**Tech Stack:** C# / .NET 8 (server engines, xUnit + Testcontainers) and
Svelte 5 + TanStack Query + vitest (`client_web`, `environment: 'node'`, no
component mounting).

**Spec:** `docs/TASK_BOARD.md` §23 ("Market (and similar REST+WebSocket
screens) can apply a stale REST result over newer WebSocket state"). Where
this plan's researched detail disagrees with the board's one-paragraph "fix
shape" — principally, that a synchronous-ack mechanism already exists and the
real work is closing engine-side coverage gaps, not building one — this
plan's detail wins; it was verified against the live source on 2026-09-17.

## Global Constraints

- **Chosen mechanism: extend the existing synchronous ack. Do NOT stamp REST
  responses with `LogicEpochCounter`.** The board names both directions;
  epoch-stamping is rejected for two independent reasons found during
  research:
  1. It would duplicate a mechanism that is already built, already shipped,
     and already proven simpler — REST invalidation firing off the server's
     own "this command resolved" signal, with **zero new wire fields**.
  2. `LogicEpochCounter` already carries a documented multi-meaning trap this
     repo has been burned by twice (`server/FolkIdle.Server/Network/ClientCommandPacket.cs:328-338`,
     `docs/architecture/WEB_CLIENT_PORT_PLAN.md:25-29`): it is simultaneously
     a save-generation counter that advances on **every checkpoint flush**
     (unrelated to any specific screen's data) and is explicitly exempted for
     two commands measured against wall-clock instead. It was the direct
     cause of a real production bug — `docs/architecture/NEXT_STEPS_BACKLOG.md:4296-4304`
     — where a flush landing between an anti-cheat challenge and its answer
     turned a correct answer into a recorded miss, because the counter
     advances for reasons that have nothing to do with what it was being
     compared against. Stamping REST responses with it and comparing against
     "the newest WS-observed epoch" would give the field a **third**
     unrelated meaning and inherit that exact flaw (it would tick over from
     an unrelated flush, not from an inventory change), which is precisely
     the "one field, two meanings" trap CLAUDE.md and this repo's own recent
     history (`BreedingSelectionMask` getting its own dedicated field rather
     than reusing `TargetVillagerSlot`) both warn against repeating.
- **Per-engine coverage, confirmed by reading each success path (2026-09-17):**

  | Engine (command) | Screen | Enqueues `CommandResult` on success today? | Screen's `setTimeout` today |
  |---|---|---|---|
  | `MarketEscrowEngine` (list/buy) | Market | **Yes** (`Success`, `MarketEscrowEngine.cs:232,487`) | Dead weight |
  | `MarketOrderBookEngine` (limit order) | Market | **No** — six silent-rollback branches and the success path all log to `Console.WriteLine` only | Load-bearing (only signal) |
  | `LarderEngine` (deposit/withdraw) | Larder | **Yes**, unconditionally (`LarderEngine.cs:172`) | Dead weight |
  | `EquipmentSlotEngine` (equip/unequip) | Character | **No** — `EquipAttemptOutcome.Success(...)` deliberately sets `ResultCode = null` (`EquipmentSlotEngine.cs:338`, with its own comment explaining only rejections were meant to report) | Load-bearing (only signal) |
  | `MailboxAndBankEngine` (mail claim) | Mailbox | **No** — `CommitMailClaimAsync` never calls `EnqueueCommandResult` on the only path it actually takes (`isSuccess` is hardcoded `true` at its one call site, `SimulationEngine.cs:1653`) | Load-bearing (only signal) |

  Market is therefore genuinely the confirmed case with a mixed picture
  (two of its three commands already fixed, one genuinely racy); Larder,
  Character and Mailbox all share the pattern the board suspected, but two
  of the three (Character, Mailbox) need a real server-side change, not
  just client cleanup.
- **Every `Toasts.svelte` entry renders unconditionally, `Success` included**
  (code 0 → "Done.", rendered for every newly-observed ring-buffer slot,
  colour-only distinction via `COMMAND_RESULT_OK_CODES`). Adding
  `EnqueueCommandResult(Success)` to a path that never sent one is a visible
  UX change, not just plumbing — every task below says so explicitly, and
  **none of them adds it inside a rapid multi-command client loop** without
  calling out the consequence, because this codebase already fixed exactly
  that shape once (auto-reroll: "an auto-reroll run is ONE event... used to
  report Success once per attempt, so a fifty-attempt run stacked fifty
  toasts" — `client_web/src/lib/stores/commandResults.ts:65-71`).
- **`client_web` runs vitest with `environment: 'node'`** (`client_web/vite.config.ts:63`)
  and no `@testing-library/svelte` — components are never mounted in this
  repo's tests. Where this plan needs to pin "a `.svelte` file no longer
  contains pattern X," it uses a source-scan test that reads the file as
  text, the established pattern in this exact repo
  (`client_web/tests/runesMode.test.ts`, `client_web/tests/serverMirrors.test.ts`),
  not a rendered-component test.
- **Every new/changed server test seeds its own `PlayerRecord` rows** and, for
  anything asserting on `CommandResultQueue`, passes a **fresh**
  `new PlayerSessionRegistry()` rather than `_fixture.PlayerRegistry` — the
  shared registry's queues can carry stray items from other tests in the same
  collection, matching `BreedingRoundOneTests.cs:116` and both prior
  2026-09-17 plans (`2026-09-17-village-gold-sync.md`,
  `2026-09-17-audit-fixes-14-17-19-20.md`).
- **Never hand-write a wire type.** This plan adds no new packet fields —
  every fix routes through the `CommandResult` ring buffer, which already
  exists on the wire.

---

### Task 1: Market — close the limit-order gap, delete the now-redundant timers, prove the mechanism

**Files:**
- Modify: `server/FolkIdle.Server/Engine/MarketOrderBookEngine.cs`
  (`PlaceLimitOrderAsync`, lines ~261-395 — six silent-rollback branches plus
  the success path)
- Modify: `client_web/src/routes/Market.svelte` (delete three `setTimeout`
  blocks: `sell()` at line 175, `buy()` at lines 184-187 including the
  now-redundant `listings.refetch()`, `placeOrder()` at line 238)
- Modify: `client_web/src/lib/stores/game.ts` (extract the
  `CommandResult` → `invalidateQueries` block, lines ~563-593, into an
  exported `processCommandResults` function — the seam the new client test
  needs; behaviour unchanged, pure extraction)
- Test (server): `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs`
  (new tests near the existing `Test_MarketOrderBook_TaxBracketsAndArchiving`
  at line 413, which is this task's model for seeding a `MarketOrderBookEngine`
  test)
- Test (client, new): `client_web/tests/commandAckInvalidation.test.ts` —
  exercises the extracted seam directly: a fresh `CommandResult` tick
  triggers exactly one cache refetch, so there is nothing left for a second,
  uncoordinated timer to race
- Test (client, new): `client_web/tests/restInvalidationTiming.test.ts` —
  source-scan pinning that `Market.svelte` no longer contains the
  `setTimeout(...invalidateOwnedItems...)` / `setTimeout(...refetch...)`
  pattern, so a future edit cannot silently reintroduce it

**Interfaces:**
- Consumes: nothing new — routes through the existing
  `PlayerSessionRegistry.EnqueueCommandResult(long playerId, byte resultCode)`
  and the existing `CommandResult0..3_Code/_Tick` wire fields.
- Produces: `client_web/src/lib/stores/game.ts`'s
  `processCommandResults(packet, arrivedAtMs, feed, client)` — an exported,
  independently-testable function. Tasks 2-4 do not depend on it directly
  (they only delete client timers and add server-side enqueues), but if a
  future screen needs the same proof, this is the seam to reuse rather than
  inventing a second seam.

- [ ] **Step 1: Write the failing server test**

In `HardenedEngineIntegrationTests.cs`, directly after
`Test_MarketOrderBook_TaxBracketsAndArchiving` (confirm its current end line
against the live file — it was ~453 as of this plan's research), add:

```csharp
        [Fact]
        public async Task Test_MarketOrderBook_LimitOrderSuccessTellsTheLiveSession()
        {
            // Modul: PlaceLimitOrderAsync's success path used to log
            // "Order placed" to the server console and nothing else -
            // Market.svelte's placeOrder() had no signal to react to besides
            // its own guessed 700ms timer. This is the fix: the same
            // CommandResultQueue signal every other market command already
            // sends.
            var registry = new PlayerSessionRegistry();
            var marketEngine = new MarketOrderBookEngine(_fixture.ServiceProvider, registry);
            const long sellerId = 950000120L;
            const string baseItemId = "eq_monolith_crown_helmet_armor_slot_base";

            long equipmentId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = sellerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                var equipment = new MarketEquipmentInstance { PlayerId = sellerId, BaseItemId = baseItemId, QualityTier = 1 };
                db.MarketEquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            await marketEngine.PlaceLimitOrderAsync(sellerId, false, equipmentId, 5000L, baseItemId, 1);

            Assert.True(registry.CommandResultQueue.TryDequeue(out var notif));
            Assert.Equal(sellerId, notif.PlayerId);
            Assert.Equal((byte)Network.CommandResultCode.Success, notif.ResultCode);
        }

        [Fact]
        public async Task Test_MarketOrderBook_LimitOrderRejectionTellsTheLiveSession()
        {
            // Modul: a SELL order on an item the player does not own (or that
            // is already locked) used to roll back silently - Console.WriteLine
            // only, nothing on the wire. CLAUDE.md's "silent rollback is this
            // server's favourite way to lie" trap, previously unrecorded for
            // this engine.
            var registry = new PlayerSessionRegistry();
            var marketEngine = new MarketOrderBookEngine(_fixture.ServiceProvider, registry);
            const long sellerId = 950000121L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = sellerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            // No MarketEquipmentInstance exists for this id at all.
            await marketEngine.PlaceLimitOrderAsync(sellerId, false, 999999999L, 5000L, "eq_monolith_crown_helmet_armor_slot_base", 1);

            Assert.True(registry.CommandResultQueue.TryDequeue(out var notif));
            Assert.Equal(sellerId, notif.PlayerId);
            Assert.Equal((byte)Network.CommandResultCode.TargetNotFound, notif.ResultCode);
        }
```

Check the exact end line of `Test_MarketOrderBook_TaxBracketsAndArchiving` and
the `Network.CommandResultCode` namespace alias this test file already uses
(other tests in the same file reference it as `Network.CommandResultCode` —
confirm, don't assume) before running.

- [ ] **Step 2: Run the server tests to verify they fail**

Stop the running server first (CLAUDE.md's stale-build rule), then:

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_MarketOrderBook_LimitOrder"`

Expected: both FAIL — `registry.CommandResultQueue.TryDequeue` returns
`false`, because `PlaceLimitOrderAsync` enqueues nothing today.

- [ ] **Step 3: Implement — `MarketOrderBookEngine.PlaceLimitOrderAsync`**

Add `_playerRegistry.EnqueueCommandResult(playerId, (byte)...)` at all seven
sites (six rollbacks, one success), mirroring `MarketEscrowEngine`'s
established pattern in this same directory. Confirm exact line numbers
against the live file (they may have drifted from this plan's 2026-09-17
research) before editing:

```csharp
                    double? buyRollingAveragePrice = await CalculateRollingAveragePriceAsync(db, baseItemId, qualityTier);
                    if (!buyRollingAveragePrice.HasValue)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                        Console.WriteLine($"BUY Order rejected: no price baseline for {baseItemId} - not in the catalogue and never traded.");
                        return;
                    }

                    {
                        double buyMinPrice = buyRollingAveragePrice.Value * 0.80;
                        double buyMaxPrice = buyRollingAveragePrice.Value * 3.00;
                        if (price < buyMinPrice || price > buyMaxPrice)
                        {
                            await transaction.RollbackAsync();
                            _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice);
                            Console.WriteLine($"BUY Order rejected: price {price} outside volatility corridor [{buyMinPrice}, {buyMaxPrice}] for {baseItemId} T{qualityTier}.");
                            return;
                        }
                    }

                    var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                    var goldRecord = await db.CommodityRecords.FromSqlRaw(goldQuery, playerId).SingleOrDefaultAsync();

                    if (goldRecord == null || goldRecord.Quantity < price)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                        Console.WriteLine("BUY Order failed: Insufficient gold.");
                        return;
                    }
```

```csharp
                    if (equip == null || equip.PlayerId != playerId || equip.IsLockedInEscrow)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                        Console.WriteLine("SELL Order failed: Item unavailable or already locked.");
                        return;
                    }
```

```csharp
                        if (price < sellMinPrice || price > sellMaxPrice)
                        {
                            await transaction.RollbackAsync();
                            _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice);
                            Console.WriteLine($"SELL Order rejected: price {price} outside volatility corridor [{sellMinPrice}, {sellMaxPrice}] for {baseItemId} T{qualityTier}.");
                            return;
                        }
```

Success, right after the commit and before the fire-and-forget match:

```csharp
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                Console.WriteLine($"Order placed: {(isBuy ? "BUY" : "SELL")} {baseItemId} T{qualityTier} @ {price}g");

                _ = MatchOrdersAsync(baseItemId, qualityTier);
```

And the outer catch:

```csharp
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                Console.WriteLine($"Order placement failed: {ex.Message}");
            }
```

**Do not touch** `MatchOrdersAsync` — it runs asynchronously after the
command that triggered it has already been acknowledged, has no single
`playerId` to answer to (it can pay out to either side of a match), and
already has its own live-session delivery path (`MarketMatchQueue`,
independent of this plan).

- [ ] **Step 4: Run the server tests to verify they pass**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_MarketOrderBook"`

Expected: all PASS, including the pre-existing
`Test_MarketOrderBook_TaxBracketsAndArchiving` (unaffected — it never
asserted on `CommandResultQueue`).

- [ ] **Step 5: Extract `processCommandResults` in `game.ts`**

Add near the top of the file, after the `import type { QueryClient } from '@tanstack/svelte-query';`
you add and above `startSession` (place it beside the other command-result
exports around line 219-225 — confirm against the live file):

```ts
/**
 * The synchronous half of the command-acknowledgment cache bust. Pulled out
 * of startSession's onStateUpdate closure into its own function so it can be
 * exercised directly by a test (tests/commandAckInvalidation.test.ts) without
 * standing up a whole WebSocket session. Behaviour is unchanged - this is a
 * pure extraction, not a rewrite.
 *
 * Modul: A COMMAND RESULT IS THE SERVER SAYING "THAT IS DONE", so it is the
 * moment every screen's data is stale. Nine screens each guessed at this with
 * a setTimeout - 400ms here, 700 there, 900 in Breeding - which is a guess
 * about how long a Serializable transaction plus a state reload takes. Too
 * short and the refetch reads the OLD rows; too long and it feels broken.
 * Invalidated globally rather than per screen because the results ring does
 * not say WHICH command it is answering, and a command a player just issued
 * can change gold, inventory, equipment and the village at once.
 */
export function processCommandResults(
  packet: Record<string, unknown>,
  arrivedAtMs: number,
  feed: CommandResultFeed,
  client: Pick<QueryClient, 'invalidateQueries'>,
): CommandResultEntry[] {
  const results = feed.accept(packet, arrivedAtMs);
  if (results.length > 0) {
    commandResults.update((entries) => [...entries, ...results]);
    // One cue per batch, not per result: the ring buffer can deliver four at
    // once and four overlapping error tones is a noise, not a signal.
    if (results.some((r) => r.code !== COMMAND_RESULT_SUCCESS)) play('error');
    client.invalidateQueries();
  }
  return results;
}
```

Add the import: `import type { QueryClient } from '@tanstack/svelte-query';`
near the top with the other imports.

Then in `onStateUpdate`, replace the inline block (~lines 563-593) with:

```ts
      processCommandResults(
        packet as unknown as Record<string, unknown>,
        arrivedAtMs,
        commandResultFeed,
        queryClient,
      );
```

(Confirmed no code after the old block reads the `results` local, so the
return value does not need to be captured at this call site.)

- [ ] **Step 6: Delete Market.svelte's three now-superseded timers**

```ts
  function sell() {
    const outcome = listItemOnMarket(sellInstanceId, sellPrice);
    if (!outcome.ok) {
      pushLocalNotice(outcome.reason);
      return;
    }
    sellInstanceId = 0;
  }

  function buy(orderId: number) {
    const outcome = buyMarketListing(orderId);
    if (!outcome.ok) {
      pushLocalNotice(outcome.reason);
      return;
    }
  }
```

and, in `placeOrder()`:

```ts
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    pushLocalNotice('Order placed. It rests until something matches it.', 'info');
```

(drop the trailing `setTimeout(() => invalidateOwnedItems(client), 700);`
line). If `invalidateOwnedItems` and `client`/`useQueryClient` end up unused
elsewhere in the file after this, remove the now-dead import — check first,
since `client` (the `QueryClient` from `useQueryClient()`) is very likely
still used elsewhere on this screen for other invalidations.

- [ ] **Step 7: New client test — the mechanism itself**

`client_web/tests/commandAckInvalidation.test.ts`:

```ts
import { describe, it, expect, vi } from 'vitest';
import { QueryClient } from '@tanstack/svelte-query';
import { processCommandResults } from '../src/lib/stores/game';
import { CommandResultFeed } from '../src/lib/stores/commandResults';

/*
  THE RACE THIS GUARDS AGAINST.

  Market.svelte used to pair the server's own "that command is done" signal
  (the CommandResult ring buffer, already driving a global invalidateQueries)
  with an independent, guessed-delay setTimeout doing the exact same
  invalidation a second time. Two uncoordinated triggers for one cache means
  whichever REFETCH resolves LAST wins the cache write, regardless of which
  was issued first or which reflects the truer state - a WS-driven refetch
  that lands early can be overwritten by a redundant timer's late one.

  This file proves the surviving mechanism is not itself racy: ONE server
  acknowledgment produces exactly ONE refetch, so there is nothing left for a
  second, uncoordinated timer to race - which is what Task 1 relies on when it
  deletes Market.svelte's own timers instead of adding a second coordination
  mechanism.
*/

function packet(slots: [number, number][]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  slots.forEach(([code, tick], i) => {
    out[`CommandResult${i}_Code`] = code;
    out[`CommandResult${i}_Tick`] = tick;
  });
  return out;
}

const INVENTORY_KEY = ['player', 'inventory'] as const;

describe('processCommandResults', () => {
  it('a fresh command result triggers exactly one refetch, and the fresher value survives', async () => {
    let calls = 0;
    let resolveSecond!: (v: string) => void;

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryDefaults(INVENTORY_KEY, {
      queryFn: () => {
        calls += 1;
        if (calls === 1) return Promise.resolve('BEFORE');
        // Mirrors the board's own 600-700ms invalidation gap: the refetch
        // triggered by the server's ack does not resolve instantly either.
        return new Promise<string>((resolve) => {
          resolveSecond = resolve;
        });
      },
    });
    await client.prefetchQuery({ queryKey: INVENTORY_KEY });
    expect(client.getQueryData(INVENTORY_KEY)).toBe('BEFORE');

    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0); // primes the watermark

    // The server's broadcast that the command resolved.
    processCommandResults(packet([[0, 1], [0, 0], [0, 0], [0, 0]]), 100, feed, client);

    expect(calls).toBe(2); // the invalidation started a second fetch immediately

    resolveSecond('AFTER');
    await vi.waitFor(() => expect(client.getQueryData(INVENTORY_KEY)).toBe('AFTER'));

    // THE ASSERTION THAT MATTERS: with the redundant per-screen setTimeout
    // gone (Task 1, Step 6), nothing else is scheduled to refetch this key.
    // Waiting past the OLD 700ms guessed delay must not start a third fetch
    // or read anything stale back over the value the ack already delivered.
    await new Promise((resolve) => setTimeout(resolve, 750));
    expect(calls).toBe(2);
    expect(client.getQueryData(INVENTORY_KEY)).toBe('AFTER');
  });

  it('does nothing on a result already seen, so a rebroadcast cannot start a redundant refetch', () => {
    let calls = 0;
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryDefaults(INVENTORY_KEY, { queryFn: () => { calls += 1; return Promise.resolve('X'); } });

    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 5], [0, 0], [0, 0], [0, 0]]), 0);
    processCommandResults(packet([[0, 5], [0, 0], [0, 0], [0, 0]]), 10, feed, client);

    expect(calls).toBe(0);
  });
});
```

- [ ] **Step 8: New client test — the pattern cannot silently come back**

`client_web/tests/restInvalidationTiming.test.ts`:

```ts
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

/*
  Modul: a ratchet nobody watches fails open. Task 1 deletes Market.svelte's
  three guessed-delay setTimeout invalidations because the server's own
  command-acknowledgment signal already covers all three commands. Nothing
  stops a future edit from reintroducing "just add a setTimeout, it's how the
  other screens do it" - this reads the file as text and refuses that pattern
  by name, the same approach tests/runesMode.test.ts and
  tests/serverMirrors.test.ts already use for a Svelte file this repo's vitest
  config cannot mount (environment: 'node', no @testing-library).
*/

const here = dirname(fileURLToPath(import.meta.url));

function readRoute(name: string): string {
  return readFileSync(join(here, '..', 'src', 'routes', name), 'utf8');
}

describe('Market.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout paired with invalidateOwnedItems, refetch, or invalidateQueries', () => {
    const source = readRoute('Market.svelte');
    const timedInvalidation =
      /setTimeout\([^)]*(invalidateOwnedItems|\.refetch\(\)|invalidateQueries)/s;
    expect(source).not.toMatch(timedInvalidation);
  });
});
```

- [ ] **Step 9: Run the full client test suite**

`cd client_web && npm test`

Expected: all PASS, including the two new files and the pre-existing
`tests/commandResults.test.ts` (unaffected — `CommandResultFeed` itself did
not change).

- [ ] **Step 10: Run `npm run exercise` against a locally running stack**

Per CLAUDE.md's verify rule — this is a gameplay-visible change (Market's
buy/sell/limit-order flow). Confirm listing, buying, and placing a limit
order all still work and the screen updates without a manual refresh.

- [ ] **Step 11: Commit**

```bash
git add server/FolkIdle.Server/Engine/MarketOrderBookEngine.cs client_web/src/routes/Market.svelte client_web/src/lib/stores/game.ts server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs client_web/tests/commandAckInvalidation.test.ts client_web/tests/restInvalidationTiming.test.ts
git commit -m "fix(market): route every market command through the existing command-ack cache bust, not a guessed timer"
```

---

## Follow-on tasks

The user's brief for this plan required spot-checking Mailbox, Character and
Larder (named by the original audit as "not individually re-checked"). All
three do share the underlying pattern (a REST cache with only a guessed
`setTimeout` invalidation) — confirmed by reading each screen's source
directly, not inferred. Two of the three need a real server-side change;
Larder does not.

### Task 2: Larder — pure client cleanup (already covered server-side)

`LarderEngine` unconditionally calls `EnqueueCommandResult` on every
deposit/withdraw outcome, success included (`LarderEngine.cs:172`, confirmed
above) — the synchronous ack already fires correctly for this screen today.
`Larder.svelte`'s `refetchSoon()` helper (`setTimeout(() => inventory.refetch(), 400)`,
called from three sites) is dead weight, not a defense: it is a second,
uncoordinated trigger on top of a mechanism that already works, and per the
same reasoning as Task 1 it is the redundant trigger capable of clobbering a
fresher value with a stale one, not a safety net.

**Files:**
- Modify: `client_web/src/routes/Larder.svelte` (delete `refetchSoon()` and
  its three call sites, lines ~72-74, 95, 109, 119 — confirm against the live
  file)
- Test (client): extend `restInvalidationTiming.test.ts` from Task 1 with a
  second `describe` block for `Larder.svelte`

- [ ] **Step 1: Extend the source-scan test first**

Add to `client_web/tests/restInvalidationTiming.test.ts`:

```ts
describe('Larder.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout calling refetch', () => {
    const source = readRoute('Larder.svelte');
    expect(source).not.toMatch(/setTimeout\([^)]*\.refetch\(\)/s);
  });
});
```

Run it — expect FAIL (the pattern is still there).

- [ ] **Step 2: Delete `refetchSoon()` and its call sites**

Confirm each of the three call sites still only needs the command send, not
a follow-up refetch, before deleting — `LarderEngine`'s success path already
enqueues `CommandResultCode.Success` unconditionally, so the global
`processCommandResults` invalidation (Task 1) covers all three.

- [ ] **Step 3: Run the client test suite**

`cd client_web && npm test` — expect the extended
`restInvalidationTiming.test.ts` to pass along with everything else.

- [ ] **Step 4: `npm run exercise`**, confirming Larder deposit/withdraw still
  update the screen without a manual refresh.

- [ ] **Step 5: Commit**

```bash
git add client_web/src/routes/Larder.svelte client_web/tests/restInvalidationTiming.test.ts
git commit -m "fix(larder): drop the guessed-delay refetch now that the server always acknowledges"
```

---

### Task 3: Character — equip/unequip's success path never told the client

`EquipmentSlotEngine.EquipAttemptOutcome.Success(notification)` deliberately
sets `ResultCode = null` (`EquipmentSlotEngine.cs:338`) — its own neighboring
comment explains only rejections were meant to report anything, written back
when nothing downstream cared about a *successful* equip's timing. That
comment is now wrong: `Character.svelte`'s `equipInstance()`/`unequip()` are
the load-bearing case (unlike Market/Larder, there is no existing coverage
to fall back on), and both still carry the exact guessed-delay `setTimeout`
this plan is closing out everywhere else.

**Files:**
- Modify: `server/FolkIdle.Server/Domain/Combat/EquipmentSlotEngine.cs`
  (line 338, one line)
- Modify: `client_web/src/routes/Character.svelte` (delete two `setTimeout`
  calls: `equipInstance()` line 339, `unequip()` line 359)
- Test (server): `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs`
  (extend or add near existing equip/unequip tests — grep
  `EquipItemAsync`/`UnequipItemAsync` for the nearest sibling test to sit
  beside)
- Test (client): extend `restInvalidationTiming.test.ts`

**Interfaces:**
- Consumes: nothing new.
- Produces: a successful equip/unequip now also produces a "Done." toast
  (`Toasts.svelte` renders every `CommandResult`, `Success` included) —
  **this is a real, visible, and accepted UX change**, not a side effect to
  silently avoid. It is low-risk here specifically because equip/unequip are
  single, deliberate, one-at-a-time actions on this screen — there is no
  rapid client-side loop of them the way `Mailbox.svelte`'s `claimAll()` has
  (see Task 4), so there is no toast-storm risk to weigh against it.

- [ ] **Step 1: Write the failing server test**

Find the nearest existing `EquipItemAsync`/`UnequipItemAsync` test in
`HardenedEngineIntegrationTests.cs` (grep for either name) and add beside it:

```csharp
        [Fact]
        public async Task Test_EquipItem_SuccessTellsTheLiveSession()
        {
            // Modul: EquipAttemptOutcome.Success used to set ResultCode to
            // null on purpose - only rejections were meant to report
            // anything - so Character.svelte's equipInstance() had no signal
            // besides its own guessed 700ms timer. This is that signal.
            var registry = new PlayerSessionRegistry();
            var engine = new EquipmentSlotEngine(_fixture.ServiceProvider, registry);
            const long testPlayerId = 950000130L;

            // TODO(implementer): seed a PlayerRecord, a character, and an
            // owned EquipmentInstance the same way the nearest existing
            // EquipItemAsync test does - copy that seeding exactly rather
            // than re-deriving it, since the equip path has several
            // preconditions (attribute gate, region gate) this plan does not
            // re-derive.

            await engine.EquipItemAsync(testPlayerId, /* characterId */ default, /* instanceId */ 0, /* slotIndex */ 0);

            Assert.True(registry.CommandResultQueue.TryDequeue(out var notif));
            Assert.Equal(testPlayerId, notif.PlayerId);
            Assert.Equal((byte)Network.CommandResultCode.Success, notif.ResultCode);
        }
```

This step is deliberately left with a `TODO` for the seeding block and the
exact `EquipItemAsync` signature — confirm both against the live file and
its nearest existing test before finishing this step; do not guess the
method signature or the preconditions (attribute/region gates) that test
already navigates.

- [ ] **Step 2: Run the server test to verify it fails**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_EquipItem_SuccessTellsTheLiveSession"`

Expected: FAILS — `registry.CommandResultQueue.TryDequeue` returns `false`.

- [ ] **Step 3: Implement — the one-line fix**

In `EquipmentSlotEngine.cs`, change:

```csharp
            public static EquipAttemptOutcome Success(EquipmentSlotUpdateNotification notification) => new(true, null, notification);
```

to:

```csharp
            public static EquipAttemptOutcome Success(EquipmentSlotUpdateNotification notification) =>
                new(true, (byte)FolkIdle.Server.Network.CommandResultCode.Success, notification);
```

`PublishOutcome` (lines 527-532) already enqueues whenever `ResultCode` has a
value — no other change needed. Update the class's doc comment at lines
321-335 (currently explains why *rejections* need a code); add a line noting
success now reports one too, so the client's cache invalidation has
something to key off.

- [ ] **Step 4: Run the server test to verify it passes**

Same filter as Step 2. Also run the full `Test_EquipItem`/`Test_UnequipItem`
suite to confirm no rejection-path test broke (it should not — `Rejected(...)`
is untouched).

- [ ] **Step 5: Delete Character.svelte's two timers**

```ts
  function equipInstance(instanceId: number) {
    if (!selected) return;
    connection.send({
      Command: CommandType.EquipItem,
      TargetId: instanceId,
      TargetGuid: selected.id,
    });
    pickerSlot = -1;
  }
```

```ts
  function unequip(slotIndex: number) {
    connection.send({
      Command: CommandType.UnequipItem,
      TargetId: slotIndex,
      TargetGuid: selected?.id ?? EMPTY_GUID,
    });
  }
```

(drop both trailing `setTimeout(() => inventory.refetch(), ...)` lines and
their preceding comments, which describe exactly the guessed-delay reasoning
this task replaces).

- [ ] **Step 6: Extend the source-scan test**

Add to `restInvalidationTiming.test.ts`:

```ts
describe('Character.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout calling refetch', () => {
    const source = readRoute('Character.svelte');
    expect(source).not.toMatch(/setTimeout\([^)]*\.refetch\(\)/s);
  });
});
```

- [ ] **Step 7: Run both suites**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj` (full
run, once) and `cd client_web && npm test`.

- [ ] **Step 8: `npm run exercise`**, confirming equip/unequip still update
  the paper doll and the backpack list without a manual refresh, and that a
  "Done." toast is the accepted new behaviour (not a defect to chase).

- [ ] **Step 9: Commit**

```bash
git add server/FolkIdle.Server/Domain/Combat/EquipmentSlotEngine.cs client_web/src/routes/Character.svelte server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs client_web/tests/restInvalidationTiming.test.ts
git commit -m "fix(character): a successful equip or unequip now acknowledges, closing the last guessed-timer gap"
```

---

### Task 4: Mailbox — the single-claim path gets the same signal; `claimAll()` is scoped out with a reason

`MailboxAndBankEngine.CommitMailClaimAsync` never calls
`EnqueueCommandResult` on its only real path (`isSuccess` is hardcoded `true`
at its one call site, `SimulationEngine.cs:1653` — the `mail == null` and
exception paths are pre-existing, unrelated silent gaps this task does not
expand into). `Mailbox.svelte` has two client call sites: `claim()` (a single
item — safe to switch) and `claimAll()` (up to ten sequential commands,
250ms apart — switching it means the server will now toast "Done." once per
claim, reproducing the exact "fifty stacked toasts" shape this codebase
already fixed once for auto-reroll).

**Decision recorded here rather than left ambiguous:** both client timers are
deleted, because after the server change lands, **both** `claim()` and
`claimAll()` will receive the `Success` signal regardless of which client
code path is kept — the server cannot distinguish "one manual claim" from
"one claim inside a ten-item batch," so there's no way to keep the signal
for one and not the other. Leaving `claimAll()`'s timer in place would only
add an eleventh, later, uncoordinated refetch on top of ten synchronous ones
— the same kind of dead weight Task 1 and Task 2 remove elsewhere. The
resulting up-to-ten-toasts-in-2.5-seconds for a full inbox sweep is a real,
visible UX cost, accepted here as an explicit, named trade-off — not a
silent regression. **Coalescing `claimAll()`'s results into one toast is
recorded as a follow-up, not built here** (see "Not in this plan").

**Files:**
- Modify: `server/FolkIdle.Server/Engine/MailboxAndBankEngine.cs`
  (`CommitMailClaimAsync`, inside the `if (isSuccess)` block, ~lines 176-211)
- Modify: `client_web/src/routes/Mailbox.svelte` (delete both `setTimeout`
  blocks: `claim()` lines 44-47, `claimAll()` lines 62-65 — the
  `client.invalidateQueries({ queryKey: queryKeys.mailbox })` calls move with
  them since the global `invalidateQueries()` from Task 1's mechanism already
  covers the mailbox key too)
- Test (server): new test in `HardenedEngineIntegrationTests.cs`, near any
  existing `ClaimMailItemAsync`/`CommitMailClaimAsync` test (grep first —
  none was found during this plan's research, so add fresh)
- Test (client): extend `restInvalidationTiming.test.ts`

- [ ] **Step 1: Write the failing server test**

```csharp
        [Fact]
        public async Task Test_MailClaim_CommitTellsTheLiveSession()
        {
            var registry = new PlayerSessionRegistry();
            var engine = new MailboxAndBankEngine(_fixture.ServiceProvider, registry);
            const long testPlayerId = 950000140L;
            long mailId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                var mail = new MailboxInstance { PlayerId = testPlayerId, GoldAttachment = 100L };
                db.MailboxInstances.Add(mail);
                await db.SaveChangesAsync();
                mailId = mail.Id;
            }

            await engine.CommitMailClaimAsync(testPlayerId, mailId, true);

            Assert.True(registry.CommandResultQueue.TryDequeue(out var notif));
            Assert.Equal(testPlayerId, notif.PlayerId);
            Assert.Equal((byte)Network.CommandResultCode.Success, notif.ResultCode);
        }
```

Check `MailboxInstance`'s actual required fields against the live model
before running — the seed above is minimal and may need more (e.g. a
non-null `BaseItemId` default) to satisfy the table's constraints.

- [ ] **Step 2: Run the test to verify it fails**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_MailClaim_CommitTellsTheLiveSession"`

Expected: FAILS — nothing in `CommandResultQueue`.

- [ ] **Step 3: Implement**

In `MailboxAndBankEngine.cs`, inside `CommitMailClaimAsync`'s
`if (isSuccess) { ... }` block, after the gold-attachment handling and before
`await db.SaveChangesAsync();` (confirm exact placement against the live
file — research read it at ~line 211):

```csharp
                    if (mail.GoldAttachment > 0)
                    {
                        // ... existing gold handling, unchanged ...
                    }

                    _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                }
                else
                {
                    mail.IsPending = false;
                }
```

Do not add an enqueue to the `mail == null` early return or the
`catch (Exception)` block in this task — both are pre-existing, silent gaps
unrelated to the REST-race this plan closes (neither is reachable from the
one real call site today, since `isSuccess` is always `true`), and expanding
into them risks scope creep beyond what this plan set out to fix. Leave a
`// Modul:` note at the `catch` block naming this as a known, deliberately
out-of-scope gap so the next reader does not assume it was missed.

- [ ] **Step 4: Run the test to verify it passes**

Same filter as Step 2.

- [ ] **Step 5: Delete Mailbox.svelte's two timers**

```ts
  function claim(entry: MailboxEntry) {
    if (entry.HasEquipmentAttachment && noSpace) {
      return pushLocalNotice('Free a backpack slot first - this message carries an item.');
    }

    const outcome = claimMailItem(entry.Id);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    play('lootDropped');
  }
```

```ts
  function claimAll() {
    const claimable = entries.filter((e) => !e.HasEquipmentAttachment || !noSpace);
    if (claimable.length === 0) return pushLocalNotice('Nothing can be claimed right now.');

    claimable.slice(0, 10).forEach((entry, index) => {
      setTimeout(() => claimMailItem(entry.Id), index * 250);
    });
    play('lootDropped');
  }
```

(`claimAll()` keeps its own internal `setTimeout` that *staggers the
outbound commands* — that one is not the invalidation timer this plan is
about and must stay, per its own comment about the server's flood-detection
token bucket; only the trailing invalidation block at the end of the
function is deleted.)

- [ ] **Step 6: Extend the source-scan test**

```ts
describe('Mailbox.svelte does not re-time-guess its cache invalidation', () => {
  it('has no setTimeout calling invalidateOwnedItems or a mailbox invalidateQueries', () => {
    const source = readRoute('Mailbox.svelte');
    const timedInvalidation =
      /setTimeout\([^)]*(invalidateOwnedItems|invalidateQueries)/s;
    expect(source).not.toMatch(timedInvalidation);
  });
});
```

(This intentionally does not forbid `setTimeout` generally in this file —
`claimAll()`'s command-staggering timer is legitimate and must remain; the
regex targets the invalidation call specifically, the same way Task 1's
version does.)

- [ ] **Step 7: Run both suites**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj` (full
run) and `cd client_web && npm test`.

- [ ] **Step 8: `npm run exercise`**, and manually confirm (dev fixture) that
  claiming a full inbox of several messages via "claim all" still empties the
  mailbox and grows the backpack without a manual refresh — the multiple
  "Done." toasts are expected, not a bug to chase.

- [ ] **Step 9: Commit**

```bash
git add server/FolkIdle.Server/Engine/MailboxAndBankEngine.cs client_web/src/routes/Mailbox.svelte server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs client_web/tests/restInvalidationTiming.test.ts
git commit -m "fix(mailbox): a claimed message now acknowledges, closing the last guessed-timer gap"
```

---

## Not in this plan

- **Epoch-stamping REST responses against `LogicEpochCounter`.** Rejected as
  the chosen mechanism — see Global Constraints. Recorded here so a future
  reader does not treat the board's two options as still open.
- **Every other screen using the same `setTimeout` + `invalidateOwnedItems`/
  `invalidateQueries` shape.** This plan's spot-check was scoped to Market,
  Larder, Character and Mailbox (the four the original audit named).
  Research for this plan also found the identical pattern in `Forge.svelte`,
  `Crafting.svelte`, `Chest.svelte`, `GuildOps.svelte`, `Progression.svelte`,
  `Social.svelte`, `Chat.svelte`, `VillageFolk.svelte`, `Breeding.svelte` and
  `Ancestors.svelte` — none of these were individually re-checked for
  whether their underlying engine already covers the success path (the way
  Larder and two of Market's three commands turned out to), and none is
  fixed here. A future pass should repeat this plan's per-engine coverage
  check (the table in Global Constraints) before assuming any of them still
  needs a server-side change.
- **`MailboxAndBankEngine`'s other silent-rollback paths** (`mail == null`,
  the outer `catch (Exception)` in `CommitMailClaimAsync`, and anything in
  the "Bank" half of that engine unrelated to mail claims) — named in Task 4
  as a known gap, not fixed, to avoid scope creep beyond the REST race this
  plan closes.
- **Coalescing `claimAll()`'s up-to-ten acknowledgments into a single client
  toast**, mirroring how auto-reroll's own multi-attempt run was consolidated
  into one result. A real follow-up, not built here — Task 4 accepts the
  toast-per-claim cost explicitly rather than building the consolidation
  mechanism this plan did not set out to add.
- **`RecruitAsync`/`AutoRerollRunner`/Forge fusion and any other
  gold-or-inventory-affecting command not named above** — out of scope,
  matching the same boundary the `2026-09-17-village-gold-sync.md` plan drew
  for its own "Not in this plan."
