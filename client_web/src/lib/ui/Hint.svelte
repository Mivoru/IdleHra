<script lang="ts">
  // Modul: TAP FOR THE EXPLANATION - TraitBadge's tap-to-reveal, generalised.
  // About fifteen pieces of information a player needs lived only in `title=`:
  // a boss's first-clear multipliers, the hunting estimate's breakdown, the
  // gathering floor. `title` never shows on touch (Android's WebView does not
  // even show it on a long press), so on the phone this game is mostly played
  // on, they did not exist.
  //
  // A tap toggles it, a mouse hover shows it, Escape or a tap anywhere else
  // closes it. The popover is PORTALLED to <body> and positioned against the
  // viewport, because the places that need one sit inside `overflow: hidden`
  // panels and scroll boxes that clipped anything drawn in place - the chat
  // dock's ContextMenu is that failure on record. It is always in the DOM (only
  // hidden), so `aria-describedby` points at real text even while closed.
  import { onDestroy, tick, type Snippet } from 'svelte';

  interface Props {
    /** The explanation. */
    text: string;
    /** The trigger's content. Without it the trigger is a small "?". */
    children?: Snippet;
    /** Accessible name for the bare "?" trigger. */
    label?: string;
    class?: string;
  }

  const { text, children, label = 'More information', class: extraClass = '' }: Props = $props();

  const id = `hint-${Math.random().toString(36).slice(2, 10)}`;
  let open = $state(false);
  let pinned = false;
  let trigger = $state<HTMLButtonElement | null>(null);
  let pop = $state<HTMLDivElement | null>(null);
  let left = $state(0);
  let top = $state(0);

  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return { destroy: () => node.remove() };
  }

  async function place() {
    await tick();
    if (!trigger || !pop) return;
    const margin = 8;
    const anchor = trigger.getBoundingClientRect();
    const box = pop.getBoundingClientRect();
    left = Math.max(margin, Math.min(anchor.left, window.innerWidth - box.width - margin));
    const below = anchor.bottom + 4;
    top = below + box.height + margin > window.innerHeight ? Math.max(margin, anchor.top - box.height - 4) : below;
  }

  function show(pin: boolean) {
    pinned = pin;
    open = true;
    void place();
  }

  function hide() {
    open = false;
    pinned = false;
  }

  // Outside tap, Escape, and anything that moves the trigger out from under a
  // popover positioned in viewport pixels.
  $effect(() => {
    if (!open) return;
    const onDown = (event: PointerEvent) => {
      const target = event.target as Node;
      if (trigger?.contains(target) || pop?.contains(target)) return;
      hide();
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') hide();
    };
    document.addEventListener('pointerdown', onDown, true);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', hide, true);
    window.addEventListener('resize', hide);
    return () => {
      document.removeEventListener('pointerdown', onDown, true);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', hide, true);
      window.removeEventListener('resize', hide);
    };
  });

  onDestroy(hide);
</script>

<button
  type="button"
  class="hint-trigger touch-exempt {extraClass}"
  class:bare={!children}
  bind:this={trigger}
  aria-expanded={open}
  aria-describedby={id}
  aria-label={children ? undefined : label}
  onclick={(event) => {
    event.stopPropagation();
    if (open && pinned) hide();
    else show(true);
  }}
  onpointerenter={(event) => {
    if (event.pointerType === 'mouse' && !open) show(false);
  }}
  onpointerleave={(event) => {
    if (event.pointerType === 'mouse' && open && !pinned) hide();
  }}
  onblur={() => {
    if (!pinned) hide();
  }}
>
  {#if children}{@render children()}{:else}?{/if}
</button>

<div class="hint-pop" role="tooltip" {id} hidden={!open} bind:this={pop} use:portal style="left: {left}px; top: {top}px;">
  {text}
</div>

<style>
  /* Modul: TOUCH-EXEMPT, with the hit area grown by a pseudo-element instead.
     A Hint sits inline in a sentence or on a chip, and the 44px floor in
     app.css would turn every one of them into a slab. The ::after extends the
     box the browser hit-tests (padding alone cannot - see client_web/CLAUDE.md
     on the checkbox) without moving anything around it. */
  .hint-trigger {
    position: relative;
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font: inherit;
    color: inherit;
    background: none;
    background-image: none;
    border: none;
    box-shadow: none;
    padding: 0;
    margin: 0;
    min-width: 0;
    cursor: help;
    text-align: inherit;
  }

  .hint-trigger::after {
    content: '';
    position: absolute;
    inset: -10px -6px;
  }

  .hint-trigger:not(.bare) {
    text-decoration: underline dotted;
    text-underline-offset: 3px;
  }

  .bare {
    justify-content: center;
    width: 1.1rem;
    height: 1.1rem;
    border: 1px solid var(--border);
    border-radius: 50%;
    font-size: 0.7rem;
    line-height: 1;
    color: var(--text-dim);
    vertical-align: middle;
  }

  .hint-pop {
    position: fixed;
    z-index: 10001;
    max-width: min(22rem, calc(100vw - 16px));
    padding: 0.45rem 0.6rem;
    background: var(--bg-panel);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: 4px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
    font-size: 0.8rem;
    line-height: 1.35;
    overflow-wrap: anywhere;
    white-space: normal;
    pointer-events: auto;
  }

  .hint-pop[hidden] {
    display: none;
  }
</style>
