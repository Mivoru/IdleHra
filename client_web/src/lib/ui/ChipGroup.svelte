<script lang="ts" generics="T">
  // Modul: TASK 106 - A FILTER IS NOT A TAB. A row of chips narrows what a
  // list shows; it does not swap one panel for another, so it is a labelled
  // group of toggle buttons (aria-pressed), each in the Tab order, rather than
  // a tablist. There were two chip groups and three `.filters` copies, each
  // with its own "on" look; this is the one look: the selected-tint fill with
  // an accent edge - the same tint a selected tab uses, so "chosen" reads the
  // same everywhere.
  //
  // `color` on an option tints that chip when it is chosen (a rarity filter).
  interface ChipOption {
    value: T;
    label: string;
    color?: string;
    testid?: string;
  }

  interface Props {
    options: readonly ChipOption[];
    value: T;
    /** The accessible name of the group. */
    label: string;
    size?: 'sm' | 'md';
    onchange?: (value: T) => void;
  }

  let { options, value = $bindable(), label, size = 'md', onchange }: Props = $props();

  function choose(next: T): void {
    if (next === value) return;
    value = next;
    onchange?.(next);
  }
</script>

<div class="chips {size}" role="group" aria-label={label}>
  {#each options as option, i (i)}
    <button
      type="button"
      class:active={option.value === value}
      aria-pressed={option.value === value}
      style:--chip-color={option.color}
      data-testid={option.testid}
      onclick={() => choose(option.value)}
    >
      {option.label}
    </button>
  {/each}
</div>

<style>
  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: var(--sp-2);
  }

  .chips button {
    flex-shrink: 0;
    border-radius: var(--radius-pill);
    padding: 0.3rem 0.75rem;
  }

  .chips.sm button {
    font-size: var(--fs-sm);
    padding: 0.2rem 0.6rem;
  }

  .chips button.active {
    border-color: var(--chip-color, var(--accent));
    background-color: var(--tint-selected);
    color: var(--chip-color, var(--accent));
    font-weight: var(--fw-bold);
  }
</style>
