<script lang="ts">
  // Modul: THE ONE QUESTION, ANSWERED FIRST (2026-10-14).
  //
  // The preview used to open with a paragraph per aptitude - odds, leanings,
  // what the top of a band is worth - and the question a player is actually
  // asking ("what does this partner do for my line?") had to be assembled from
  // all of it. This draws it instead: both parents and the child's band on one
  // track per aptitude, the bloodline ceiling marked on it, and one sentence
  // underneath that says where the child can beat the hero.
  //
  // Every number is the preview endpoint's: PredictedMin/Max, SelectedMin for
  // a ticked aptitude, AtCeiling and BloodlineCeiling. Nothing here knows a
  // breeding rule; it compares numbers the server already sent.
  //
  // The heading is load-bearing beyond the layout: exercise.mjs asserts on
  // "What the child would inherit" and on the aptitude names.
  import type { BreedingPreview } from '../net/rest';
  import { APTITUDES, APTITUDE_MAX, aptitudeBonusPercent } from '../net/commands';

  interface Props {
    preview: BreedingPreview;
    /** Aptitudes ticked on the Breeding Grounds - bit i is APTITUDES[i]. */
    selectionMask: number;
    heroName: string;
    partnerName: string;
  }

  const { preview, selectionMask, heroName, partnerName }: Props = $props();

  const rows = $derived(
    preview.Aptitudes.map((apt, index) => {
      const selected = (selectionMask & (1 << index)) !== 0;
      const low = selected ? apt.SelectedMin : apt.PredictedMin;
      const high = apt.PredictedMax;
      const hero = apt.ParentHero;
      return {
        ...apt,
        index,
        selected,
        low,
        high,
        blurb: APTITUDES[index]?.blurb ?? '',
        /** Every outcome in the band beats the hero. */
        sure: low > hero,
        /** The band reaches above the hero. */
        can: high > hero,
        /** What the top of the band is worth, in the bloodline panel's currency. */
        worth: aptitudeBonusPercent(Math.min(high, APTITUDE_MAX)),
      };
    }),
  );

  const sure = $derived(rows.filter((r) => r.sure).map((r) => r.AptitudeName));
  const can = $derived(rows.filter((r) => r.can && !r.sure).map((r) => r.AptitudeName));
  const atCeiling = $derived(rows.some((r) => r.AtCeiling));

  function list(names: string[]): string {
    if (names.length <= 1) return names.join('');
    return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
  }

  /** Percent along a 0..APTITUDE_MAX track. */
  const pos = (value: number) => `${(Math.max(0, Math.min(value, APTITUDE_MAX)) / APTITUDE_MAX) * 100}%`;
</script>

<section class="forecast" data-testid="child-forecast">
  <h3>What the child would inherit</h3>

  <!-- The verdict, in one sentence, before any of the detail. -->
  <p class="verdict" class:good={sure.length > 0 || can.length > 0} data-testid="child-verdict">
    {#if sure.length > 0}
      <strong>Better than {heroName} in {list(sure)}</strong>{#if can.length > 0}, and can beat them in {list(can)}{/if}.
    {:else if can.length > 0}
      <strong>Can beat {heroName} in {list(can)}</strong> &mdash; not certain; a tick on the Grounds makes it surer.
    {:else}
      <strong>No better than {heroName} anywhere.</strong> Look for a partner stronger where your line is weak.
    {/if}
  </p>

  <ul class="tracks">
    {#each rows as r (r.AptitudeName)}
      <li class:selected={r.selected}>
        <div class="head">
          <span class="name">{r.AptitudeName}{#if r.selected}<span class="tick" title="Bred for on the Grounds">&#10003;</span>{/if}</span>
          <span class="band" class:up={r.can}>{r.low === r.high ? r.low : `${r.low}–${r.high}`}</span>
        </div>
        <div
          class="track"
          role="img"
          aria-label={`${r.AptitudeName}: ${heroName} ${r.ParentHero}, ${partnerName} ${r.ParentPartner}, child ${r.low} to ${r.high}`}
        >
          <span class="ceiling" style:left={pos(preview.BloodlineCeiling)}></span>
          <span class="range" class:up={r.can} style:left={pos(r.low)} style:width={`calc(${pos(r.high)} - ${pos(r.low)})`}></span>
          <span class="mark hero" style:left={pos(r.ParentHero)} title={`${heroName}: ${r.ParentHero}`}></span>
          <span class="mark partner" style:left={pos(r.ParentPartner)} title={`${partnerName}: ${r.ParentPartner}`}></span>
        </div>
        <p class="why dim tiny">
          {heroName} {r.ParentHero} &middot; {partnerName} {r.ParentPartner}
          &middot; the top is worth +{r.worth.toFixed(1)}%
          {#if r.AtCeiling}&middot; <span class="cap">held by the ceiling</span>{/if}
        </p>
      </li>
    {/each}
  </ul>

  <div class="legend dim tiny" aria-hidden="true">
    <span><i class="key key-hero"></i>{heroName}</span>
    <span><i class="key key-partner"></i>{partnerName}</span>
    <span><i class="key key-child"></i>the child</span>
    <span><i class="key key-ceiling"></i>ceiling {preview.BloodlineCeiling}</span>
  </div>

  {#if atCeiling}
    <p class="dim tiny ceiling-note" data-testid="ceiling-note">
      Your <strong>bloodline ceiling</strong> is {preview.BloodlineCeiling}: no child is bred above it.
      {#if preview.NextBloodlineCeiling > preview.BloodlineCeiling}
        Your next rebirth raises it to {preview.NextBloodlineCeiling}.
      {/if}
    </p>
  {/if}
</section>

<style>
  .forecast {
    display: grid;
    gap: 0.5rem;
  }

  h3 {
    margin: 0.9rem 0 0;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .verdict {
    margin: 0;
    padding: 0.55rem 0.7rem;
    border-radius: var(--radius);
    background: var(--bg);
    border-left: 3px solid var(--border);
    font-size: 0.85rem;
    line-height: 1.4;
  }

  .verdict.good {
    border-left-color: var(--brass-lit, var(--accent));
    background: color-mix(in srgb, var(--brass-lit, #d8b45a) 9%, var(--bg));
  }

  .tracks {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.55rem;
  }

  li {
    display: grid;
    gap: 0.2rem;
    min-width: 0;
  }

  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
    min-width: 0;
    font-size: 0.85rem;
  }

  .name {
    font-weight: 600;
    min-width: 0;
  }

  .tick {
    margin-left: 0.35rem;
    color: var(--brass-lit, var(--accent));
    font-size: 0.8em;
  }

  .band {
    flex: none;
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  .band.up {
    color: var(--brass-lit, inherit);
  }

  /* The track: 0..APTITUDE_MAX, so every row shares one scale and the long
     road past the village (20) is visible as the empty right-hand side. */
  .track {
    position: relative;
    height: 14px;
    border-radius: 7px;
    background: var(--bg);
    border: 1px solid var(--border);
    overflow: hidden;
  }

  .range {
    position: absolute;
    top: 2px;
    bottom: 2px;
    min-width: 6px;
    border-radius: 5px;
    background: color-mix(in srgb, var(--text-dim) 55%, transparent);
    transition:
      left 260ms ease,
      width 260ms ease,
      background-color 260ms ease;
  }

  .range.up {
    background: linear-gradient(90deg, var(--brass, #8a6a34), var(--brass-lit, #d8b45a));
    box-shadow: 0 0 8px var(--glow-soft, transparent);
  }

  li.selected .range {
    outline: 1px solid var(--glow-warm, #ffd98a);
  }

  .mark {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 3px;
    margin-left: -1px;
    border-radius: 2px;
    transition: left 260ms ease;
  }

  .mark.hero {
    background: var(--text);
  }

  .mark.partner {
    background: var(--diamond, #8fd3ff);
  }

  .ceiling {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 0;
    border-left: 2px dashed var(--danger);
    opacity: 0.8;
  }

  .why {
    margin: 0;
    line-height: 1.35;
    overflow-wrap: anywhere;
  }

  .cap {
    color: var(--danger);
  }

  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem 0.9rem;
  }

  .legend span {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
  }

  .key {
    display: inline-block;
    width: 10px;
    height: 10px;
    border-radius: 2px;
  }

  .key-hero {
    background: var(--text);
    width: 3px;
  }

  .key-partner {
    background: var(--diamond, #8fd3ff);
    width: 3px;
  }

  .key-child {
    background: linear-gradient(90deg, var(--brass, #8a6a34), var(--brass-lit, #d8b45a));
  }

  .key-ceiling {
    width: 0;
    border-left: 2px dashed var(--danger);
  }

  .ceiling-note {
    margin: 0;
    line-height: 1.4;
  }

  @media (prefers-reduced-motion: reduce) {
    .range,
    .mark {
      transition: none;
    }
  }
</style>
