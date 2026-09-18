import './helpers/nodeLocalStorage';
import { describe, it, expect, vi } from 'vitest';
import { QueryClient, QueryObserver } from '@tanstack/svelte-query';
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
  second, uncoordinated timer to race - which is what the fix relies on when
  it deletes each screen's own timers instead of adding a second coordination
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

    // Modul: invalidateQueries() only REFETCHES a query with an active
    // observer by default (its own default is refetchType: 'active') - a
    // bare prefetchQuery() populates the cache but leaves nothing subscribed,
    // so invalidation would just mark it stale and this test would prove
    // nothing. An active `createQuery(...)` on a mounted screen is exactly
    // this kind of observer; this stands one up without mounting a component.
    const observer = new QueryObserver(client, { queryKey: INVENTORY_KEY });
    const unsubscribe = observer.subscribe(() => {});

    await vi.waitFor(() => expect(client.getQueryData(INVENTORY_KEY)).toBe('BEFORE'));

    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0); // primes the watermark

    // The server's broadcast that the command resolved.
    processCommandResults(packet([[0, 1], [0, 0], [0, 0], [0, 0]]), 100, feed, client);

    // invalidateQueries() schedules the refetch as a microtask rather than
    // firing it synchronously, so this settles within a tick or two - well
    // before the 750ms guessed-delay window checked below.
    await vi.waitFor(() => expect(calls).toBe(2)); // the invalidation started a second fetch

    resolveSecond('AFTER');
    await vi.waitFor(() => expect(client.getQueryData(INVENTORY_KEY)).toBe('AFTER'));

    // THE ASSERTION THAT MATTERS: with the redundant per-screen setTimeout
    // gone, nothing else is scheduled to refetch this key. Waiting past the
    // OLD 700ms guessed delay must not start a third fetch or read anything
    // stale back over the value the ack already delivered.
    await new Promise((resolve) => setTimeout(resolve, 750));
    expect(calls).toBe(2);
    expect(client.getQueryData(INVENTORY_KEY)).toBe('AFTER');

    unsubscribe();
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
