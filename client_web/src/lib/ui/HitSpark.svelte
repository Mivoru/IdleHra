<script lang="ts">
  // Modul: THE HIT HAD NO MARK.
  //
  // A swing lands every 1.5 seconds for hours and the only thing that ever
  // moved was the health bar. This draws what the blow was: a slash for a
  // blade, a streak for an arrow, a burst for a wand - and a louder version of
  // whichever it is when the blow crit.
  //
  // CSS RATHER THAN A CANVAS OR A PARTICLE LIBRARY. A few shapes on a keyframe
  // each is what this needs, and the browser composites transforms and opacity
  // off the main thread - which matters here more than usual, because keying an
  // effect on the damage stream is exactly what starved the main thread the
  // last time this client tried to animate combat.
  //
  // Modul: A BLADE CUTS RED (owner, 2026-10-28). The slash was a thin beige arc
  // and "did not look like it cut anything". It is a tapered blade stroke now,
  // dark red at its core and lighter at its edges, drawn across the monster.
  // A crit crosses it with a second stroke, and a Double Strike crit adds a
  // white-hot flash - so the three outcomes read apart at a glance.
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
      <span class="spark kind-{spark.weaponKind}" class:crit={spark.isCrit} class:double={spark.isDoubleCrit}>
        <span class="stroke a"></span>
        {#if spark.isCrit}<span class="stroke b"></span>{/if}
        {#if spark.isDoubleCrit}<span class="flash"></span>{/if}
      </span>
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
    width: 0;
    height: 0;
  }

  .stroke,
  .flash {
    position: absolute;
    left: 0;
    top: 0;
  }

  /* --- 0: a blade. A tapered red stroke that is drawn across, then fades. --
     The glow is on the parent: a drop-shadow on the stroke itself would be
     cut away by its own clip-path. */
  .kind-0 {
    filter: drop-shadow(0 0 6px rgba(255, 40, 30, 0.75));
  }

  .kind-0 .stroke {
    width: 9rem;
    height: 1.1rem;
    margin: -0.55rem 0 0 -4.5rem;
    /* Across the stroke: light edge, dark core, light edge. */
    background: linear-gradient(
      180deg,
      transparent 0%,
      #ff8a7a 22%,
      #e0201a 40%,
      #6e0000 50%,
      #e0201a 60%,
      #ff8a7a 78%,
      transparent 100%
    );
    /* A lens: sharp at both tips, widest a little past the middle, the way
       a blade's arc thickens where it bites. */
    clip-path: polygon(0% 50%, 30% 30%, 62% 18%, 100% 50%, 62% 82%, 30% 70%);
    opacity: 0;
  }

  .kind-0 .stroke.a {
    rotate: -32deg;
    animation: slash-a 380ms cubic-bezier(0.2, 0.8, 0.3, 1) forwards;
  }

  .kind-0 .stroke.b {
    rotate: 32deg;
    animation: slash-b 380ms cubic-bezier(0.2, 0.8, 0.3, 1) 70ms forwards;
  }

  /* Drawn along its own line (the individual `rotate` applies before
     `transform`, so translateX here runs along the diagonal), then the stroke
     thins and fades. */
  @keyframes slash-a {
    0% {
      opacity: 1;
      transform: translateX(-30%) scaleX(0.1) scaleY(1);
    }
    40% {
      opacity: 1;
      transform: translateX(0) scaleX(1) scaleY(1);
    }
    100% {
      opacity: 0;
      transform: translateX(6%) scaleX(1.05) scaleY(0.2);
    }
  }

  @keyframes slash-b {
    0% {
      opacity: 1;
      transform: translateX(-30%) scaleX(0.1) scaleY(1);
    }
    40% {
      opacity: 1;
      transform: translateX(0) scaleX(1) scaleY(1);
    }
    100% {
      opacity: 0;
      transform: translateX(6%) scaleX(1.05) scaleY(0.2);
    }
  }

  .kind-0.crit .stroke {
    width: 11rem;
    height: 1.35rem;
    margin: -0.675rem 0 0 -5.5rem;
  }

  .kind-0.crit {
    filter: drop-shadow(0 0 9px rgba(255, 50, 30, 0.9));
  }

  /* --- 1: an arrow. A bright streak drives in and a ring marks the impact. */
  .kind-1 {
    filter: drop-shadow(0 0 5px rgba(255, 220, 140, 0.85));
  }

  .kind-1 .stroke.a {
    width: 7rem;
    height: 4px;
    margin: -2px 0 0 -7rem;
    background: linear-gradient(90deg, transparent, rgba(255, 214, 140, 0.7) 55%, #fff6dc 92%, #ffffff);
    border-radius: 2px;
    animation: arrow 320ms cubic-bezier(0.2, 0.7, 0.3, 1) forwards;
  }

  @keyframes arrow {
    0% {
      opacity: 0;
      transform: translateX(-120%);
    }
    25% {
      opacity: 1;
    }
    60% {
      opacity: 1;
      transform: translateX(0);
    }
    100% {
      opacity: 0;
      transform: translateX(4%);
    }
  }

  /* The impact: a ring at the arrowhead, after the streak arrives. */
  .kind-1 .stroke.a::after {
    content: '';
    position: absolute;
    right: -0.9rem;
    top: 50%;
    width: 1.8rem;
    height: 1.8rem;
    margin-top: -0.9rem;
    border-radius: 50%;
    border: 2px solid rgba(255, 236, 190, 0.95);
    opacity: 0;
    animation: impact 320ms ease-out 170ms forwards;
  }

  @keyframes impact {
    0% {
      opacity: 1;
      transform: scale(0.2);
    }
    100% {
      opacity: 0;
      transform: scale(1.6);
    }
  }

  /* A crit fires a second arrow along a slightly different line. */
  .kind-1 .stroke.b {
    width: 7rem;
    height: 3px;
    margin: 0.6rem 0 0 -7rem;
    background: linear-gradient(90deg, transparent, rgba(255, 214, 140, 0.6) 55%, #fff6dc);
    border-radius: 2px;
    rotate: -8deg;
    opacity: 0;
    animation: arrow 320ms cubic-bezier(0.2, 0.7, 0.3, 1) 60ms forwards;
  }

  .kind-1.crit {
    filter: drop-shadow(0 0 8px rgba(255, 200, 90, 1));
  }

  /* --- 2: a wand. A ring that blows outward and fades. Smaller than it was
     (owner, 2026-10-28): it covered most of the monster on a phone. --------- */
  .kind-2 .stroke.a {
    width: 2.2rem;
    height: 2.2rem;
    margin: -1.1rem 0 0 -1.1rem;
    border-radius: 50%;
    border: 2px solid rgba(186, 160, 255, 0.95);
    box-shadow:
      0 0 12px rgba(150, 110, 255, 0.75),
      inset 0 0 10px rgba(200, 180, 255, 0.6);
    animation: burst 340ms ease-out forwards;
  }

  .kind-2 .stroke.b {
    width: 1.4rem;
    height: 1.4rem;
    margin: -0.7rem 0 0 -0.7rem;
    border-radius: 50%;
    background: radial-gradient(circle, rgba(235, 225, 255, 0.95), rgba(150, 110, 255, 0) 70%);
    animation: burst 300ms ease-out forwards;
  }

  @keyframes burst {
    0% {
      opacity: 0;
      transform: scale(0.3);
    }
    20% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      transform: scale(1.8);
    }
  }

  .kind-2.crit .stroke.a {
    border-color: rgba(255, 230, 160, 0.95);
    box-shadow:
      0 0 14px rgba(255, 214, 92, 0.85),
      inset 0 0 10px rgba(255, 236, 190, 0.6);
  }

  /* --- Double Strike: one white-hot flash over whichever shape it was. ---- */
  .flash {
    width: 6rem;
    height: 6rem;
    margin: -3rem 0 0 -3rem;
    border-radius: 50%;
    background: radial-gradient(circle, rgba(255, 255, 255, 0.95), rgba(255, 230, 170, 0.5) 35%, transparent 70%);
    animation: flash 260ms ease-out forwards;
  }

  @keyframes flash {
    0% {
      opacity: 0;
      transform: scale(0.4);
    }
    30% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      transform: scale(1.5);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .stroke,
    .stroke::after,
    .flash {
      animation-duration: 1ms !important;
      opacity: 0 !important;
    }
  }
</style>
