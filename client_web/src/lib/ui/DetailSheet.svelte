<script lang="ts">
  // Modul: ONE SHEET FOR "THE DETAIL OF THIS ROW" (tasks 103/104).
  //
  // The Hall of Ancestors and the Great Works both had every control on every
  // row - "Field into [1][2][3] [Keep]" two hundred times, two Deposit buttons
  // per monument - and the page was the controls. A row now says what it IS
  // and a tap opens this, where the controls live once.
  //
  // Portalled to <body> for the same two reasons PersonPicker's sheet is (see
  // ui/portal.ts: `.panel` keeps a stacking context, and a blurred ancestor
  // would become the fixed element's containing block). Registered with the
  // overlay stack so the hardware back button and Escape close it rather than
  // walking off the screen underneath, at LAYER_Z.pickerSheet because it paints
  // at that z-index.
  import type { Snippet } from 'svelte';
  import { registerOverlay } from '../stores/sheet';
  import { LAYER_Z } from '../net/backButton';
  import { portal } from './portal';

  interface Props {
    title: string;
    onClose: () => void;
    children: Snippet;
    testid?: string;
  }

  const { title, onClose, children, testid }: Props = $props();

  $effect(() => registerOverlay(() => onClose(), LAYER_Z.pickerSheet));

  function onWindowKey(event: KeyboardEvent) {
    if (event.key === 'Escape') onClose();
  }
</script>

<svelte:window onkeydown={onWindowKey} />

<div class="layer" use:portal>
  <button type="button" class="backdrop touch-exempt" aria-label="Close" onclick={onClose}></button>
  <div class="sheet" role="dialog" aria-modal="true" aria-label={title} data-testid={testid}>
    <div class="top">
      <h3>{title}</h3>
      <button type="button" class="close" onclick={onClose}>Close</button>
    </div>
    <div class="body">
      {@render children()}
    </div>
  </div>
</div>

<style>
  /* z-index 1400/1401 is LAYER_Z.pickerSheet in net/backButton.ts - above the
     chat dock and coach (40) and the app header (1100). Fixed, so it carries
     its own safe-area inset: body's padding never reaches a fixed overlay. */
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 1400;
    border: 0;
    padding: 0;
    min-height: 0;
    background: rgba(0, 0, 0, 0.55);
  }

  .sheet {
    position: fixed;
    left: 50%;
    bottom: 0;
    translate: -50% 0;
    width: min(32rem, 100%);
    z-index: 1401;
    max-height: 82vh;
    overflow-y: auto;
    overscroll-behavior: contain;
    box-sizing: border-box;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-bottom: 0;
    border-radius: 12px 12px 0 0;
    box-shadow: 0 -6px 24px rgba(0, 0, 0, 0.35);
    padding: 0.75rem max(0.9rem, var(--safe-area-inset-left, env(safe-area-inset-left, 0px)))
      calc(1rem + var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)));
  }

  .top {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
    margin-bottom: 0.5rem;
  }

  h3 {
    margin: 0;
    font-size: 1rem;
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .close {
    flex-shrink: 0;
    font-size: 0.8rem;
    padding: 0.25rem 0.6rem;
  }

  .body {
    display: grid;
    gap: 0.6rem;
  }
</style>
