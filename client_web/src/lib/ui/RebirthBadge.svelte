<script lang="ts">
  // The rebirth badge beside a name (owner, 2026-10-11): a small shield with
  // the count. At the cap (RebirthRules.MaxRebirths, sent by the rebirth
  // preview as MaxRebirths - mirrored here only as the threshold for the gold
  // look) it turns gold and glows, and so does the name right before it:
  // app.css lights `*:has(+ .rebirth-badge.max)`, so no screen that shows a
  // name has to know about it.
  interface Props {
    count: number;
  }

  let { count }: Props = $props();

  /** RebirthRules.MaxRebirths - only decides the gold look; the server enforces the cap. */
  const MAX_REBIRTHS = 10;
  const max = $derived(count >= MAX_REBIRTHS);
</script>

{#if count > 0}
  <span
    class="rebirth-badge"
    class:max
    title={max ? `Reborn ${count} times - every rebirth taken` : `Reborn ${count} ${count === 1 ? 'time' : 'times'}`}
    aria-label={`Reborn ${count} ${count === 1 ? 'time' : 'times'}`}
    data-testid="rebirth-badge">{count}</span
  >
{/if}

<style>
  .rebirth-badge {
    display: inline-grid;
    place-items: center;
    flex-shrink: 0;
    min-width: 1.35em;
    height: 1.35em;
    margin-left: 0.35em;
    padding: 0 0.2em;
    /* A shield: flat top, pointed bottom. */
    clip-path: polygon(0 0, 100% 0, 100% 62%, 50% 100%, 0 62%);
    background: color-mix(in srgb, var(--accent) 30%, var(--bg));
    color: var(--text);
    font-size: 0.72em;
    font-weight: 800;
    line-height: 1;
    vertical-align: middle;
  }

  /* A still glow: an endless filter animation repaints every frame, which
     the phone GPU budget forbids (gpuBudget.test.ts). */
  .rebirth-badge.max {
    background: linear-gradient(160deg, #fff4b0, #ffd700 45%, #b8860b);
    color: #3a2a00;
    filter: drop-shadow(0 0 4px #ffd700cc);
  }
</style>
