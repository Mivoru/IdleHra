<script lang="ts">
  import { FRAME_DRAWINGS } from './frames';

  interface Props {
    frameId: string | null;
    /**
     * Pulse the glow. Only for a face shown on its own (the profile); a face
     * in a list - chat, the boards, a guild roster - glows still. Task 108.
     */
    animate?: boolean;
  }

  let { frameId, animate = false }: Props = $props();

  const drawing = $derived(frameId ? (FRAME_DRAWINGS[frameId] ?? null) : null);

  // Everything is drawn in a 100x100 box laid over a circular portrait of
  // radius 40 centred at 50,50, so the frame scales with the avatar size.
  const studs = Array.from({ length: 8 }, (_, i) => {
    const a = (i / 8) * Math.PI * 2 - Math.PI / 2;
    return { x: 50 + Math.cos(a) * 44, y: 50 + Math.sin(a) * 44 };
  });
  const points = Array.from({ length: 8 }, (_, i) => {
    const a = (i / 8) * Math.PI * 2 - Math.PI / 2;
    const half = Math.PI / 16;
    const p = (r: number, t: number) => `${(50 + Math.cos(t) * r).toFixed(2)},${(50 + Math.sin(t) * r).toFixed(2)}`;
    return `${p(45, a - half)} ${p(50, a)} ${p(45, a + half)}`;
  });
</script>

{#if drawing}
  {#if drawing.glow && animate}
    <span class="halo" aria-hidden="true" style="--frame-color: {drawing.color}"></span>
  {/if}
  <svg class="frame" class:glow={drawing.glow} viewBox="0 0 100 100" aria-hidden="true" style="--frame-color: {drawing.color}">
    {#if drawing.style === 'band'}
      <circle cx="50" cy="50" r="43" fill="none" stroke={drawing.accent} stroke-width="8" />
      <circle cx="50" cy="50" r="43" fill="none" stroke={drawing.color} stroke-width="5.5" />
    {:else if drawing.style === 'rope'}
      <circle cx="50" cy="50" r="43" fill="none" stroke={drawing.accent} stroke-width="7.5" />
      <circle cx="50" cy="50" r="43" fill="none" stroke={drawing.color} stroke-width="5" stroke-dasharray="5 2.5" />
    {:else if drawing.style === 'studs'}
      <circle cx="50" cy="50" r="44" fill="none" stroke={drawing.color} stroke-width="6.5" />
      {#each studs as s}
        <circle cx={s.x} cy={s.y} r="3.4" fill={drawing.accent} stroke={drawing.color} stroke-width="1" />
      {/each}
    {:else if drawing.style === 'knot'}
      <circle cx="50" cy="50" r="42" fill="none" stroke={drawing.color} stroke-width="4" />
      <circle cx="50" cy="50" r="47" fill="none" stroke={drawing.accent} stroke-width="3" stroke-dasharray="7 3 2 3" />
      <circle cx="50" cy="50" r="47" fill="none" stroke={drawing.color} stroke-width="1.2" />
    {:else}
      <circle cx="50" cy="50" r="42" fill="none" stroke={drawing.color} stroke-width="4.5" />
      <circle cx="50" cy="50" r="45.5" fill="none" stroke={drawing.accent} stroke-width="1.2" />
      {#each points as p}
        <polygon points={p} fill={drawing.color} stroke={drawing.accent} stroke-width="0.8" />
      {/each}
    {/if}
  </svg>
{/if}

<style>
  .frame {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    pointer-events: none;
    overflow: visible;
  }

  /* Modul: TASK 108 - THE GLOW IS STILL; ONLY A FADE MOVES.
     This animated `filter: drop-shadow` 3 s and infinite, and a frame rides on
     every PlayerAvatar - every chat message, every leaderboard row, GuildOps,
     WorldBoss. A filter is redrawn, not composited, so each of those faces
     re-rasterised its SVG every frame. The shadow is drawn once now (the
     pulse's resting depth, same colour), and where a face is shown alone the
     pulse is a ring on its own layer whose only moving property is opacity. */
  .glow {
    filter: drop-shadow(0 0 3px var(--frame-color));
  }

  .halo {
    position: absolute;
    inset: 3%;
    border-radius: 50%;
    box-shadow: 0 0 7px 2px var(--frame-color);
    opacity: 0;
    animation: frame-halo 3.2s ease-in-out infinite;
    pointer-events: none;
  }

  @keyframes frame-halo {
    50% {
      opacity: 0.7;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .halo {
      animation: none;
      opacity: 0;
    }
  }
</style>
