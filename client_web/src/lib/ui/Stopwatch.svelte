<script lang="ts">
  // Modul: ONE STOPWATCH, drawn once. Village drew the same SVG twice at two
  // sizes, and task 28 had to be fixed in both.
  //
  // THE HANDS TURN, NOT THE WATCH. The first version animated the whole
  // <svg>, so the case, the crown and the bow rotated with the hands and the
  // thing spun like a dropped coin; the hands and the crown were also a single
  // <path>, so there was nothing to animate separately until they were split
  // (4926886). That fix pivoted the hand with `transform-box: view-box;
  // transform-origin: 12px 13px`.
  //
  // THE HANDS ARE NOW DRAWN AROUND (0,0) INSIDE translate(12 13), so the dial
  // centre IS the hand's own origin. A plain rotate() then turns about the
  // centre with no help from transform-box. The owner's phone kept showing
  // the whole watch spinning after 4926886 - it turned out to be on a frozen
  // over-the-air bundle (1.0.464, from before that commit; see
  // ops/oracle/deploy.sh), not an engine defect - but the pivot should not
  // depend on an engine resolving `transform-origin: 12px 13px` against the
  // right box. `transform-origin: 0 0` means the same thing under view-box
  // and under the initial value; only fill-box would move it, and view-box
  // is set explicitly below.
  interface Props {
    /** Rendered size in px; the viewBox is always 24. */
    size?: number;
    /** Accessible name. Omit for a decorative watch next to text that already says it. */
    label?: string;
  }
  const { size = 14, label }: Props = $props();
</script>

<span
  class="stopwatch"
  role={label ? 'img' : undefined}
  aria-label={label}
  aria-hidden={label ? undefined : 'true'}
>
  <svg
    viewBox="0 0 24 24"
    width={size}
    height={size}
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
  >
    <circle cx="12" cy="13" r="8" />
    <!-- The crown and the bow, which do not move. -->
    <path d="M9 2h6M12 2v3" />
    <!-- The hands, drawn about the dial centre. Was M12 9v4l2.5 2.5. -->
    <g transform="translate(12 13)">
      <path class="hand" d="M0 -4V0l2.5 2.5" />
    </g>
  </svg>
</span>

<style>
  .stopwatch {
    display: inline-flex;
    align-items: center;
    color: var(--accent);
    flex: none;
  }

  /* A stopwatch that does not move is a picture of a stopwatch. Only the
     hands turn. Modul: the animation sits on the inner <path>, NEVER on the
     <g>: a CSS transform on the <g> would REPLACE its translate(12 13)
     attribute and the hands would jump to the corner. */
  .hand {
    transform-box: view-box;
    transform-origin: 0 0;
    animation: tick 2s steps(8, end) infinite;
  }

  @keyframes tick {
    to {
      transform: rotate(360deg);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .hand {
      animation: none;
    }
  }
</style>
