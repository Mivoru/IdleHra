<script lang="ts">
  // Modul: THE CHRONICLE WINDOW (owner, 2026-11-15): the pass and the daily
  // missions that fill it, as two tabs of one window, opened by the side
  // bubble. The pass is the reward, the missions are how to get there - one
  // place, so "what do I do for the next level" is always one tab away.
  import { chronicleView } from './missions';
  import ChroniclePassPanel from './ChroniclePassPanel.svelte';
  import MissionsPanel from './MissionsPanel.svelte';
  import Modal from './Modal.svelte';

  function close() {
    chronicleView.set(null);
  }
</script>

{#if $chronicleView}
  <Modal label="Chronicle" tone="brass" width="34rem" layout="block" onClose={close} testid="chronicle">
    {#snippet header()}
      <div class="head">
        <div class="tabs" role="tablist">
          <button role="tab" aria-selected={$chronicleView === 'pass'} class:on={$chronicleView === 'pass'}
            onclick={() => chronicleView.set('pass')} data-testid="chronicle-tab-pass">Chronicle</button>
          <button role="tab" aria-selected={$chronicleView === 'missions'} class:on={$chronicleView === 'missions'}
            onclick={() => chronicleView.set('missions')} data-testid="chronicle-tab-missions">Daily missions</button>
        </div>
        <button class="close" aria-label="Close" onclick={close} data-testid="chronicle-close">&times;</button>
      </div>
    {/snippet}

    {#if $chronicleView === 'pass'}
      <ChroniclePassPanel />
    {:else}
      <MissionsPanel onLeave={close} />
    {/if}
  </Modal>
{/if}

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .tabs {
    flex: 1;
    display: flex;
    gap: 0.35rem;
    min-width: 0;
  }

  .tabs button {
    min-height: 44px;
    font-weight: 600;
    opacity: 0.7;
  }

  .tabs button.on {
    opacity: 1;
    border-color: var(--accent);
    box-shadow: 0 -2px 0 var(--accent) inset;
  }

  .close {
    min-width: 44px;
    min-height: 44px;
    flex-shrink: 0;
  }
</style>
