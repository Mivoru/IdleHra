<script lang="ts">
  // Modul: THE FORGE STRIKES WHERE YOU PRESSED (owner, 2026-11-20: the band of
  // light across the whole screen "looks bad and stutters").
  //
  // The old flourish was a sweep over the entire Forge panel. On a phone that
  // panel is taller than the screen, so the burst at its centre was often
  // off-screen while the band repainted every visible pixel - a lot of cost
  // for a sign the player could not place. This is a small hot strike at the
  // button itself: a glow that blooms and fades, a ring, and sparks. All of it
  // is transform and opacity, so the compositor runs it.
  //
  // FIXED, AND MOVED TO <body>. The point is a viewport coordinate from the
  // click; a position:fixed element inside any transformed ancestor would
  // measure from that ancestor instead, so the node is reparented to body the
  // same way the Modal and the PersonPicker portal theirs.
  import Burst from './Burst.svelte';

  interface Props {
    /** Viewport coordinates of the strike, usually a button's centre. */
    x: number;
    y: number;
    /** Heavier for a fusion, light for a refine. */
    weight?: 'light' | 'normal' | 'heavy';
    color?: string;
  }

  let { x, y, weight = 'normal', color = 'var(--brass-lit)' }: Props = $props();

  const sparks = $derived(weight === 'heavy' ? 18 : weight === 'light' ? 9 : 12);
  const reach = $derived(weight === 'heavy' ? 4.2 : weight === 'light' ? 2 : 3);

  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return { destroy: () => node.remove() };
  }
</script>

<span
  class="strike {weight}"
  use:portal
  aria-hidden="true"
  style="left: {x}px; top: {y}px; --tint: {color}"
>
  <span class="glow"></span>
  <span class="ring"></span>
  <Burst count={sparks} {reach} {color} />
</span>

<style>
  .strike {
    position: fixed;
    width: 0;
    height: 0;
    pointer-events: none;
    z-index: 60;
  }

  .glow,
  .ring {
    position: absolute;
    left: 0;
    top: 0;
    border-radius: 50%;
    translate: -50% -50%;
  }

  .glow {
    width: 5rem;
    height: 5rem;
    background: radial-gradient(circle, rgba(255, 214, 140, 0.85) 0%, rgba(255, 150, 60, 0.35) 38%, transparent 70%);
    opacity: 0;
    animation: strike-glow 520ms ease-out forwards;
  }

  .ring {
    width: 3rem;
    height: 3rem;
    border: 2px solid var(--tint);
    opacity: 0;
    animation: strike-ring 600ms cubic-bezier(0.2, 0.7, 0.3, 1) forwards;
  }

  .heavy .glow {
    width: 8rem;
    height: 8rem;
    animation-duration: 760ms;
  }
  .heavy .ring {
    width: 4.5rem;
    height: 4.5rem;
    animation-duration: 820ms;
  }
  .light .glow {
    width: 4rem;
    height: 4rem;
  }
  .light .ring {
    display: none;
  }

  @keyframes strike-glow {
    0% {
      opacity: 0;
      scale: 0.3;
    }
    18% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      scale: 1.25;
    }
  }

  @keyframes strike-ring {
    0% {
      opacity: 0.9;
      scale: 0.2;
    }
    100% {
      opacity: 0;
      scale: 1.6;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .ring {
      display: none;
    }
    .glow {
      animation-duration: 300ms;
    }
  }
</style>
