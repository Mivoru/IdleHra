<script lang="ts">
  // Modul: "COULD NOT LOAD" IS AN ANSWER, AND "NOTHING HERE" IS A DIFFERENT ONE.
  //
  // Thirteen screens created a query and never read `isError`, so a failed
  // request fell through to whatever came next in the {#if} chain - the empty
  // branch or the skeleton. The chest said "Nothing here." to a player whose
  // request had 500'd, on a game whose players remember losing 17,836 items to
  // a real incident; the guild list said "No guilds exist yet. Create the
  // first."; the statistics panel shimmered for ever. Every one of those is a
  // claim about the player's world made from the absence of an answer.
  //
  // This is the error half of QueryState, separate so a screen whose data
  // branch is too large to re-nest can drop it into its own {#if} chain as one
  // branch. `stale` is the other case TanStack has: a refetch failed but the
  // last good data is still on screen, so the line says "refresh" rather than
  // "load" and sits above the content instead of replacing it.

  interface RetryableQuery {
    readonly error: unknown;
    readonly isFetching: boolean;
    refetch: () => unknown;
  }

  interface Props {
    query: RetryableQuery;
    /** A noun phrase for the copy: "Could not load {what}." */
    what: string;
    /** The data on screen is the last good copy; only the refresh failed. */
    stale?: boolean;
  }

  const { query, what, stale = false }: Props = $props();

  const message = $derived(
    query.error instanceof Error ? query.error.message : query.error ? String(query.error) : '',
  );
</script>

<div class="query-error" class:stale role="status" data-testid="query-error">
  <p class="warn">
    {stale ? `Could not refresh ${what} - showing the last copy.` : `Could not load ${what}.`}
    {#if message}<span class="detail">{message}</span>{/if}
  </p>
  <button class="tiny-btn" disabled={query.isFetching} onclick={() => query.refetch()}>
    {query.isFetching ? 'Retrying...' : 'Retry'}
  </button>
</div>

<style>
  .query-error {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.7rem;
    margin: 0.3rem 0 0.6rem;
  }

  .query-error.stale {
    font-size: 0.85rem;
  }

  .warn {
    margin: 0;
    flex: 1 1 12rem;
    color: var(--warn);
  }

  /* The server's own words, for a bug report - dim so the sentence above is
     what a player reads. */
  .detail {
    display: block;
    font-size: 0.72rem;
    color: var(--text-dim);
    overflow-wrap: anywhere;
  }

  button {
    flex-shrink: 0;
  }
</style>
