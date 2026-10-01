<script lang="ts" generics="T">
  // Modul: THE FOUR STATES OF A QUERY, IN ONE ORDER, ON EVERY SCREEN.
  //
  // pending -> error -> empty -> data. Screens wrote the first, third and
  // fourth and skipped the second, so a failed request read as "you have
  // nothing" or as a skeleton that never resolves (see QueryError for the
  // cases). Writing the order down once means a new screen cannot forget a
  // branch, and tests/queryStates.test.ts fails any file that creates a query
  // and handles neither `isError` nor this.
  //
  // Error only REPLACES the content when there is no data. A failed background
  // refetch keeps the last good copy on screen with a line above it: hiding a
  // player's chest because a refresh blipped would be the same lie in reverse.
  //
  // `isEmpty` is a predicate rather than a length check because most screens
  // decide emptiness from a derived, filtered list, not from the raw payload.

  import type { Snippet } from 'svelte';
  import Skeleton from './Skeleton.svelte';
  import QueryError from './QueryError.svelte';

  interface QueryLike<D> {
    readonly data: D | undefined;
    readonly error: unknown;
    readonly isPending: boolean;
    readonly isError: boolean;
    readonly isFetching: boolean;
    refetch: () => unknown;
  }

  interface Props {
    query: QueryLike<T>;
    /** A noun phrase for the error copy: "Could not load {what}." */
    what: string;
    /** True when the loaded data has nothing to show. */
    isEmpty?: (data: T) => boolean;
    /** Rendered when `isEmpty` says so. Keep the screen's own helpful copy here. */
    empty?: Snippet;
    /** Passed to the Skeleton shown while the first response is in flight. */
    rows?: number;
    variant?: 'line' | 'row';
    children: Snippet<[T]>;
  }

  const { query, what, isEmpty, empty, rows, variant, children }: Props = $props();
</script>

{#if query.isPending}
  <Skeleton {rows} {variant} />
{:else if query.data === undefined}
  <!-- Not pending and no data: the request failed with nothing cached, so
       there is no answer to show. Say so rather than show an empty list. -->
  <QueryError {query} {what} />
{:else}
  {#if query.isError}
    <QueryError {query} {what} stale />
  {/if}
  {#if isEmpty?.(query.data)}
    {#if empty}
      {@render empty()}
    {:else}
      <p class="dim">Nothing here.</p>
    {/if}
  {:else}
    {@render children(query.data)}
  {/if}
{/if}

<style>
  .dim {
    color: var(--text-dim);
  }
</style>
