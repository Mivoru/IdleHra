<script lang="ts">
  // Task 54: a player's face - their worn avatar (or, by default, their main
  // character's race portrait) inside their worn frame. One component for
  // every place a face is shown, so "how big, and where not" lives here and
  // in the callers' choice of size, nowhere else.
  import { onMount } from 'svelte';
  import CosmeticFrame from './CosmeticFrame.svelte';
  import { avatarIcon, raceIcon } from './sprites';
  import { loadCosmeticCatalogue, type CosmeticCatalogue } from '../net/cosmetics';

  interface Props {
    avatarId: string | null;
    frameId: string | null;
    raceId?: number;
    female?: boolean;
    /** sm 28px (rows, chat), md 44px, tile 64px (wardrobe grid), lg 96px (profile). */
    size?: 'sm' | 'md' | 'tile' | 'lg';
    /** Shown as a letter when there is no picture at all. */
    name?: string;
  }

  let { avatarId, frameId, raceId = 0, female = false, size = 'sm', name = '' }: Props = $props();

  let catalogue = $state<CosmeticCatalogue | null>(null);
  onMount(() => {
    loadCosmeticCatalogue()
      .then((c) => (catalogue = c))
      .catch(() => {});
  });

  const art = $derived(avatarId ? (catalogue?.Items.find((d) => d.Id === avatarId)?.Art ?? null) : null);
  const src = $derived(avatarIcon(art) ?? (raceId > 0 ? raceIcon(raceId, female) : null));
  const initial = $derived((name.trim()[0] ?? '?').toUpperCase());
</script>

<span class="avatar {size}" data-testid="avatar">
  {#if src}
    <img {src} alt="" loading="lazy" decoding="async" />
  {:else}
    <span class="initial" aria-hidden="true">{initial}</span>
  {/if}
  <!-- Task 108: only a face shown on its own pulses its frame; a row of them
       (chat, boards, a roster) glows still. -->
  <CosmeticFrame {frameId} animate={size === 'lg'} />
</span>

<style>
  .avatar {
    position: relative;
    display: inline-block;
    flex-shrink: 0;
    border-radius: 50%;
    vertical-align: middle;
  }

  .sm {
    width: 28px;
    height: 28px;
  }

  .md {
    width: 44px;
    height: 44px;
  }

  .tile {
    width: 64px;
    height: 64px;
  }

  .lg {
    width: 96px;
    height: 96px;
  }

  img,
  .initial {
    position: absolute;
    /* The portrait sits inside the frame's ring: radius 40 of a 100 box. */
    inset: 10%;
    width: 80%;
    height: 80%;
    border-radius: 50%;
    object-fit: cover;
    object-position: 50% 25%;
    background: #2a241d;
  }

  .initial {
    display: grid;
    place-items: center;
    color: var(--text-dim);
    font-weight: 600;
    font-size: 0.8em;
  }

  /* A glow on every row of a chat log would be a lot of moving pixels for a
     28px face; the small size stays still. */
  .sm :global(.glow) {
    animation: none;
  }
</style>
