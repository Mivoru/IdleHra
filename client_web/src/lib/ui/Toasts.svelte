<script lang="ts">
  import { commandResults, dismissCommandResult } from '../stores/game';
  import { COMMAND_RESULT_SUCCESS } from '../stores/commandResults';
</script>

<div class="toasts" role="status" aria-live="polite">
  {#each $commandResults as toast (toast.id)}
    <div class="toast" class:ok={toast.code === COMMAND_RESULT_SUCCESS}>
      <span>{toast.message}</span>
      <button aria-label="Dismiss" onclick={() => dismissCommandResult(toast.id)}>×</button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    right: 1rem;
    bottom: 1rem;
    display: grid;
    gap: 0.4rem;
    z-index: 60;
    max-width: min(24rem, 90vw);
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

  .toast button {
    background: none;
    border: none;
    padding: 0 0.2rem;
    margin-left: auto;
    color: var(--text-dim);
    font-size: 1.1rem;
    line-height: 1;
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
