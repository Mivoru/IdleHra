<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchStoreCatalog } from '../lib/net/rest';
  import {
    purchaseLegacyUnlock,
    consumeChronoCore,
    toggleChronoAcceleration,
  } from '../lib/net/commands';
  import { prettifyBaseId } from '../lib/net/content';

  const catalog = createQuery(() => ({ queryKey: queryKeys.storeCatalog, queryFn: fetchStoreCatalog }));

  const snap = $derived($playerState);
  const quarantined = $derived(snap ? snap.Quarantine_Active !== 0 : false);

  // --- legacy shop ----------------------------------------------------------
  // Modul: LegacyPerksBitmask packs three prestige perks at byte offsets
  // 0/8/16 (LegacyPerkResolver) - XP multiplier, gold drop rate, combat speed.
  // They gate real combat maths, so the ranks are read off the wire rather
  // than tracked client-side.
  const PERKS = [
    { id: 1, name: 'XP multiplier', shift: 0 },
    { id: 2, name: 'Gold drop rate', shift: 8 },
    { id: 3, name: 'Combat speed', shift: 16 },
  ];

  function perkRank(shift: number): number {
    if (!snap) return 0;
    // The mask is a long; ranks are one byte each.
    return Number((BigInt(snap.LegacyPerksBitmask) >> BigInt(shift)) & 0xffn);
  }

  function buyPerk(unlockId: number) {
    const outcome = purchaseLegacyUnlock(unlockId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  // --- chrono bank ----------------------------------------------------------
  let coreItemId = $state(0);

  function useCore() {
    const outcome = consumeChronoCore(coreItemId, quarantined);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  function setSpeed(value: number) {
    const outcome = toggleChronoAcceleration(value);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

{#if !snap}
  <p class="dim pad">Waiting for state...</p>
{:else}
  <div class="grid">
    <section class="panel">
      <h2>Diamond packages</h2>
      <p class="dim small">
        You hold {snap.PremiumCurrencyBalance.toLocaleString()} diamonds.
      </p>

      {#if catalog.isPending}
        <p class="dim">Loading...</p>
      {:else if catalog.isError}
        <p class="err">{catalog.error?.message}</p>
      {:else}
        <ul class="rows">
          {#each catalog.data ?? [] as entry (entry.ProductId)}
            <li>
              <span class="name">{prettifyBaseId(entry.ProductId)}</span>
              <span class="amount">{entry.DiamondAmount.toLocaleString()} diamonds</span>
            </li>
          {/each}
        </ul>
      {/if}

      <!-- Modul: purchases are NOT wired, deliberately. The catalog carries no
           price, and the whole flow - storefront listing, platform receipt,
           /api/v1/billing/verify-receipt - needs a real store SDK behind it.
           The port plan schedules that for Capacitor packaging, and a "Buy"
           button that cannot take money would be worse than none. Receipt
           verification is also the endpoint the Unity client never called at
           all, which is a real revenue risk rather than cosmetics. -->
      <p class="dim tiny">
        Buying is not available in the browser build. The catalogue carries no
        price - real-money purchase needs a platform store SDK and receipt
        verification, which arrives with the mobile packaging.
      </p>
    </section>

    <section class="panel">
      <h2>Legacy shop</h2>
      <p class="dim small">
        {snap.LegacyShardBalance.toLocaleString()} shards
        &middot; {snap.CitizenMultiSlotsUnlocked} citizen slots unlocked
      </p>

      <ul class="rows">
        {#each PERKS as perk}
          <li>
            <span class="name">{perk.name}</span>
            <span class="dim tiny">rank {perkRank(perk.shift)}</span>
            <button class="tiny-btn" onclick={() => buyPerk(perk.id)}>Buy rank</button>
          </li>
        {/each}
      </ul>
      <p class="dim tiny">
        Perks are bought with prestige shards and raise combat maths directly -
        the server prices and applies each rank.
      </p>
    </section>

    <section class="panel">
      <h2>Chrono bank</h2>

      <dl class="stats">
        <div><dt>Banked</dt><dd>{snap.VisualBankedChronoSeconds.toLocaleString()}s</dd></div>
        <div>
          <dt>Accelerating</dt>
          <dd>{snap.IsChronoAccelerating ? `${snap.CurrentSimulationSpeedMultiplier}x` : 'no'}</dd>
        </div>
      </dl>

      <h3>Speed</h3>
      <div class="speeds">
        {#each [1, 2, 3, 4] as value}
          <button
            class:active={snap.CurrentSimulationSpeedMultiplier === value}
            onclick={() => setSpeed(value)}
          >
            {value}x
          </button>
        {/each}
      </div>
      <p class="dim tiny">1x turns acceleration off. Banked seconds pay for the rest.</p>

      <h3>Consume a chrono core</h3>
      <div class="row">
        <input type="number" min="1" placeholder="Item id" bind:value={coreItemId} />
        <button disabled={quarantined || coreItemId < 1} onclick={useCore}>Consume</button>
      </div>
      {#if quarantined}
        <p class="dim tiny">A restricted account cannot consume cores.</p>
      {/if}
    </section>
  </div>
{/if}

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(19rem, 1fr));
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

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
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
    margin: 0.4rem 0 0;
  }
  .pad {
    padding: 1rem;
  }
  .err {
    color: var(--danger);
  }

  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .rows li {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.28rem;
  }

  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .amount {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.5rem;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .speeds {
    display: flex;
    gap: 0.3rem;
  }

  .speeds button.active {
    border-color: var(--accent);
    color: var(--accent);
  }

  .row {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.4rem;
  }

  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
