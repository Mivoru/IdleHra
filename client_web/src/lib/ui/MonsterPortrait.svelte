<script lang="ts">
  // Modul: a monster as a picture.
  //
  // All 25 canonical monsters have art, matched by exact Name, so unlike items
  // this never falls back in practice - but it still does rather than assuming,
  // because a 26th monster would otherwise render as a broken image.
  //
  // The fifth monster of every region is its boss (ids 95, 100, 105, 110, 115),
  // which the art was drawn for. The frame says so, since nothing else on the
  // combat screen distinguishes a boss from the four ordinary monsters beside
  // it and a player walking into one deserves the warning.

  import { monsterIcon, initialsFor } from './sprites';
  import { FIRST_CANONICAL_MONSTER_ID, MONSTERS_PER_REGION } from '../net/content';

  interface Props {
    monsterId: number;
    name: string;
    size?: 'sm' | 'md' | 'lg';
    /** Dimmed, for a codex entry never encountered. */
    unknown?: boolean;
  }

  const { monsterId, name, size = 'md', unknown = false }: Props = $props();

  const url = $derived(monsterIcon(monsterId));

  // The last monster of each five-strong region group.
  const isBoss = $derived(
    monsterId >= FIRST_CANONICAL_MONSTER_ID &&
      (monsterId - FIRST_CANONICAL_MONSTER_ID + 1) % MONSTERS_PER_REGION === 0,
  );
</script>

<span
  class="portrait"
  data-size={size}
  class:boss={isBoss}
  class:unknown
  title={isBoss ? `${name} (boss)` : name}
>
  {#if url}
    <img src={url} alt="" loading="lazy" decoding="async" />
  {:else}
    <span class="fallback" aria-hidden="true">{initialsFor(name)}</span>
  {/if}
</span>

<style>
  .portrait {
    display: inline-grid;
    place-items: center;
    flex: none;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg);
    overflow: hidden;
    line-height: 0;
  }

  .portrait[data-size='sm'] {
    width: 2rem;
    height: 2rem;
  }
  .portrait[data-size='md'] {
    width: 3.2rem;
    height: 3.2rem;
  }
  .portrait[data-size='lg'] {
    width: 8rem;
    height: 8rem;
  }

  /* Colour AND a heavier border, plus "(boss)" in the tooltip - three signals
     for one fact. */
  .portrait.boss {
    border-color: var(--rarity-10);
    border-width: 2px;
  }

  /* An unencountered codex entry is a silhouette rather than a blank: it says
     "there is something here you have not met" instead of "nothing here". */
  .unknown img {
    filter: brightness(0) saturate(0);
    opacity: 0.35;
  }

  img {
    width: 100%;
    height: 100%;
    object-fit: contain;
  }

  .fallback {
    font-weight: 700;
    font-size: 0.8rem;
    line-height: 1;
    color: var(--text-dim);
  }
</style>
