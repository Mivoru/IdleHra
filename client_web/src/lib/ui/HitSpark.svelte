<script lang="ts">
  // Modul: THE HIT HAD NO MARK.
  //
  // A swing lands every 1.5 seconds for hours and the only thing that ever
  // moved was the health bar. This draws what the blow was: a slash arc for a
  // blade, a streak for an arrow, a burst for a wand - and a brighter, wider
  // version of whichever it is when the blow crit.
  //
  // CSS RATHER THAN A CANVAS OR A PARTICLE LIBRARY. Three shapes on a keyframe
  // each is what this needs, and the browser composites transforms and opacity
  // off the main thread - which matters here more than usual, because keying an
  // effect on the damage stream is exactly what starved the main thread the
  // last time this client tried to animate combat.
  import { hitSparks } from '../stores/game';

  const spark = $derived($hitSparks);

  // Keyed on the hit id, so each spark animates exactly once and Svelte
  // discards the node when the next one replaces it. Without the key an
  // identical class list would leave the old element in place, never
  // re-running the animation, and the effect would fire once and never again.
</script>

<div class="layer" aria-hidden="true">
  {#if spark}
    {#key spark.id}
      <span class="spark kind-{spark.weaponKind}" class:crit={spark.isCrit}></span>
    {/key}
  {/if}
</div>

<style>
  .layer {
    position: absolute;
    inset: 0;
    overflow: hidden;
    pointer-events: none;
  }

  .spark {
    position: absolute;
    left: 50%;
    top: 50%;
    translate: -50% -50%;
  }

  /* --- 0: a blade. An arc that sweeps through and thins out. ------------- */
  .kind-0 {
    width: 7rem;
    height: 7rem;
    border-radius: 50%;
    border: 3px solid rgba(255, 240, 210, 0.95);
    border-color: rgba(255, 240, 210, 0.95) transparent transparent transparent;
    rotate: -35deg;
    animation: slash 320ms ease-out forwards;
  }

  @keyframes slash {
    0% {
      opacity: 0;
      scale: 0.55;
      rotate: -70deg;
    }
    25% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      scale: 1.25;
      rotate: 25deg;
    }
  }

  /* --- 1: an arrow. A short streak that drives in and stops. ------------- */
  .kind-1 {
    width: 5.5rem;
    height: 3px;
    background: linear-gradient(90deg, transparent, rgba(255, 246, 214, 0.95));
    border-radius: 2px;
    animation: arrow 300ms cubic-bezier(0.2, 0.7, 0.3, 1) forwards;
  }

  @keyframes arrow {
    0% {
      opacity: 0;
      translate: -180% -50%;
      scale: 0.7 1;
    }
    35% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      translate: -20% -50%;
      scale: 1.15 1;
    }
  }

  /* --- 2: a wand. A ring that blows outward and fades. ------------------- */
  .kind-2 {
    width: 3rem;
    height: 3rem;
    border-radius: 50%;
    border: 3px solid rgba(186, 160, 255, 0.95);
    box-shadow:
      0 0 18px rgba(150, 110, 255, 0.75),
      inset 0 0 14px rgba(200, 180, 255, 0.6);
    animation: burst 360ms ease-out forwards;
  }

  @keyframes burst {
    0% {
      opacity: 0;
      scale: 0.3;
    }
    20% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      scale: 2.4;
    }
  }

  /* A crit is the same shape, bigger and hotter - so it reads as "that one
     was the same thing, harder" rather than as a different attack. */
  .crit {
    filter: drop-shadow(0 0 10px rgba(255, 214, 92, 0.95)) brightness(1.4);
    scale: 1.35;
  }

  @media (prefers-reduced-motion: reduce) {
    .spark {
      animation-duration: 1ms;
      opacity: 0;
    }
  }
</style>
