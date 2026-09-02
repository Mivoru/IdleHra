<script lang="ts">
  // Modul: an item as a picture, framed by its rarity.
  //
  // The frame is the point. Fourteen quality tiers already have colours, but
  // until now they only tinted a line of text, which is the weakest possible
  // carrier - a player scanning an inventory reads shapes before words. A
  // coloured border on a square is visible at a glance and at any size.
  //
  // About a fifth of the catalogue has no artwork (rings, amulets and several
  // gloves and helmets were never drawn). Those get a tile with the item's
  // initials in the same rarity colour, which is honestly "no picture" rather
  // than a generic box that reads as a broken image.

  import { itemIcon, initialsFor } from './sprites';
  import { rarityColor, rarityName, shouldGlow } from './rarity';
  import { loadContent, type ContentRegistry } from '../net/content';
  import { onMount } from 'svelte';

  interface Props {
    baseItemId: string;
    /** Display name, used for the tooltip and the fallback initials. */
    name: string;
    qualityTier?: number;
    size?: 'sm' | 'md' | 'lg';
    /** Stack count, drawn in the corner when above one. */
    quantity?: number;
  }

  const { baseItemId, name, qualityTier = 0, size = 'md', quantity }: Props = $props();

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  const url = $derived(itemIcon(baseItemId));
  const color = $derived(rarityColor(qualityTier));
  const glow = $derived(shouldGlow(qualityTier));
  const regionTier = $derived(registry?.itemsByBaseId.get(baseItemId)?.RegionTier ?? 0);

  // The title carries the rarity WORD, so the tier is never colour-only.
  const title = $derived(
    [
      name,
      qualityTier > 0 ? ` - ${rarityName(qualityTier)}` : '',
      regionTier > 0 ? ` (Tier ${regionTier})` : ''
    ].join('')
  );
</script>

<span class="icon" data-size={size} style="--rarity: {color}" class:glow {title}>
  {#if url}
    <!-- Lazy because an inventory can render fifty of these at once, and
         decoding them all synchronously stalls the first paint. -->
    <img src={url} alt="" loading="lazy" decoding="async" />
  {:else}
    <span class="fallback" aria-hidden="true">{initialsFor(name)}</span>
  {/if}

  {#if quantity !== undefined && quantity > 1}
    <span class="qty">{quantity > 9999 ? `${Math.floor(quantity / 1000)}k` : quantity}</span>
  {/if}

  {#if regionTier > 0}
    <span class="tier" aria-hidden="true">T{regionTier}</span>
  {/if}
</span>

<style>
  /* Modul: ONE CELL, EXPLICITLY THE WHOLE TILE.
     Without these two tracks the single implicit row and column are `auto`,
     which makes them indefinite - so the `height: 100%` on the img below has
     nothing to resolve against and falls back to intrinsic sizing. A sprite
     taller than it is wide (`birch wand.webp`, 470x512) then came out 25.7px
     inside a 23.6px tile and `overflow: hidden` quietly shaved 2px off the top
     and bottom of the artwork, on every small icon in the Chest, the Market
     and the drop tables. `object-fit: contain` was already asking for a
     letterbox; it just needed a box with a known height. */
  .icon {
    position: relative;
    display: inline-grid;
    grid-template: 100% / 100%;
    place-items: center;
    flex: none;
    border: 1px solid var(--rarity);
    border-radius: var(--radius);
    background: var(--bg);
    overflow: hidden;
    line-height: 0;
  }

  .icon[data-size='sm'] {
    width: 1.6rem;
    height: 1.6rem;
  }
  .icon[data-size='md'] {
    width: 2.6rem;
    height: 2.6rem;
  }
  .icon[data-size='lg'] {
    width: 4.5rem;
    height: 4.5rem;
  }

  img {
    width: 100%;
    height: 100%;
    object-fit: contain;
  }

  .fallback {
    font-size: 0.62em;
    font-weight: 700;
    line-height: 1;
    letter-spacing: 0.03em;
    color: var(--rarity);
  }

  .icon[data-size='sm'] .fallback {
    font-size: 0.58rem;
  }
  .icon[data-size='md'] .fallback {
    font-size: 0.8rem;
  }
  .icon[data-size='lg'] .fallback {
    font-size: 1.2rem;
  }

  /* Only the top tiers glow, or the effect stops meaning anything - the same
     threshold the text glow uses, so a legendary reads as legendary whichever
     way it is drawn. */
  .glow {
    box-shadow: 0 0 6px var(--rarity);
  }

  @media (prefers-reduced-motion: reduce) {
    .glow {
      box-shadow: 0 0 4px var(--rarity);
    }
  }

  .qty {
    position: absolute;
    right: 0;
    bottom: 0;
    padding: 0 0.18rem;
    font-size: 0.58rem;
    line-height: 1.25;
    font-variant-numeric: tabular-nums;
    background: var(--bg);
    color: var(--text);
    border-top-left-radius: 4px;
  }

  .tier {
    position: absolute;
    left: 0;
    bottom: 0;
    padding: 0 0.18rem;
    font-size: 0.58rem;
    line-height: 1.25;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    background: var(--bg);
    color: var(--text-dim, #888);
    border-top-right-radius: 4px;
    opacity: 0.95;
    z-index: 1;
  }
</style>
