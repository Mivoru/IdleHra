<script lang="ts">
  // Modul: ONE TRAIT, as a coloured badge. Tap (or hover) for its effect -
  // `title` alone does nothing on a phone. `interactive={false}` renders a plain
  // span for places already inside a button (PersonPicker's option cards),
  // because a button inside a button is invalid and swallows the tap.
  import type { TraitDefinition } from '../net/rest';
  import { rarityClass } from './traits';

  interface Props {
    trait: TraitDefinition;
    interactive?: boolean;
  }

  const { trait, interactive = true }: Props = $props();
  let open = $state(false);
</script>

{#if interactive}
  <span class="wrap">
    <button
      type="button"
      class="trait {rarityClass(trait.Rarity)}"
      title={trait.Description}
      aria-expanded={open}
      onclick={(event) => {
        event.stopPropagation();
        open = !open;
      }}>{trait.Name}</button
    >
    {#if open}<span class="desc">{trait.Description}</span>{/if}
  </span>
{:else}
  <span class="trait {rarityClass(trait.Rarity)}" title={trait.Description}>{trait.Name}</span>
{/if}

<style>
  .wrap {
    display: inline-flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.15rem;
    min-width: 0;
  }

  .trait {
    display: inline-flex;
    align-items: center;
    flex-shrink: 0;
    font: inherit;
    font-size: 0.68rem;
    font-weight: 600;
    line-height: 1.2;
    padding: 0.1rem 0.4rem;
    border: 1px solid var(--border);
    border-radius: 999px;
    background: var(--bg);
    color: var(--text);
    cursor: default;
    white-space: nowrap;
  }

  button.trait {
    cursor: pointer;
  }

  .trait-common {
    border-color: var(--border);
    color: var(--text-dim);
  }
  .trait-rare {
    border-color: #4a7fc1;
    color: #6f9fd8;
  }
  .trait-legendary {
    border-color: var(--brass, #c9a227);
    color: var(--brass-lit, #e0b93a);
  }
  .trait-flaw {
    border-color: var(--danger);
    color: var(--danger);
  }

  .desc {
    font-size: 0.7rem;
    color: var(--text-dim);
  }
</style>
