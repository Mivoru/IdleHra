<script lang="ts">
  import { commandResults, dismissCommandResult } from '../stores/game';
  import { COMMAND_RESULT_OK_CODES } from '../stores/commandResults';
</script>

<div class="toasts" role="status" aria-live="polite">
  {#each $commandResults as toast (toast.id)}
    <div class="toast" class:ok={COMMAND_RESULT_OK_CODES.has(toast.code)}>
      <span>{toast.message}</span>
      <button aria-label="Dismiss" onclick={() => dismissCommandResult(toast.id)}>×</button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    /* Fixed to the viewport, so body's safe-area padding does not reach it. */
    right: calc(1rem + var(--sa-right));
    bottom: calc(1rem + var(--sa-bottom) + var(--tabbar-h));
    display: grid;
    gap: 0.4rem;
    z-index: 60;
    max-width: min(24rem, 90vw);
    /* Modul: A TOAST MUST NOT EAT THE TAP IT IS REPORTING ON. Every manual
       reroll answers with a toast that lives 6 s, and on a phone the stack is
       90vw wide over the bottom of the screen - exactly where the Forge's
       Reroll button is - so the second reroll had to wait for the first
       one's toast to leave. The layer is click-through; only the dismiss
       button takes pointer events. */
    pointer-events: none;
  }

  /* On a phone the actions live at the bottom of a screen, so the toasts go
     to the top, under the status bar (fixed, so body's safe-area padding does
     not reach it - the inset is its own). */
  @media (max-width: 40rem) {
    .toasts {
      top: calc(0.5rem + var(--sa-top));
      bottom: auto;
      left: calc(0.5rem + var(--sa-left));
      right: calc(0.5rem + var(--sa-right));
      max-width: none;
    }

    /* Rerolling five times in six seconds stacked five toasts down the
       screen, over the very button being pressed. The newest two are the
       news; the older ones are still in the store and expire as before. */
    .toast:nth-last-child(n + 3) {
      display: none;
    }
  }

  .toast {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.55rem 0.7rem;
    background: var(--bg-raised);
    border: 1px solid var(--danger);
    border-left-width: 3px;
    border-radius: var(--radius);
    font-size: 0.85rem;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
    animation: slide-in 160ms ease-out;
  }

  .toast.ok {
    border-color: var(--good);
  }

  /* Modul: a toast can carry player text - "Could not join "<guild name>"",
     and a guild name is up to 100 characters with no charset rule. The wrap
     rule in app.css is scoped to .panel, which a fixed toast is not inside,
     and a flex child will not shrink below its longest word without
     min-width: 0. One unbroken name ran off a phone. */
  .toast span {
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .toast button {
    background: none;
    border: none;
    padding: 0 0.2rem;
    margin-left: auto;
    color: var(--text-dim);
    font-size: 1.1rem;
    line-height: 1;
    pointer-events: auto;
    flex-shrink: 0;
  }

  @keyframes slide-in {
    from {
      transform: translateY(0.5rem);
      opacity: 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .toast {
      animation: none;
    }
  }
</style>
