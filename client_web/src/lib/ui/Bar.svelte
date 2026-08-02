<script lang="ts">
  interface Props {
    value: number;
    max: number;
    color?: string;
    label?: string;
  }

  let { value, max, color = 'var(--accent)', label }: Props = $props();

  // Clamped, because an interpolated value can sit a hair outside the range
  // between snapshots and a bar wider than its track looks like a bug.
  const pct = $derived(max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0);
</script>

<div class="bar" role="progressbar" aria-valuenow={Math.round(value)} aria-valuemin="0" aria-valuemax={max}>
  <div class="bar-fill" style="width: {pct}%; background: {color};"></div>
  {#if label}
    <span class="bar-label">{label}</span>
  {/if}
</div>
