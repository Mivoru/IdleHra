<script lang="ts">
  // Task 84: a Great Work drawn from how many stages are built. One little
  // monument, five layers - foundation, pillars, walls, roof, beacon - so the
  // SAME picture is the panel's progress and the Home map's landmark.
  //
  // Modul: NO ART FILE, ON PURPOSE. The server serves sprites for items and
  // places, and there is no painting of a monument stage; a tasteful SVG that
  // takes its colour from the theme tokens is honest about that and cannot
  // 404. `ghost` draws the unbuilt layers as faint dashed outlines (the panel:
  // "this is what is still to come"); the map leaves them out, so a stage
  // appears on the valley only when it is built.
  interface Props {
    stage: number;
    ghost?: boolean;
    size?: number | string;
    label?: string;
  }

  let { stage, ghost = false, size = 40, label = '' }: Props = $props();

  const shown = (layer: number) => stage >= layer;
</script>

<svg
  class="monument"
  width={size}
  height={size}
  viewBox="0 0 40 40"
  role="img"
  aria-label={label || `Great Work, stage ${stage} of 5`}
  data-stage={stage}
>
  <!-- 1 foundation -->
  {#if shown(1) || ghost}
    <rect class="part" class:ghost={!shown(1)} x="4" y="31" width="32" height="5" rx="1" />
  {/if}
  <!-- 2 pillars -->
  {#if shown(2) || ghost}
    <rect class="part" class:ghost={!shown(2)} x="8" y="20" width="4" height="11" />
    <rect class="part" class:ghost={!shown(2)} x="28" y="20" width="4" height="11" />
  {/if}
  <!-- 3 walls -->
  {#if shown(3) || ghost}
    <rect class="part wall" class:ghost={!shown(3)} x="12" y="22" width="16" height="9" />
  {/if}
  <!-- 4 roof -->
  {#if shown(4) || ghost}
    <path class="part roof" class:ghost={!shown(4)} d="M5 20 L20 9 L35 20 Z" />
  {/if}
  <!-- 5 beacon -->
  {#if shown(5) || ghost}
    <circle class="part beacon" class:ghost={!shown(5)} cx="20" cy="5.5" r="3.2" />
    {#if shown(5)}
      <circle class="glow" cx="20" cy="5.5" r="5.5" />
    {/if}
  {/if}
</svg>

<style>
  .monument {
    display: block;
    flex-shrink: 0;
    overflow: visible;
  }

  .part {
    fill: color-mix(in srgb, var(--accent, #c9a227) 55%, var(--bg-panel, #1c1712));
    stroke: var(--accent, #c9a227);
    stroke-width: 1;
    stroke-linejoin: round;
  }

  .wall {
    fill: color-mix(in srgb, var(--accent, #c9a227) 30%, var(--bg-panel, #1c1712));
  }

  .roof {
    fill: color-mix(in srgb, var(--danger, #d1503c) 45%, var(--bg-panel, #1c1712));
    stroke: var(--danger, #d1503c);
  }

  .beacon {
    fill: var(--accent, #c9a227);
  }

  .glow {
    fill: none;
    stroke: var(--accent, #c9a227);
    stroke-width: 0.8;
    opacity: 0.55;
  }

  .part.ghost {
    fill: none;
    stroke-dasharray: 2 2;
    opacity: 0.35;
  }
</style>
