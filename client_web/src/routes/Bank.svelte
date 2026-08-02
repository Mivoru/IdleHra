<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchBank, fetchInventory, type InventoryEquipment } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { depositToBank, withdrawFromBank } from '../lib/net/commands';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import { rarityColor, rarityName, shouldGlow } from '../lib/ui/rarity';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';

  const client = useQueryClient();
  const bank = createQuery(() => ({ queryKey: queryKeys.bank, queryFn: fetchBank }));
  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  const snap = $derived($playerState);

  // Only carried items can be deposited - a worn piece has to come off first.
  const depositable = $derived(
    (inventory.data?.Equipment ?? []).filter((e: InventoryEquipment) => !e.IsEquipped),
  );

  function refreshBoth() {
    // Both sides move together on every operation, so both are invalidated -
    // refreshing only one is how a vault ends up showing an item that is also
    // still in the backpack.
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.bank });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 600);
  }

  function deposit(instanceId: number) {
    const outcome = depositToBank(instanceId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refreshBoth();
  }

  function withdraw(bankRowId: number) {
    const outcome = withdrawFromBank(bankRowId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refreshBoth();
  }
</script>

<div class="grid">
  <section class="panel">
    <h2>Backpack</h2>
    {#if snap}
      <p class="dim small">
        {snap.InventoryCapacity - snap.InventorySpaceRemaining}/{snap.InventoryCapacity} slots used.
        Depositing frees a slot; withdrawing needs a free one.
      </p>
    {/if}

    {#if inventory.isPending}
      <p class="dim">Loading...</p>
    {:else if depositable.length === 0}
      <p class="dim">Nothing carried to deposit.</p>
    {:else}
      <ul class="items">
        {#each depositable as item (item.Id)}
          <li>
            <ItemIcon
              baseItemId={item.BaseItemId}
              name={prettifyBaseId(item.BaseItemId)}
              qualityTier={item.QualityTier}
              size="sm"
            />
            <span
              style="color: {rarityColor(item.QualityTier)}"
              class:rarity-glow={shouldGlow(item.QualityTier)}
            >
              {prettifyBaseId(item.BaseItemId)}
            </span>
            <span class="dim tiny">[{rarityName(item.QualityTier)}]</span>
            <button class="tiny-btn" onclick={() => deposit(item.Id)}>Deposit</button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Vault</h2>
    <p class="dim small">
      Stored equipment. Withdrawing addresses the vault row, not the item -
      a distinction the wire cares about even though the player never sees it.
    </p>

    {#if bank.isPending}
      <p class="dim">Loading...</p>
    {:else if bank.isError}
      <p class="err">{bank.error?.message}</p>
    {:else if (bank.data ?? []).length === 0}
      <p class="dim">The vault is empty.</p>
    {:else}
      <ul class="items">
        {#each bank.data ?? [] as entry (entry.Id)}
          <li>
            <ItemIcon
              baseItemId={entry.BaseItemId}
              name={prettifyBaseId(entry.BaseItemId)}
              qualityTier={entry.QualityTier}
              size="sm"
            />
            <span
              style="color: {rarityColor(entry.QualityTier)}"
              class:rarity-glow={shouldGlow(entry.QualityTier)}
            >
              {prettifyBaseId(entry.BaseItemId)}
            </span>
            <span class="dim tiny">
              [{rarityName(entry.QualityTier)}]{#if entry.IsAffixLocked}&nbsp;· locked{/if}
            </span>
            <button
              class="tiny-btn"
              disabled={snap !== null && snap.InventorySpaceRemaining <= 0}
              title={snap !== null && snap.InventorySpaceRemaining <= 0 ? 'Backpack full' : ''}
              onclick={() => withdraw(entry.Id)}
            >
              Withdraw
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
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
  .err {
    color: var(--danger);
  }

  .items {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
    max-height: 30rem;
    overflow-y: auto;
  }

  .items li {
    display: grid;
    grid-template-columns: auto 1fr auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
