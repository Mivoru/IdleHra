<script lang="ts" generics="T extends string">
  // Modul: TASK 106 - ONE TAB ROW. There were four role=tablist rows with four
  // class names and four "selected" looks, three more tab sets with no ARIA at
  // all, and a nested row (Community > Leaderboards' Standing/Deepest) that
  // looked exactly like the row it sat under, so a player could not tell which
  // level they were switching.
  //
  // - ARIA tabs: role=tablist/tab, aria-selected, and a ROVING tabindex - only
  //   the selected tab is in the Tab order; the arrow keys, Home and End move
  //   between tabs and select as they go (automatic activation: every panel
  //   here is already loaded or cheap to load).
  // - `variant="pill"` for a screen's own top row; `variant="underline"` for a
  //   row nested inside a screen or a panel, so the two levels read differently.
  //
  // The panels are the caller's: render the one for `value` below this.
  interface TabItem {
    value: T;
    label: string;
    testid?: string;
  }

  interface Props {
    tabs: readonly TabItem[];
    value: T;
    /** The accessible name of the row. */
    label: string;
    variant?: 'pill' | 'underline';
    /** A data-* attribute name to set to each tab's value, for scripts that select tabs by key (data-progress-tab). */
    dataAttr?: string;
    onchange?: (value: T) => void;
  }

  let { tabs, value = $bindable(), label, variant = 'pill', dataAttr, onchange }: Props = $props();

  let root = $state<HTMLElement | null>(null);

  function choose(next: T): void {
    if (next === value) return;
    value = next;
    onchange?.(next);
  }

  function onKey(event: KeyboardEvent): void {
    const index = tabs.findIndex((t) => t.value === value);
    let next = -1;
    if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
    else if (event.key === 'ArrowLeft') next = (index - 1 + tabs.length) % tabs.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = tabs.length - 1;
    if (next < 0) return;
    event.preventDefault();
    choose(tabs[next].value);
    root?.querySelectorAll<HTMLElement>('[role="tab"]')[next]?.focus();
  }
</script>

<div class="tabs {variant}" role="tablist" aria-label={label} tabindex="-1" bind:this={root} onkeydown={onKey}>
  {#each tabs as tab (tab.value)}
    <button
      type="button"
      role="tab"
      class:active={tab.value === value}
      aria-selected={tab.value === value}
      tabindex={tab.value === value ? 0 : -1}
      data-testid={tab.testid}
      {...dataAttr ? { [dataAttr]: tab.value } : {}}
      onclick={() => choose(tab.value)}
    >
      {tab.label}
    </button>
  {/each}
</div>

<style>
  .tabs {
    display: flex;
    gap: var(--sp-4);
    flex-wrap: wrap;
  }

  .tabs:focus {
    outline: none;
  }

  .tabs button {
    min-height: 44px;
    flex-shrink: 0;
    padding: 0.4rem 0.9rem;
    font: inherit;
    color: inherit;
    cursor: pointer;
  }

  .pill button {
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--bg-panel);
  }

  .pill button.active {
    border-color: var(--accent);
    background-color: var(--tint-selected);
    color: var(--accent);
    font-weight: var(--fw-bold);
  }

  /* A nested row: no boxes, a rule under the row and a brass underline under
     the chosen tab. Scrolls sideways rather than wrapping, because a wrapped
     underline row loses the line it is drawn on. */
  .underline {
    gap: var(--sp-1);
    flex-wrap: nowrap;
    overflow-x: auto;
    border-bottom: 1px solid var(--line);
    scrollbar-width: none;
  }

  .underline button {
    border: 0;
    border-bottom: 2px solid transparent;
    border-radius: 0;
    background: none;
    background-image: none;
    box-shadow: none;
    color: var(--text-dim);
  }

  .underline button.active {
    color: var(--text);
    border-bottom-color: var(--brass-lit);
    font-weight: var(--fw-bold);
  }

  @media (hover: hover) and (pointer: fine) {
    .underline button:hover:not(.active) {
      color: var(--text);
      border-bottom-color: var(--border);
    }
  }
</style>
