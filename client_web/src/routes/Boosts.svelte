<script lang="ts">
  // Modul: temporary power - consumables, the buffs they leave behind, and the
  // chrono bank that converts offline time into progress.
  //
  // One screen rather than three because they are one decision: everything
  // here is a resource you spend now for an advantage that expires. Splitting
  // them would hide the fact that a potion's duration and the chrono lock are
  // both timers competing for the same session.
  //
  // None of this existed in the web client before the 2026-08-02 audit - eight
  // real consumables, both potion slots, the buff timer and a seven-day time
  // bank were all unreachable.

  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchInventory } from '../lib/net/rest';
  import { loadContent, consumableKind, prettifyBaseId, type ContentRegistry } from '../lib/net/content';
  import {
    consumeConsumable,
    activateChronoBoost,
    consumeTimeWarpCore,
    CHRONO_MULTIPLIERS,
    MAX_BANKED_CHRONO_SECONDS,
    MAX_BUFF_TICKS,
  } from '../lib/net/commands';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { play } from '../lib/ui/audio';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const snap = $derived($playerState);
  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  let registry = $state<ContentRegistry | null>(null);
  $effect(() => {
    void loadContent().then((loaded) => (registry = loaded));
  });

  // ---------------------------------------------------------------------------
  // Consumables held
  // ---------------------------------------------------------------------------

  interface HeldConsumable {
    itemId: number;
    baseId: string;
    kind: 'food' | 'offensive' | 'defensive';
    quantity: number;
    attack: number;
    defense: number;
  }

  const held = $derived.by((): HeldConsumable[] => {
    if (!registry) return [];
    const stacks = inventory.data?.Stacks ?? [];
    const rows: HeldConsumable[] = [];

    for (const stack of stacks) {
      const kind = consumableKind(stack.ItemId);
      if (kind === null) continue;
      const definition = registry.itemsByBaseId.get(stack.ItemId);
      if (!definition) continue;

      // Modul: BOTH HALVES. This counted only the backpack, on the reasoning
      // that a stash quantity "is in the bank and cannot be consumed without
      // withdrawing it first". That has not been true since storage became one
      // unbounded village chest: every spend goes through
      // InventoryAndStashSystem.TryConsumeUnifiedAsync, which draws from
      // CommodityRecords AND VillageStashInstances and only refuses when the
      // SUM is short.
      //
      // So the reasoning was inverted - hiding the stash did not avoid a button
      // that does nothing, it hid a potion the server would have been happy to
      // drink. Same defect the larder had, and the chest already reads both.
      const quantity = stack.BackpackQuantity + stack.StashQuantity;
      if (quantity <= 0) continue;

      rows.push({
        itemId: definition.Id,
        baseId: stack.ItemId,
        kind,
        quantity,
        attack: definition.FlatAttackPower,
        defense: definition.FlatDefenseRating,
      });
    }

    return rows.sort((a, b) => a.kind.localeCompare(b.kind) || a.itemId - b.itemId);
  });

  const buffTicks = $derived(snap?.RemainingBuffDurationTicks ?? 0);
  const saturated = $derived(buffTicks > MAX_BUFF_TICKS);

  function use(row: HeldConsumable) {
    const outcome = consumeConsumable(row.itemId, buffTicks);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    play('windowOpen');
  }

  // ---------------------------------------------------------------------------
  // Active effects
  // ---------------------------------------------------------------------------

  const offensiveId = $derived(snap?.ActiveOffensivePotionId ?? 0);
  const defensiveId = $derived(snap?.ActiveDefensivePotionId ?? 0);

  function itemLabel(itemId: number): string {
    const definition = registry?.items.get(itemId);
    return definition ? prettifyBaseId(definition.BaseId) : `Item #${itemId}`;
  }

  function durationLabel(ms: number): string {
    if (ms <= 0) return 'expired';
    const seconds = Math.round(ms / 1000);
    if (seconds < 60) return `${seconds}s`;
    return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  }

  // Ticks are 10 Hz, which is the one conversion worth doing in one place -
  // reading this as seconds is how a two-hour cap looks like twelve minutes.
  const buffSeconds = $derived(Math.round(buffTicks / 10));

  // ---------------------------------------------------------------------------
  // Chrono bank
  // ---------------------------------------------------------------------------

  const banked = $derived(Number(snap?.BankedChronoSeconds ?? 0));
  const accelerating = $derived((snap?.IsChronoAccelerating ?? 0) !== 0);
  const quarantined = $derived((snap?.Quarantine_Active ?? 0) !== 0);
  const lockTicks = $derived(snap?.ActiveChronoLockExpirationTicks ?? 0);

  let warpMinutes = $state(10);

  function bankLabel(seconds: number): string {
    if (seconds <= 0) return 'empty';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    if (h >= 24) return `${Math.floor(h / 24)}d ${h % 24}h`;
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
  }

  function boost(multiplier: number) {
    const outcome = activateChronoBoost(multiplier, banked, quarantined);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    play('windowOpen');
  }

  function warp() {
    const seconds = Math.round(warpMinutes * 60);
    const outcome = consumeTimeWarpCore(seconds, banked, quarantined);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    play('levelUp');
  }

  const bankPct = $derived(Math.min(1, banked / MAX_BANKED_CHRONO_SECONDS));
</script>

<div class="grid">
  <section class="panel">
    <h2>Consumables</h2>
    <p class="dim small">
      Eight exist in the game - four foods and two potions of each kind. Only
      what is in your backpack can be used; anything in the bank has to be
      withdrawn first.
    </p>

    {#if saturated}
      <p class="warn" role="status">
        You are saturated with buff duration. Using anything now would be
        refused by disconnecting you, so the buttons stay off until the timer
        falls below the cap.
      </p>
    {/if}

    {#if inventory.isPending || !registry}
      <Skeleton />
    {:else if held.length === 0}
      <p class="dim">You are not carrying any consumables.</p>
    {:else}
      <ul class="items">
        {#each held as row (row.itemId)}
          <li>
            <span class="kind" data-kind={row.kind}>{row.kind}</span>
            <span class="name">{prettifyBaseId(row.baseId)}</span>
            <span class="stat">
              {#if row.attack > 0}<span class="atk">+{row.attack} atk</span>{/if}
              {#if row.defense > 0}<span class="def">+{row.defense} def</span>{/if}
            </span>
            <span class="qty">x{row.quantity}</span>
            <button class="tiny-btn" disabled={saturated} onclick={() => use(row)}>Use</button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Active effects</h2>

    {#if offensiveId === 0 && defensiveId === 0 && buffTicks === 0}
      <p class="dim">Nothing active.</p>
    {:else}
      <ul class="effects">
        {#if offensiveId > 0}
          <li>
            <span class="kind" data-kind="offensive">offensive</span>
            <span class="name">{itemLabel(offensiveId)}</span>
            <span class="time">{durationLabel(snap?.OffensivePotionDurationMs ?? 0)}</span>
          </li>
        {/if}
        {#if defensiveId > 0}
          <li>
            <span class="kind" data-kind="defensive">defensive</span>
            <span class="name">{itemLabel(defensiveId)}</span>
            <span class="time">{durationLabel(snap?.DefensivePotionDurationMs ?? 0)}</span>
          </li>
        {/if}
      </ul>

      {#if buffTicks > 0}
        <h3>Saturation</h3>
        <div class="bar">
          <div
            class="bar-fill"
            class:over={saturated}
            style="width: {Math.min(100, (buffTicks / MAX_BUFF_TICKS) * 100)}%"
          ></div>
          <span class="bar-label">{Math.floor(buffSeconds / 60)}m of 120m</span>
        </div>
        <p class="dim tiny">
          Buff duration accumulates rather than replacing. Past two hours the
          server stops accepting new consumables entirely.
        </p>
      {/if}
    {/if}
  </section>

  <section class="panel">
    <h2>Chrono bank</h2>
    <p class="dim small">
      Time you were offline, converted into a balance you can spend. It fills on
      its own and caps at seven days.
    </p>

    <div class="bar">
      <div class="bar-fill chrono" style="width: {bankPct * 100}%"></div>
      <span class="bar-label">{bankLabel(banked)}</span>
    </div>

    {#if quarantined}
      <p class="warn">Your account is restricted, so nothing here can be spent.</p>
    {:else}
      <h3>Acceleration</h3>
      <p class="dim tiny">
        Runs the simulation faster while the bank drains. Only 2x and 4x exist -
        the server rejects any other multiplier.
      </p>
      <div class="row">
        {#each CHRONO_MULTIPLIERS as multiplier}
          <button disabled={banked <= 0} onclick={() => boost(multiplier)}>{multiplier}x</button>
        {/each}
        {#if accelerating}
          <span class="on">accelerating</span>
        {/if}
      </div>

      {#if lockTicks > 0}
        <p class="dim tiny">Locked for another {Math.round(lockTicks / 10)}s.</p>
      {/if}

      <h3>Time warp</h3>
      <!-- Modul: time warp is NOT how offline progress is collected.
           It used to look like it, because offline catch-up was capped at
           twenty actions and everything past that was pushed into this bank -
           so a night away left the card near-empty and the bank full, and
           warping felt mandatory. Offline now runs in full for every
           character and applies itself on login. What lands here is only
           time no character could use, plus whatever login rewards and the
           season pass grant. -->
      <p class="dim tiny">
        Replays banked time at once. Time banks only when a character had
        nothing to do - what you actually farmed while away is already yours,
        and is shown when you sign in.
      </p>
      <div class="row">
        <label>
          Minutes
          <input type="number" min="1" max={Math.floor(banked / 60) || 1} bind:value={warpMinutes} />
        </label>
        <button disabled={banked <= 0} onclick={warp}>Warp</button>
      </div>
      <p class="dim tiny">
        {#if banked <= 0}
          Nothing banked. Time banks only when a character was idle while you
          were away, so an empty bank means everyone was working.
        {:else}
          Costs {Math.round(warpMinutes * 60).toLocaleString()}s of the
          {banked.toLocaleString()}s banked.
        {/if}
      </p>
    {/if}
  </section>
</div>

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
    margin: 0.3rem 0 0;
  }

  .warn {
    font-size: 0.82rem;
    color: var(--danger);
    border-left: 2px solid var(--danger);
    padding-left: 0.55rem;
    margin: 0 0 0.7rem;
  }

  .items,
  .effects {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .items li {
    display: grid;
    grid-template-columns: auto 1fr auto auto auto;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.55rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
  }

  .effects li {
    display: grid;
    grid-template-columns: auto 1fr auto;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.55rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
  }

  /* Colour AND a word, never colour alone - the kind is readable without it. */
  .kind {
    font-size: 0.62rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    padding: 0.1rem 0.35rem;
    border-radius: 999px;
    border: 1px solid currentColor;
  }

  .kind[data-kind='food'] {
    color: var(--good);
  }
  .kind[data-kind='offensive'] {
    color: var(--rarity-10);
  }
  .kind[data-kind='defensive'] {
    color: var(--accent);
  }

  .name {
    font-size: 0.88rem;
    min-width: 0;
  }

  .stat {
    display: flex;
    gap: 0.35rem;
    font-size: 0.72rem;
    font-variant-numeric: tabular-nums;
  }

  .atk {
    color: var(--rarity-10);
  }
  .def {
    color: var(--accent);
  }

  .qty,
  .time {
    font-size: 0.78rem;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  .chrono {
    background: linear-gradient(90deg, var(--rarity-13), var(--accent));
  }

  .bar-fill.over {
    background: var(--danger);
  }

  .row {
    display: flex;
    align-items: flex-end;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .on {
    font-size: 0.72rem;
    color: var(--rarity-13);
  }

  label {
    display: grid;
    gap: 0.2rem;
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  input[type='number'] {
    width: 6rem;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.55rem;
  }
</style>
