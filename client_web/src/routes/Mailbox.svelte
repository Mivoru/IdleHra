<script lang="ts">
  // Modul: the mailbox. Absent from this client entirely until the 2026-08-02
  // protocol audit - which mattered more than a missing screen usually does,
  // because mail is how the server delivers things it could not put straight
  // into a full backpack. Rewards that overflowed were not lost, they were
  // sitting somewhere the player had no way to look.

  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchMailbox, type MailboxEntry } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { claimMailItem } from '../lib/net/commands';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import { rarityColor, rarityName, shouldGlow } from '../lib/ui/rarity';
  import { play } from '../lib/ui/audio';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const client = useQueryClient();
  const mailbox = createQuery(() => ({ queryKey: queryKeys.mailbox, queryFn: fetchMailbox }));

  const snap = $derived($playerState);
  const entries = $derived(mailbox.data ?? []);

  // Claiming an attachment needs somewhere to put it. The server does not say
  // "your backpack is full" - it just declines to move the item, leaving the
  // mail sitting there looking unclaimed, so the screen says it first.
  const spaceRemaining = $derived(snap?.InventorySpaceRemaining ?? 0);
  const noSpace = $derived(spaceRemaining <= 0);

  function claim(entry: MailboxEntry) {
    if (entry.HasEquipmentAttachment && noSpace) {
      return pushLocalNotice('Free a backpack slot first - this message carries an item.');
    }

    const outcome = claimMailItem(entry.Id);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    play('lootDropped');
    // The mailbox row disappears server-side and the inventory grows, so both
    // are refreshed. The delay matches the other screens: the command travels
    // by WebSocket and the list by HTTP, so an immediate refetch races the
    // simulation tick that applies it.
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.mailbox });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 600);
  }

  function claimAll() {
    // Deliberately sequential rather than a burst: the server's token bucket
    // throttles inbound commands and a flood infraction is recorded against
    // the account, so a "claim 40 messages" button that fires 40 commands in
    // one frame looks exactly like an attack.
    const claimable = entries.filter((e) => !e.HasEquipmentAttachment || !noSpace);
    if (claimable.length === 0) return pushLocalNotice('Nothing can be claimed right now.');

    claimable.slice(0, 10).forEach((entry, index) => {
      setTimeout(() => claimMailItem(entry.Id), index * 250);
    });
    play('lootDropped');
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.mailbox });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, claimable.length * 250 + 800);
  }

  function received(epochSeconds: number): string {
    return new Date(epochSeconds * 1000).toLocaleString();
  }

  const totalGold = $derived(entries.reduce((sum, e) => sum + Number(e.GoldAttachment), 0));
</script>

<div class="wrap">
  <section class="panel">
    <header class="head">
      <h2>Mailbox</h2>
      {#if entries.length > 0}
        <span class="count">{entries.length} unclaimed</span>
      {/if}
    </header>

    <p class="dim small">
      Only unclaimed mail appears here - the server filters out anything already
      taken, so everything below is actionable and there is no read/unread state
      to track.
    </p>

    {#if noSpace}
      <p class="warn" role="status">
        Your backpack is full. Messages carrying an item cannot be claimed until
        you free a slot; gold-only messages still can.
      </p>
    {/if}

    {#if mailbox.isPending}
      <Skeleton />
    {:else if mailbox.isError}
      <p class="warn">Could not load the mailbox.</p>
    {:else if entries.length === 0}
      <p class="dim">No mail waiting.</p>
    {:else}
      <div class="actions">
        <button onclick={claimAll}>Claim up to 10</button>
        {#if totalGold > 0}
          <span class="gold">{totalGold.toLocaleString()}g waiting</span>
        {/if}
      </div>

      <ul class="mail">
        {#each entries as entry (entry.Id)}
          <li>
            <div class="what">
              {#if entry.BaseItemId}
                <ItemIcon
                  baseItemId={entry.BaseItemId}
                  name={prettifyBaseId(entry.BaseItemId)}
                  qualityTier={entry.QualityTier}
                  quantity={entry.Quantity}
                  size="sm"
                />
                <span
                  class="name"
                  style="color: {rarityColor(entry.QualityTier)}"
                  class:rarity-glow={shouldGlow(entry.QualityTier)}
                >
                  {prettifyBaseId(entry.BaseItemId)}
                </span>
                {#if entry.QualityTier > 0}
                  <span class="dim tiny">[{rarityName(entry.QualityTier)}]</span>
                {/if}
                {#if entry.Quantity > 1}
                  <span class="qty">x{entry.Quantity}</span>
                {/if}
              {:else}
                <span class="name">Gold delivery</span>
              {/if}

              {#if entry.GoldAttachment > 0}
                <span class="gold">+{Number(entry.GoldAttachment).toLocaleString()}g</span>
              {/if}
            </div>

            {#if entry.SenderName || entry.MessageText}
              <div class="message-content">
                {#if entry.SenderName}
                  <span class="sender">From: {entry.SenderName}</span>
                {/if}
                {#if entry.MessageText}
                  <p class="text">{entry.MessageText}</p>
                {/if}
              </div>
            {/if}

            <span class="dim tiny when">{received(entry.ReceivedTimestamp)}</span>

            <button
              class="tiny-btn"
              disabled={entry.HasEquipmentAttachment && noSpace}
              onclick={() => claim(entry)}
            >
              Claim
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
  .wrap {
    padding: 1rem;
    max-width: 46rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
  }

  h2 {
    margin: 0 0 0.4rem;
    font-size: 1.05rem;
  }

  .count {
    font-size: 0.75rem;
    color: var(--accent);
    border: 1px solid var(--accent);
    border-radius: 999px;
    padding: 0.05rem 0.5rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .tiny {
    font-size: 0.72rem;
  }

  .warn {
    font-size: 0.82rem;
    color: var(--danger);
    border-left: 2px solid var(--danger);
    padding-left: 0.55rem;
    margin: 0 0 0.7rem;
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 0.7rem;
    margin-bottom: 0.6rem;
  }

  .mail {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .mail li {
    display: grid;
    grid-template-columns: 1fr auto auto;
    align-items: center;
    gap: 0.6rem;
    padding: 0.45rem 0.6rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
  }

  .what {
    display: flex;
    align-items: baseline;
    gap: 0.4rem;
    flex-wrap: wrap;
    min-width: 0;
  }

  .name {
    font-weight: 600;
    font-size: 0.9rem;
  }

  .qty {
    font-size: 0.78rem;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  /* Gold has its own token; borrowing rarity 12 here was a coincidence of the
     palette that broke the moment a tier 12 item sat next to a price. */
  .gold {
    font-size: 0.8rem;
    color: var(--gold);
    font-variant-numeric: tabular-nums;
  }

  .when {
    white-space: nowrap;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.55rem;
  }

  .message-content {
    grid-column: 1 / -1;
    background: rgba(255, 255, 255, 0.03);
    padding: 0.5rem;
    border-left: 2px solid var(--border);
    border-radius: 0 4px 4px 0;
    margin-top: 0.25rem;
    margin-bottom: 0.25rem;
  }

  .message-content .sender {
    display: block;
    font-size: 0.75rem;
    color: var(--text-dim);
    margin-bottom: 0.2rem;
  }

  .message-content .text {
    margin: 0;
    font-size: 0.85rem;
    color: var(--fg);
    white-space: pre-wrap;
  }

  @media (max-width: 30rem) {
    .mail li {
      grid-template-columns: 1fr auto;
    }
    .when {
      grid-column: 1 / -1;
    }
  }
</style>
