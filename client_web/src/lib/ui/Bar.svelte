<script lang="ts">
  // Modul: TASK 106 - A BAR SAYS WHAT IT MEASURES. It was a progressbar with
  // no name, so a screen reader announced "progress bar, 340" sixteen times
  // over a screen without saying of what. `ariaLabel` names it; without one the
  // visible `label` does, and the label is also the value text, so "1,200 /
  // 4,000" is read instead of a bare number.
  //
  // `tone` picks a theme colour by meaning; `color` stays for the callers that
  // pass a rarity or a computed colour. `size="sm"` is a thin meter (no room
  // for a label inside it - the label is still read out). The track is
  // --bar-track, a well that shows on parchment as well as on oak.
  interface Props {
    value: number;
    max: number;
    tone?: 'accent' | 'good' | 'danger' | 'warn' | 'brass' | 'info';
    color?: string;
    label?: string;
    ariaLabel?: string;
    size?: 'sm' | 'md';
  }

  let { value, max, tone = 'accent', color, label, ariaLabel, size = 'md' }: Props = $props();

  const TONES: Record<NonNullable<Props['tone']>, string> = {
    accent: 'var(--accent)',
    good: 'var(--good)',
    danger: 'var(--danger)',
    warn: 'var(--warn)',
    brass: 'var(--brass-lit)',
    info: 'var(--info)',
  };

  // Clamped, because an interpolated value can sit a hair outside the range
  // between snapshots and a bar wider than its track looks like a bug.
  const pct = $derived(max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0);
  const fill = $derived(color ?? TONES[tone]);
</script>

<div
  class="bar"
  class:bar-sm={size === 'sm'}
  role="progressbar"
  aria-label={ariaLabel ?? label}
  aria-valuetext={label}
  aria-valuenow={Math.round(value)}
  aria-valuemin="0"
  aria-valuemax={max}
>
  <div class="bar-fill" style="width: {pct}%; background: {fill};"></div>
  {#if label && size !== 'sm'}
    <span class="bar-label">{label}</span>
  {/if}
</div>
