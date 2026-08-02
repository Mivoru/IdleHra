<script lang="ts">
  // Modul: unclaimed mail count for the header.
  //
  // A component rather than a `$derived` in App.svelte for a structural
  // reason: App.svelte is what MOUNTS QueryClientProvider, so its own top
  // level sits outside the query context and cannot call createQuery. Anything
  // that needs a query in the header has to live one level down, here.
  //
  // It exists at all because mail is the only thing in this game a player has
  // no other way to notice - nothing on any other screen changes when a
  // message arrives.

  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchMailbox } from '../net/rest';

  const mailbox = createQuery(() => ({
    queryKey: queryKeys.mailbox,
    queryFn: fetchMailbox,
    // Mail arrives from server-side events rather than anything this client
    // does, so a periodic check is the only way to see it. A minute is slow
    // enough to be free and fast enough that nobody notices the delay.
    refetchInterval: 60_000,
  }));

  const count = $derived(mailbox.data?.length ?? 0);
</script>

{#if count > 0}
  <span class="badge" title="{count} unclaimed message(s)">{count}</span>
{/if}

<style>
  .badge {
    font-size: 0.68rem;
    font-weight: 700;
    line-height: 1;
    min-width: 1.05rem;
    text-align: center;
    padding: 0.15rem 0.3rem;
    border-radius: 999px;
    background: var(--accent);
    color: var(--bg);
  }
</style>
