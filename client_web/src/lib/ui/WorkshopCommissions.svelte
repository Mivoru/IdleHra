<script lang="ts">
  // Modul: WORKSHOP COMMISSIONS (task 83) - the material sink.
  //
  // A commission makes one region piece at a rarity FLOOR set by the Crafting
  // Workshop's level, with one affix the player picks, over 1-8 hours of real
  // time, for the region's own wood and ore in the tens of thousands.
  //
  // Everything on this panel is the SERVER's answer (GET /api/v1/workshop):
  // the floor, the duration, every price line and what the player holds. The
  // panel decides nothing - the Village screen once priced upgrades from its
  // own copy of the tier table and quoted a price the server did not charge.
  //
  // Pickers are BUTTONS, never a <select>: an Android WebView draws a select as
  // a system dialog that a re-render closes (CLAUDE.md), and this panel
  // re-renders every second while a commission counts down. The countdown is
  // plain text beside the controls, never inside one.
  import { onDestroy } from 'svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchWorkshop,
    placeCommission,
    collectCommission,
    type WorkshopView,
    type WorkshopResultName,
  } from '../net/rest';
  import { invalidateOwnedItems } from '../net/queryClient';
  import { prettifyBaseId } from '../net/content';
  import { pushLocalNotice } from '../stores/game';
  import { formatNumber } from './format';
  import { affixLabel, toDisplayAffixes } from './affixes';
  import { rarityColor, rarityName } from './rarity';
  import Bar from './Bar.svelte';

  const client = useQueryClient();
  const workshop = createQuery(() => ({ queryKey: queryKeys.workshop, queryFn: fetchWorkshop }));
  const w = $derived(workshop.data);

  // The server's clock minus ours, taken whenever a view arrives, so the
  // countdown is the server's even on a phone whose clock is off.
  let skew = $state(0);
  let nowMs = $state(Date.now());
  $effect(() => {
    if (w) skew = w.NowEpoch - Date.now() / 1000;
  });
  const timer = setInterval(() => (nowMs = Date.now()), 1000);
  onDestroy(() => clearInterval(timer));

  const serverNow = $derived(Math.floor(nowMs / 1000 + skew));
  const running = $derived(w?.Commission ?? null);
  const remaining = $derived(running ? Math.max(0, running.CompletionEpoch - serverNow) : 0);
  const ready = $derived(running !== null && remaining === 0);

  let region = $state(0);
  let itemId = $state(0);
  let affixId = $state('');
  let working = $state(false);

  const openRegions = $derived((w?.Regions ?? []).filter((r) => r.Unlocked));
  // Default to the deepest region the player has opened - the one whose gear
  // they are chasing.
  const chosenRegion = $derived(
    openRegions.find((r) => r.Region === region) ?? openRegions[openRegions.length - 1] ?? null,
  );
  const chosenPiece = $derived(chosenRegion?.Pieces.find((p) => p.ItemId === itemId) ?? null);
  const chosenAffix = $derived(chosenPiece && chosenPiece.Affixes.includes(affixId) ? affixId : '');

  const REFUSALS: Record<WorkshopResultName, string> = {
    Ok: '',
    WorkshopNotBuilt: 'Build the Crafting Workshop in the Village first.',
    UnknownPiece: 'The Workshop cannot make that piece.',
    RegionLocked: 'Beat that region’s boss before commissioning its gear.',
    IllegalAffix: 'That affix cannot go on that piece.',
    Busy: 'The Workshop is already working on a commission.',
    NotEnoughMaterials: 'Not enough materials for that commission.',
    Restricted: 'This account is restricted.',
    NothingToCollect: 'There is nothing to collect.',
    NotReady: 'The commission is not finished yet.',
    NotFound: 'Your account could not be read.',
  };

  function duration(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = seconds % 60;
    if (h > 0) return `${h} h ${String(m).padStart(2, '0')} min`;
    if (m > 0) return `${m} min ${String(s).padStart(2, '0')} s`;
    return `${s} s`;
  }

  function settle(view: WorkshopView) {
    client.setQueryData(queryKeys.workshop, view);
    // A commission spends materials and a collection adds a piece: both the
    // chest's halves are stale now (invalidateOwnedItems names both keys).
    invalidateOwnedItems(client);
    client.invalidateQueries({ queryKey: queryKeys.recipes });
    client.invalidateQueries({ queryKey: queryKeys.villageQuote });
  }

  async function commission() {
    if (!chosenPiece || !chosenAffix || working) return;
    working = true;
    try {
      const view = await placeCommission(chosenPiece.ItemId, chosenAffix);
      settle(view);
      if (view.Result === 'Ok') {
        pushLocalNotice(
          `Commissioned ${prettifyBaseId(chosenPiece.BaseItemId)} with ${affixLabel(chosenAffix)}.`,
          'info',
        );
      } else {
        pushLocalNotice(REFUSALS[view.Result ?? 'NotFound'] || 'The commission was refused.', 'error');
      }
    } catch {
      pushLocalNotice('The commission did not go through. Nothing was spent - try again.', 'error');
    } finally {
      working = false;
    }
  }

  async function collect() {
    if (working) return;
    working = true;
    try {
      const view = await collectCommission();
      settle(view);
      if (view.Result === 'Ok' && view.Collected) {
        pushLocalNotice(
          `${view.Collected.RarityName} ${prettifyBaseId(view.Collected.BaseItemId)} collected - it is in your chest.`,
          'info',
        );
      } else {
        pushLocalNotice(REFUSALS[view.Result ?? 'NotFound'] || 'Nothing was collected.', 'error');
      }
    } catch {
      pushLocalNotice('Collecting did not go through - try again.', 'error');
    } finally {
      working = false;
    }
  }

  const collectedAffixes = $derived.by(() => {
    const c = w?.Collected;
    if (!c) return [];
    try {
      return toDisplayAffixes(JSON.parse(c.AffixPayload) as Record<string, number>);
    } catch {
      return [];
    }
  });
</script>

<section class="panel workshop" data-testid="workshop-commissions">
  <div class="head">
    <h2>Workshop commissions</h2>
    {#if w}
      <span class="dim tiny">
        Workshop {w.WorkshopLevel}/{w.MaxWorkshopLevel}
        {#if w.WorkshopFloorTier > 0}
          &middot; floor <strong style="color: {rarityColor(w.WorkshopFloorTier)}">{rarityName(w.WorkshopFloorTier)}</strong>
        {/if}
      </span>
    {/if}
  </div>

  <p class="dim small">
    Order one piece of a region's gear. It comes out at least at the Workshop's
    rarity floor, carries one affix you choose (at Common strength), and takes
    real time - it finishes while you are away. A better Workshop raises the
    floor; no floor ever reaches the gear a region's boss expects.
  </p>

  {#if workshop.isPending}
    <p class="dim tiny">Reading the Workshop&hellip;</p>
  {:else if workshop.isError || !w}
    <p class="warn">The Workshop could not be loaded.</p>
  {:else if running}
    <div class="order" data-testid="workshop-running">
      <p>
        <strong>{prettifyBaseId(running.BaseItemId)}</strong>
        &middot; {affixLabel(running.ChosenAffixId)}
        &middot; floor <span style="color: {rarityColor(running.FloorTier)}">{running.FloorName}</span>
      </p>
      <Bar
        value={serverNow - running.StartedEpoch}
        max={Math.max(1, running.CompletionEpoch - running.StartedEpoch)}
        color="var(--good)"
      />
      <p class="dim tiny" data-testid="workshop-countdown">
        {ready ? 'Finished.' : `Ready in ${duration(remaining)}.`}
      </p>
      <button class="collect primary" data-testid="workshop-collect" disabled={!ready || working} onclick={collect}>
        {working ? 'Collecting…' : 'Collect'}
      </button>
    </div>
  {:else if w.WorkshopLevel === 0}
    <p class="warn">Build the Crafting Workshop in the Village to take commissions.</p>
  {:else}
    {#if openRegions.length > 1}
      <div class="chips" role="group" aria-label="Region">
        {#each openRegions as r (r.Region)}
          <button
            class="chip"
            class:on={chosenRegion?.Region === r.Region}
            onclick={() => {
              region = r.Region;
              itemId = 0;
              affixId = '';
            }}
          >
            Region {r.Region}
          </button>
        {/each}
      </div>
    {/if}

    {#if chosenRegion}
      <p class="small">
        Region {chosenRegion.Region}: floor
        <strong style="color: {rarityColor(chosenRegion.FloorTier)}">{chosenRegion.FloorName}</strong>,
        takes {duration(chosenRegion.DurationSeconds)}.
      </p>
      <div class="mats" data-testid="workshop-cost">
        {#each chosenRegion.Cost as line (line.ItemId)}
          <span class:short={line.Held < line.Quantity}>
            {prettifyBaseId(line.ItemId)} {formatNumber(line.Held)}/{formatNumber(line.Quantity)}
          </span>
        {/each}
      </div>

      <p class="dim tiny label">Piece</p>
      <div class="chips" role="group" aria-label="Piece" data-testid="workshop-pieces">
        {#each chosenRegion.Pieces as piece (piece.ItemId)}
          <button
            class="chip"
            class:on={chosenPiece?.ItemId === piece.ItemId}
            onclick={() => {
              itemId = piece.ItemId;
              if (!piece.Affixes.includes(affixId)) affixId = '';
            }}
          >
            {prettifyBaseId(piece.BaseItemId)}
          </button>
        {/each}
      </div>

      {#if chosenPiece}
        <p class="dim tiny label">Affix</p>
        <div class="chips" role="group" aria-label="Affix" data-testid="workshop-affixes">
          {#each chosenPiece.Affixes as id (id)}
            <button class="chip" class:on={chosenAffix === id} onclick={() => (affixId = id)}>
              {affixLabel(id)}
            </button>
          {/each}
        </div>
      {/if}

      <button
        class="commission primary"
        data-testid="workshop-commission"
        disabled={!chosenPiece || !chosenAffix || !chosenRegion.Affordable || working}
        onclick={commission}
      >
        {#if !chosenRegion.Affordable}
          Not enough materials
        {:else if !chosenPiece}
          Pick a piece
        {:else if !chosenAffix}
          Pick an affix
        {:else}
          {working ? 'Commissioning…' : `Commission (${duration(chosenRegion.DurationSeconds)})`}
        {/if}
      </button>
    {:else}
      <p class="dim tiny">No region is open yet.</p>
    {/if}
  {/if}

  {#if w?.Collected}
    <p class="collected" data-testid="workshop-collected">
      Last collected:
      <strong style="color: {rarityColor(w.Collected.QualityTier)}">
        {w.Collected.RarityName} {prettifyBaseId(w.Collected.BaseItemId)}
      </strong>
      {#each collectedAffixes as a (a.key)}
        <span class="dim tiny"> &middot; {a.label} {a.value}</span>
      {/each}
    </p>
  {/if}
</section>

<style>
  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    display: grid;
    gap: 0.5rem;
    margin-bottom: 1rem;
  }

  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 1rem;
    flex-wrap: wrap;
  }

  h2 {
    margin: 0;
    font-size: 1.05rem;
  }

  p {
    margin: 0;
    line-height: 1.4;
    max-width: 78ch;
    overflow-wrap: anywhere;
  }

  .order {
    display: grid;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem;
    background: var(--bg);
    border-radius: var(--radius);
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
  }

  .chip {
    font-size: 0.75rem;
    padding: 0.25rem 0.5rem;
    flex-shrink: 0;
    max-width: 100%;
    overflow-wrap: anywhere;
    text-align: left;
  }

  .chip.on {
    border-color: var(--accent);
    color: var(--accent);
    font-weight: 600;
  }

  .label {
    margin-top: 0.2rem;
  }

  .mats {
    display: grid;
    gap: 0.1rem;
    font-size: 0.75rem;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
  }

  .mats .short {
    color: var(--danger);
  }

  .commission,
  .collect {
    justify-self: start;
    flex-shrink: 0;
  }

  .primary {
    border-color: currentColor;
    font-weight: 600;
  }

  .collected {
    font-size: 0.85rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .warn {
    color: var(--warn);
  }
</style>
