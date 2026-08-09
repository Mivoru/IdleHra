<script lang="ts">
  // Modul: a handful of sparks thrown outward, for the moments that deserve
  // more than a border catching light.
  //
  // WHERE THIS IS USED AND WHERE IT IS NOT. A burst is loud, so it is reserved
  // for things that are rare by construction: a top-tier drop (1 in 21 at best,
  // and far rarer above), a forge fusion actually landing, a level. Putting one
  // on every kill would make it mean nothing, which is the same reason
  // rarity.ts only glows at tier 10 and above.
  //
  // Twelve spans on a transform each. No canvas, no library and no per-frame
  // JavaScript: the browser composites transform and opacity off the main
  // thread, and the main thread here is running a 10 Hz packet stream.
  interface Props {
    /** How many sparks. Kept small - this is punctuation, not weather. */
    count?: number;
    /** Sparks fly this far, in rem. */
    reach?: number;
    /** Gold by default; pass a rarity colour to match what dropped. */
    color?: string;
  }

  let { count = 12, reach = 3.2, color = 'var(--brass-lit)' }: Props = $props();

  // Angles are evenly spaced with a fixed jitter rather than a random one:
  // a burst that lands differently on every render looks like a glitch when
  // two fire at once, and an even fan reads as deliberate.
  const sparks = $derived(
    Array.from({ length: count }, (_, i) => {
      const angle = (360 / count) * i + (i % 2 === 0 ? 7 : -7);
      const radians = (angle * Math.PI) / 180;
      return {
        x: Math.cos(radians) * reach,
        y: Math.sin(radians) * reach,
        delay: (i % 3) * 30,
      };
    }),
  );
</script>

<span class="burst" aria-hidden="true">
  {#each sparks as spark, i (i)}
    <span
      class="spark"
      style="--x: {spark.x}rem; --y: {spark.y}rem; --delay: {spark.delay}ms; --tint: {color}"
    ></span>
  {/each}
</span>

<style>
  .burst {
    position: absolute;
    left: 50%;
    top: 50%;
    width: 0;
    height: 0;
    pointer-events: none;
  }

  .spark {
    position: absolute;
    width: 4px;
    height: 4px;
    border-radius: 50%;
    background: var(--tint);
    box-shadow: 0 0 6px var(--tint);
    animation: folk-burst 620ms ease-out var(--delay) forwards;
  }

  @keyframes folk-burst {
    0% {
      opacity: 0;
      translate: 0 0;
      scale: 0.4;
    }
    15% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      translate: var(--x) var(--y);
      scale: 0.6;
    }
  }

  /* The sparks are the whole component, so reduced motion removes it rather
     than freezing twelve dots on screen. */
  @media (prefers-reduced-motion: reduce) {
    .spark {
      display: none;
    }
  }
</style>
