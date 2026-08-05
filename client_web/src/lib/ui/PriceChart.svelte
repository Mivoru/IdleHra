<script lang="ts">
  import type { MarketPricePoint } from '../net/rest';

  // Inline SVG rather than a charting library: this draws one line and a fill,
  // the bundle ships to phones over a Capacitor wrapper, and every charting
  // dependency here would be several times the weight of the whole screen.
  let {
    points = [],
    height = 120,
  }: { points?: MarketPricePoint[]; height?: number } = $props();

  // A viewBox in abstract units with preserveAspectRatio="none" lets the chart
  // stretch to whatever width the panel gives it without recomputing anything
  // on resize. Stroke widths are compensated by vector-effect below, so the
  // line does not stretch with the box.
  const W = 300;
  const H = 100;

  const stats = $derived.by(() => {
    if (points.length === 0) return null;

    let low = Infinity;
    let high = -Infinity;
    for (const p of points) {
      if (p.Price < low) low = p.Price;
      if (p.Price > high) high = p.Price;
    }

    // A flat market has zero range, and dividing by it would put every point
    // at NaN. Give it a band so the line renders down the middle.
    const range = high - low || Math.max(1, high) * 0.1;
    const first = points[0].Epoch;
    const last = points[points.length - 1].Epoch;
    const span = last - first || 1;

    const xy = points.map((p) => ({
      x: ((p.Epoch - first) / span) * W,
      // SVG y grows downward, so a higher price is a smaller y.
      y: H - ((p.Price - (low - range * 0.1)) / (range * 1.2)) * H,
    }));

    return { xy, low, high, rising: points[points.length - 1].Price >= points[0].Price };
  });

  // A single trade is a point, not a line - `L` with nothing to draw to
  // produces an empty path, so it gets a dot instead.
  const linePath = $derived(
    stats && stats.xy.length > 1
      ? stats.xy.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(2)},${p.y.toFixed(2)}`).join(' ')
      : '',
  );

  const areaPath = $derived(linePath ? `${linePath} L${W},${H} L0,${H} Z` : '');
</script>

{#if !stats}
  <p class="empty">No trades recorded yet.</p>
{:else}
  <svg
    class="chart"
    viewBox="0 0 {W} {H}"
    preserveAspectRatio="none"
    style="height: {height}px"
    role="img"
    aria-label="Price history, {points.length} trades, {stats.rising ? 'rising' : 'falling'}"
  >
    <defs>
      <linearGradient id="priceFill" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stop-color="currentColor" stop-opacity="0.28" />
        <stop offset="100%" stop-color="currentColor" stop-opacity="0" />
      </linearGradient>
    </defs>

    <g class:rising={stats.rising} class:falling={!stats.rising}>
      {#if areaPath}
        <path d={areaPath} fill="url(#priceFill)" stroke="none" />
        <path
          d={linePath}
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linejoin="round"
          stroke-linecap="round"
          vector-effect="non-scaling-stroke"
        />
      {:else}
        <circle cx={stats.xy[0].x} cy={stats.xy[0].y} r="3" fill="currentColor" vector-effect="non-scaling-stroke" />
      {/if}
    </g>
  </svg>
{/if}

<style>
  .chart {
    display: block;
    width: 100%;
    overflow: visible;
  }

  /* Green up, red down - the convention every price chart a player has seen
     already uses. Colour is not the only carrier: the aria-label says which,
     and the percentage figures beside the chart say it in words. */
  .rising {
    color: var(--good, #4ade80);
  }

  .falling {
    color: var(--bad, #f87171);
  }

  .empty {
    margin: 0;
    font-size: 0.85rem;
    opacity: 0.7;
  }
</style>
