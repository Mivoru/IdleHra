<script lang="ts">
  // Modul: TASK 106 - ONE MODAL, NOT NINE.
  //
  // Death, victory, What's new, Welcome back, the player profile and the exit
  // prompt each drew their own backdrop: four scrim opacities, three
  // safe-area paddings, two dvh caps, z-indexes from 60 to 1100, and focus
  // management in none of them. This is the shared shell:
  //
  // - portalled to <body> (see portal.ts - .panel's animation and the chat
  //   dock's blur both trap a fixed overlay otherwise);
  // - role=dialog, aria-modal, labelled; the app root goes `inert` behind it
  //   and Tab stays inside it, focus returning on close (modalStack.ts);
  // - Escape and the hardware back button go through App's resolver, never a
  //   listener here. Pass `onClose` and the closer is registered on the stack
  //   in stores/sheet.ts at `z`. The App-level cards whose open state lives in
  //   a store (death, victory, offline summary, the exit prompt) pass
  //   `register={false}`: App already reads those stores, and registering them
  //   too would be a second copy of the same truth;
  // - --scrim, the z-index, safe-area padding and a dvh cap;
  // - `variant="sheet"` docks it to the bottom edge on a phone.
  //
  // The card is this component's element, so its look is set by props (`width`,
  // `tone`, `flush`, `layout`, and `class` for global classes such as
  // folk-sweep). Everything INSIDE it is the caller's own markup and takes the
  // caller's scoped styles as usual.
  import type { Snippet } from 'svelte';
  import { portal } from './portal';
  import { openModal } from './modalStack';
  import { registerOverlay } from '../stores/sheet';
  import { LAYER_Z } from '../net/backButton';

  interface Props {
    /** The accessible name, when no heading inside carries it. */
    label?: string;
    /** The id of the heading that names the dialog. */
    labelledby?: string;
    /** Closes it: from the scrim, and from Escape/back when `register` is on. */
    onClose?: () => void;
    /** Register `onClose` on the overlay stack. Off for store-backed App cards. */
    register?: boolean;
    /** A tap on the dim area around the card closes it (needs `onClose`). */
    dismissOnScrim?: boolean;
    /** The painted z-index - a LAYER_Z value, so back and paint agree. */
    z?: number;
    /** The card's maximum width. */
    width?: string;
    tone?: 'default' | 'brass' | 'danger';
    /** `sheet` docks to the bottom edge on a phone. */
    variant?: 'center' | 'sheet';
    /** How the body lays out its children: a gapped grid, or normal flow. */
    layout?: 'grid' | 'block';
    /** No padding on the card - for a header/body that pad themselves. */
    flush?: boolean;
    class?: string;
    testid?: string;
    /** Pinned above the body; the body scrolls under it. */
    header?: Snippet;
    /** Pinned below the body, so the way out never scrolls away. */
    footer?: Snippet;
    children: Snippet;
  }

  let {
    label,
    labelledby,
    onClose,
    register = true,
    dismissOnScrim = true,
    z = LAYER_Z.modal,
    width = '26rem',
    tone = 'default',
    variant = 'center',
    layout = 'grid',
    flush = false,
    class: cardClass = '',
    testid,
    header,
    footer,
    children,
  }: Props = $props();

  let scrim = $state<HTMLElement | null>(null);
  let card = $state<HTMLElement | null>(null);

  $effect(() => {
    if (!card || !scrim) return;
    return openModal(card, scrim);
  });

  $effect(() => {
    if (!register || !onClose) return;
    return registerOverlay(() => onClose?.(), z);
  });

  // Only a press that starts AND ends on the scrim: a drag that selects text
  // in the card and is released outside it is reading, not dismissing.
  let downOnScrim = false;
  function scrimDown(event: PointerEvent): void {
    downOnScrim = event.target === event.currentTarget;
  }
  function scrimClick(event: MouseEvent): void {
    const hit = downOnScrim && event.target === event.currentTarget;
    downOnScrim = false;
    if (hit && dismissOnScrim) onClose?.();
  }
</script>

<!-- The scrim is a pointer target only; the keyboard way out is Escape (App)
     and the card's own buttons. -->
<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div
  class="modal-scrim"
  class:sheet={variant === 'sheet'}
  style:z-index={z}
  bind:this={scrim}
  use:portal
  onpointerdown={scrimDown}
  onclick={scrimClick}
>
  <div
    class="modal-card tone-{tone} {cardClass}"
    class:flush
    class:pinned={!!header || !!footer}
    style:--modal-width={width}
    role="dialog"
    aria-modal="true"
    aria-label={labelledby ? undefined : label}
    aria-labelledby={labelledby}
    tabindex="-1"
    data-testid={testid}
    bind:this={card}
  >
    {#if header}{@render header()}{/if}
    <div class="modal-body" class:grid={layout === 'grid'}>
      {@render children()}
    </div>
    {#if footer}<div class="modal-footer">{@render footer()}</div>{/if}
  </div>
</div>

<style>
  .modal-scrim {
    --modal-pad: 1rem;
    position: fixed;
    inset: 0;
    background: var(--scrim);
    display: grid;
    place-items: center;
    /* Fixed, so body's safe-area padding never reaches it - its own inset. */
    padding: calc(var(--modal-pad) + var(--sa-top)) calc(var(--modal-pad) + var(--sa-right))
      calc(var(--modal-pad) + var(--sa-bottom)) calc(var(--modal-pad) + var(--sa-left));
    overscroll-behavior: contain;
  }

  /* The card scrolls rather than running off a short screen (a landscape
     phone, a large font): a fixed scrim cannot scroll for it. dvh after vh -
     on mobile web vh is the LARGE viewport, so a vh cap could end below the
     URL bar's fold with the buttons in it. */
  .modal-card {
    position: relative;
    width: min(var(--modal-width), 100%);
    max-height: calc(100vh - 2 * var(--modal-pad));
    max-height: calc(100dvh - 2 * var(--modal-pad) - var(--sa-top) - var(--sa-bottom));
    overflow-y: auto;
    overscroll-behavior: contain;
    background: var(--bg-panel);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: var(--shadow-xl);
    padding: 1.2rem;
    text-align: start;
  }

  .modal-card:focus {
    outline: none;
  }

  .modal-card.flush {
    padding: 0;
  }

  .tone-brass {
    border-color: var(--brass);
  }

  .tone-danger {
    border-color: var(--danger);
  }

  /* A header or footer stays put and only the body moves. */
  .modal-card.pinned {
    display: flex;
    flex-direction: column;
    gap: var(--sp-4);
    overflow: hidden;
  }

  .modal-card.pinned.flush {
    gap: 0;
  }

  .modal-card.pinned > .modal-body {
    flex: 1 1 auto;
    min-height: 0;
    overflow-y: auto;
    overscroll-behavior: contain;
    /* Anything prepended to a scroller wants this - see the loot list. */
    overflow-anchor: none;
  }

  .modal-body.grid {
    display: grid;
    gap: var(--sp-4);
    align-content: start;
  }

  .modal-footer {
    flex-shrink: 0;
  }

  @media (max-width: 40rem) {
    .modal-scrim {
      --modal-pad: 0.5rem;
    }

    /* A sheet: docked to the bottom edge, full width, the gesture bar's
       inset inside the card rather than under it. */
    .modal-scrim.sheet {
      place-items: end stretch;
      padding: var(--sa-top) 0 0;
    }

    .modal-scrim.sheet > .modal-card {
      width: 100%;
      max-height: 90vh;
      max-height: min(90dvh, calc(100dvh - var(--sa-top)));
      border-radius: var(--radius-lg) var(--radius-lg) 0 0;
      border-bottom: 0;
      padding-bottom: calc(1.2rem + var(--sa-bottom));
      padding-left: calc(1.2rem + var(--sa-left));
      padding-right: calc(1.2rem + var(--sa-right));
    }

    .modal-scrim.sheet > .modal-card.flush {
      padding: 0 var(--sa-right) var(--sa-bottom) var(--sa-left);
    }
  }
</style>
