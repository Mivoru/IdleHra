<script lang="ts">
  // Modul: TASK 50 - A LEGENDARY IS AN EVENT, NOT A ROW.
  //
  // A Legendary-or-better drop used to arrive as one more line in a list most
  // players were not looking at. This card shows it over whatever screen is
  // open, for about 1.5 s (lootFeel.REVEAL_VISIBLE_MS), with the same
  // comparison and Wear the loot row has (task 49).
  //
  // NOT MODAL. It is a moment, not a question: no backdrop, nothing blocked,
  // and it leaves on its own. Pointing at it or focusing it holds it open, so
  // a player reaching for Wear does not lose the card under their finger. A
  // better drop replaces a showing one (lootFeel.shouldReplaceReveal).
  //
  // Held on pointer MOVEMENT, not pointerenter: a card that appears under a
  // resting cursor fires pointerenter with nobody reaching for it, and it
  // then never left - exercise.mjs found exactly that, because its last click
  // had parked the mouse where the card lands.
  import { createQuery } from '@tanstack/svelte-query';
  import { onMount } from 'svelte';
  import { lootReveal, dismissLootReveal, playerState } from '../stores/game';
  import { loadContent, itemName, type ContentRegistry } from '../net/content';
  import { queryKeys, fetchWorn } from '../net/rest';
  import { wearOnMain } from '../net/commands';
  import { play } from './audio';
  import { rarityColor, rarityName } from './rarity';
  import { compareDrop, comparisonLine, dropRequirement, isUpgrade } from './lootCompare';
  import { REVEAL_VISIBLE_MS } from './lootFeel';
  import Burst from './Burst.svelte';
  import ItemIcon from './ItemIcon.svelte';

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    try {
      registry = await loadContent();
    } catch {
      // No registry: the card still names the rarity, and the row has the rest.
    }
  });

  const drop = $derived($lootReveal);
  const def = $derived(drop ? (registry?.items.get(drop.itemId) ?? null) : null);

  const worn = createQuery(() => ({ queryKey: queryKeys.worn, queryFn: fetchWorn, enabled: drop !== null }));
  const cmp = $derived(def && drop && worn.data ? compareDrop(registry, def.BaseId, drop.qualityTier, worn.data.Pieces) : null);
  const req = $derived(def ? dropRequirement(registry, def.BaseId, $playerState ?? {}) : null);

  let held = $state(false);
  $effect(() => {
    if (!drop || held) return;
    const timer = setTimeout(dismissLootReveal, REVEAL_VISIBLE_MS);
    return () => clearTimeout(timer);
  });

  function wear() {
    if (!drop) return;
    wearOnMain(drop.instanceId);
    play('itemEquipped');
    held = false;
    dismissLootReveal();
  }
</script>

{#if drop}
  {#key drop.id}
    <div
      class="reveal folk-sweep"
      role="status"
      aria-live="polite"
      data-loot-reveal={drop.qualityTier}
      style="--tint: {rarityColor(drop.qualityTier)}"
      onpointermove={() => (held = true)}
      onpointerleave={() => (held = false)}
      onfocusin={() => (held = true)}
      onfocusout={() => (held = false)}
    >
      <span class="crown"><Burst color={rarityColor(drop.qualityTier)} count={14} reach={3.6} /></span>
      {#if def}
        <ItemIcon baseItemId={def.BaseId} name={itemName(registry, drop.itemId)} qualityTier={drop.qualityTier} size="md" />
      {/if}
      <div class="text">
        <p class="kicker" style="color: {rarityColor(drop.qualityTier)}">
          {rarityName(drop.qualityTier)} drop{#if drop.record}<span class="record">New record</span>{/if}
        </p>
        <p class="name rarity-glow rarity-glow-live" style="color: {rarityColor(drop.qualityTier)}">{itemName(registry, drop.itemId)}</p>
        {#if cmp}
          <p class="cmp" class:up={isUpgrade(cmp)}>
            {comparisonLine(cmp)}{#if req && !req.met}<span class="unmet"> · {req.text}</span>{/if}
          </p>
        {/if}
      </div>
      <div class="actions">
        {#if drop.instanceId > 0}
          <!-- Filled only when the comparison says it is better: a filled Wear
               next to "same base stats" pushed a sidegrade (task 109). -->
          <button class:primary={cmp !== null && isUpgrade(cmp)} disabled={req !== null && !req.met} onclick={wear}>Wear</button>
        {/if}
        <button class="close" aria-label="Close" onclick={dismissLootReveal}>×</button>
      </div>
    </div>
  {/key}
{/if}

<style>
  .reveal {
    position: fixed;
    z-index: 55;
    left: 50%;
    translate: -50% 0;
    /* Its own inset: a fixed layer is not reached by body's safe-area padding. */
    top: calc(var(--safe-area-inset-top, env(safe-area-inset-top, 0px)) + 4.5rem);
    width: min(26rem, calc(100vw - 2rem));
    display: flex;
    align-items: center;
    gap: 0.7rem;
    padding: 0.7rem 0.8rem;
    background: var(--bg-panel);
    border: 1px solid var(--tint);
    border-radius: var(--radius);
    box-shadow: 0 0 18px -4px var(--tint), 0 8px 24px rgba(0, 0, 0, 0.35);
    animation: reveal-in 220ms ease-out;
  }

  .record {
    margin-left: 0.4rem;
    padding: 0 0.3rem;
    border: 1px solid currentColor;
    border-radius: 3px;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .crown {
    position: absolute;
    left: 2.2rem;
    top: 50%;
    width: 0;
    height: 0;
  }

  /* Modul: ON A PHONE THE REVEAL STANDS ON THE TAB BAR, not under the header.
     At `top: 4.5rem` it covered the phone header's second row - the Menu
     button, the one way to most screens - until it was dismissed, and the
     two command toasts the phone stacks at the top (z 60, over this z 55)
     reached ~5.4rem and covered its item name. The bottom band 4.25rem above
     the tab bar clears the chat handle and is free of both. A desktop has
     neither problem: one header row, toasts bottom-right. */
  @media (max-width: 40rem) {
    .reveal {
      top: auto;
      bottom: calc(4.25rem + var(--sa-bottom) + var(--tabbar-h));
    }
  }

  .text {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    gap: 0.1rem;
    overflow-wrap: anywhere;
  }

  p {
    margin: 0;
  }

  .kicker {
    font-size: 0.68rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
  }

  .name {
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .cmp {
    font-size: 0.75rem;
    color: var(--text-dim);
    overflow-wrap: anywhere;
  }

  .cmp.up {
    color: var(--good);
  }

  .unmet {
    color: var(--danger);
  }

  .actions {
    display: flex;
    gap: 0.35rem;
    flex-shrink: 0;
  }

  .actions button {
    flex-shrink: 0;
  }

  @keyframes reveal-in {
    from {
      opacity: 0;
      transform: translateY(-0.6rem) scale(0.97);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .reveal {
      animation: none;
    }
  }
</style>
