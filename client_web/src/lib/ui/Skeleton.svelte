<script lang="ts">
  // Modul: a placeholder with the SHAPE of the thing being loaded.
  //
  // Nineteen places said "Loading..." in dim grey. That is honest but it makes
  // every screen reflow the moment data lands, and on this wire that can be a
  // second or more - so the player watches a bare line of text, then the whole
  // panel jumps. A block of the right size holds the space and tells them what
  // kind of thing is coming.
  //
  // The shimmer stops entirely under reduced-motion, where it becomes a flat
  // block: it is pure decoration and it is exactly the kind of continuous
  // movement that setting exists to remove.

  interface Props {
    /** How many placeholder rows. Match the usual length of the real list. */
    rows?: number;
    /** Row height. `line` for text, `row` for a list item with an icon. */
    variant?: 'line' | 'row';
  }

  const { rows = 3, variant = 'line' }: Props = $props();
</script>

<div class="skeleton" data-variant={variant} role="status" aria-label="Loading">
  {#each Array(rows) as _, index}
    <!-- Widths vary so it reads as text rather than as a broken table. The
         pattern is deterministic, not random: a placeholder that reshuffles on
         every render draws the eye to itself. -->
    <span class="bar" style="width: {[100, 82, 91, 74][index % 4]}%"></span>
  {/each}
</div>

<style>
  .skeleton {
    display: grid;
    gap: 0.4rem;
    margin: 0.3rem 0 0.6rem;
  }

  .bar {
    height: 0.75rem;
    border-radius: 4px;
    background: linear-gradient(
      90deg,
      var(--bg-raised) 0%,
      var(--border) 50%,
      var(--bg-raised) 100%
    );
    background-size: 200% 100%;
    animation: skeleton-sweep 1.4s ease-in-out infinite;
  }

  .skeleton[data-variant='row'] .bar {
    height: 2.2rem;
  }

  @keyframes skeleton-sweep {
    from {
      background-position: 200% 0;
    }
    to {
      background-position: -200% 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .bar {
      animation: none;
      background: var(--bg-raised);
    }
  }
</style>
