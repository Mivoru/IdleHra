<script lang="ts" module>
  export interface MenuItem {
    label: string;
    onSelect: () => void;
    danger?: boolean;
    disabled?: boolean;
    /** Why it is disabled, or what it does - shown as the tooltip. */
    title?: string;
    /** Draws a divider above this item. */
    separated?: boolean;
    /** Keeps the menu open after the click (a two-step confirm). */
    keepOpen?: boolean;
    /** Why it is disabled, printed under the label while it is - a touch
     *  screen shows no tooltip, least of all on a disabled item. */
    note?: string;
  }
</script>

<script lang="ts">
  // Modul: ONE MENU, TWO CALLERS. This was Chat's name menu with Whisper, Add
  // Friend, Profile and Block written into it. Task 81 needed the same thing
  // for a Chest row (five buttons became one action plus this), so it takes
  // its items instead. A second, Chest-only copy would have been two popups
  // that drift apart on positioning, dismissal and touch size.
  import { onMount } from 'svelte';

  interface Props {
    x: number;
    y: number;
    title: string;
    items: MenuItem[];
    onClose: () => void;
  }

  let { x, y, title, items, onClose }: Props = $props();

  let node = $state<HTMLDivElement | null>(null);
  let left = $state(0);
  let top = $state(0);

  // Modul: CLAMPED TO THE VIEWPORT after it has a size. It opens at the
  // pointer, and a Chest row's menu button sits at the right edge of a 390px
  // phone, so an unclamped menu opened half off the screen.
  onMount(() => {
    left = x;
    top = y;
    if (!node) return;
    const margin = 8;
    const rect = node.getBoundingClientRect();
    left = Math.max(margin, Math.min(x, window.innerWidth - rect.width - margin));
    top = y + rect.height + margin > window.innerHeight ? Math.max(margin, y - rect.height) : y;
    node.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
  });

  function clickOutside(el: HTMLElement) {
    const handleClick = (event: MouseEvent) => {
      if (!el.contains(event.target as Node) && !event.defaultPrevented) onClose();
    };
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    // Capture phase, so it fires before anything else stops propagation.
    document.addEventListener('click', handleClick, true);
    document.addEventListener('keydown', handleKey);
    return {
      destroy() {
        document.removeEventListener('click', handleClick, true);
        document.removeEventListener('keydown', handleKey);
      },
    };
  }

  function select(item: MenuItem) {
    item.onSelect();
    if (!item.keepOpen) onClose();
  }
</script>

<div
  class="context-menu"
  role="menu"
  aria-label={title}
  style="top: {top}px; left: {left}px;"
  bind:this={node}
  use:clickOutside
>
  <div class="header">
    <strong>{title}</strong>
  </div>
  {#each items as item (item.label)}
    {#if item.separated}<div class="divider"></div>{/if}
    <button
      role="menuitem"
      class:danger={item.danger}
      disabled={item.disabled}
      title={item.title ?? ''}
      onclick={() => select(item)}
    >
      {item.label}
      {#if item.disabled && item.note}<span class="note">{item.note}</span>{/if}
    </button>
  {/each}
</div>

<style>
  .context-menu {
    position: fixed;
    z-index: 10000;
    /* Modul: the app's tokens, not fallbacks. It read var(--bg-dark,
       #1a1a1a), a token the theme never defines, so the menu was always
       near-black - and its items inherit the text colour, which the light
       theme makes dark. Dark on dark: invisible. */
    background: var(--bg-panel);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: 4px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
    min-width: 150px;
    max-width: calc(100vw - 16px);
    display: flex;
    flex-direction: column;
    padding: 0.25rem 0;
  }

  .header {
    padding: 0.5rem 1rem;
    font-size: 0.85rem;
    color: var(--accent);
    border-bottom: 1px solid var(--border);
    margin-bottom: 0.25rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  button {
    background: transparent;
    border: none;
    color: inherit;
    text-align: left;
    padding: 0.5rem 1rem;
    cursor: pointer;
    font-size: 0.9rem;
    border-radius: 0;
  }

  button:hover:not(:disabled) {
    background: var(--bg-raised);
  }

  button:disabled {
    opacity: 0.5;
    cursor: default;
  }

  button.danger {
    color: var(--danger);
  }

  button.danger:hover:not(:disabled) {
    background: rgba(255, 68, 68, 0.1);
  }

  .note {
    display: block;
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  .divider {
    height: 1px;
    background: var(--border);
    margin: 0.25rem 0;
  }

  /* The touch floor (client_web/CLAUDE.md). The items are full-width rows, so
     only their height needs raising. */
  @media (max-width: 40rem) {
    button {
      min-height: 44px;
    }
  }
</style>
