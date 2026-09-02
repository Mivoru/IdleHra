<script lang="ts">
  // Modul: THE PREVIEW IS THE TEACHING MOMENT.
  //
  // Breeding is the densest system in the game and had the least explanation.
  // A separate help page would not have fixed that, because the moment a player
  // wants to understand breeding is the moment they are choosing between two
  // partners - so the explanation belongs where the choice is made, attached to
  // the actual numbers of the actual pair.
  //
  // Every figure below is either sent by the preview endpoint or computed from
  // two numbers it already sent. NOTHING NEW IS ASKED OF THE WIRE - see
  // ui/breeding.ts, which mirrors the server's own constants the same way
  // net/commands.ts already mirrors BonusPercentFor.
  //
  // Shared by both tabs. The two pairings differ in what a partner IS and in
  // what the pairing costs afterwards, not in how a child is made, and having
  // one of them explained was how the roster tab ended up saying something that
  // was not true (that a child can never beat a number the pair already has -
  // the drift roll and the epic roll both can, by one).
  //
  // Vocabulary is docs/breeding_model.md section 0: APTITUDE, BLOODLINE, GENE
  // (never "locus"), COPY (never "allele"), NEWCOMER then ELDER.
  import type { BreedingPreview } from '../net/rest';
  import { APTITUDE_MAX, aptitudeBonusPercent } from '../net/commands';
  import {
    breedingCostFor,
    driftOdds,
    epicChancePercent,
    geneBlurb,
    inheritChancePercent,
  } from './breeding';

  interface Props {
    preview: BreedingPreview;
    /** 'village' is hero x newcomer, 'roster' is hero x hero. */
    mode: 'village' | 'roster';
    /**
     * The generation the price is charged against - the hero's alone for a
     * newcomer pairing (a newcomer is generation 0 by definition), the higher
     * of the two for a roster pairing. Null when it is not known yet, in which
     * case the arithmetic behind the price is simply not shown rather than
     * guessed at.
     */
    generation: number | null;
  }

  const { preview, mode, generation }: Props = $props();

  const inbred = $derived(preview.IsInbredRisk);
  const drift = $derived(driftOdds(inbred));
  const epic = $derived(epicChancePercent(inbred));

  const firstParent = $derived(mode === 'village' ? 'you' : 'the father');
  const secondParent = $derived(mode === 'village' ? 'them' : 'the mother');

  /** The named genes, in the order the endpoint sends them, minus Race - which
   *  is a hard requirement rather than an outcome and is already explained by
   *  the refusal when it does not match. */
  const genes = $derived(preview.Loci.filter((g) => g.LocusName !== 'Race'));

  /** What the top of a band would be worth, so the bloodline panel's percentage
   *  and this screen's raw points are the same currency. */
  function worth(points: number): string {
    return `+${aptitudeBonusPercent(Math.min(points, APTITUDE_MAX)).toFixed(1)}%`;
  }
</script>

<!-- The heading is load-bearing beyond the layout: exercise.mjs asserts on it,
     because a preview that renders without quoting what a child would inherit
     is the exact failure this screen shipped with once. -->
<h3>What the child would inherit</h3>

<ul class="apts">
  {#each preview.Aptitudes as apt (apt.AptitudeName)}
    {@const heroOdds = inheritChancePercent(apt.ParentHero, apt.ParentPartner)}
    {@const favoured = apt.ParentHero === apt.ParentPartner
      ? null
      : apt.ParentHero > apt.ParentPartner
        ? firstParent
        : secondParent}
    <li>
      <div class="head">
        <span class="name">{apt.AptitudeName}</span>
        <span
          class="band"
          class:up={apt.PredictedMax > Math.max(apt.ParentHero, apt.ParentPartner)}
        >
          {apt.PredictedMin}&ndash;{apt.PredictedMax}
        </span>
      </div>
      <p class="why dim tiny">
        {firstParent}
        {apt.ParentHero} &middot; {secondParent}
        {apt.ParentPartner} &mdash;
        {#if favoured === null}
          a coin flip, both are {apt.ParentHero}
        {:else}
          {heroOdds}% to copy {firstParent === 'you' ? 'yours' : `${firstParent}'s`},
          {100 - heroOdds}% {secondParent === 'them' ? 'theirs' : `${secondParent}'s`},
          so it leans to {favoured}
        {/if}
        &middot; the top of that band is worth {worth(apt.PredictedMax)}
      </p>
    </li>
  {/each}
</ul>

<!-- WHY the band is the band. Each of these is a real roll the server makes in
     this order, and the epic one is deliberately outside the quoted range. -->
<div class="rules">
  <p class="dim tiny">
    Each aptitude is <strong>copied whole from one parent</strong>, weighted as
    above. Then it drifts: <strong>{drift.up}% +1</strong>,
    <strong>{drift.down}% &minus;1</strong>, {drift.same}% unchanged &mdash;
    which is the only reason a band reaches one past the better parent.
  </p>
  {#if inbred}
    <p class="warn-line tiny">
      These two are related, so the drift is <strong>inverted</strong>: it is
      more likely to lose a point than gain one, and the epic roll falls from
      5% to {epic}%. Related pairs also lose a quarter of every gene below.
    </p>
  {/if}
  <p class="dim tiny">
    <strong>{epic}%</strong> of children are epic: <strong>+1 to all four</strong>
    on top, and it is marked on them forever. That extra point is
    <em>not</em> in the bands above, so an epic child beats them by one.
  </p>
  <p class="dim tiny">
    Cross two <strong>different</strong> specialists and each aptitude leans to
    whichever parent is better at it &mdash; the child comes out good at both.
    Two similar parents just reproduce what you already have.
  </p>
</div>

{#if genes.length > 0}
  <h3>And its genes</h3>
  <ul class="genes">
    {#each genes as gene (gene.LocusName)}
      <li>
        <div class="head">
          <span class="name">{gene.LocusName}</span>
          <span class="band">{gene.PredictedMinDominant}&ndash;{gene.PredictedMaxDominant}</span>
        </div>
        <p class="why dim tiny">
          {geneBlurb(gene.LocusName)}
          {#if geneBlurb(gene.LocusName)}&middot;{/if}
          {firstParent} {gene.ParentPaternalDominant} &middot; {secondParent}
          {gene.ParentMaternalDominant}
          {#if gene.MutationChancePct > 0}
            &middot; {gene.MutationChancePct.toFixed(1)}% chance of a mutation
          {/if}
        </p>
      </li>
    {/each}
  </ul>
  <p class="dim tiny">
    Every gene has two copies. Each parent passes one of theirs at random and
    the higher of the two becomes the child's, so a strong recessive copy can
    surface a generation later. Mutations get rarer with every generation, which
    is why genes drift slowly where aptitudes climb.
  </p>
{/if}

<h3>And what it will be</h3>
<p class="dim tiny arrival">
  <strong>Level 1, a child</strong>, one of the two sexes at random, generation
  {generation === null ? '+1' : generation + 1}, at the end of your roster.
  It does not grow up on the bench: <strong>field it</strong> in the Hall of
  Ancestors and give it about an hour of play to reach Adult, then level 50
  before it can be a parent itself.
</p>
<p class="dim tiny arrival">
  {#if mode === 'village'}
    Your hero rests an hour afterwards. The villager becomes an
    <strong>elder</strong> &mdash; everybody marries into your line exactly once.
  {:else}
    Both parents rest an hour afterwards.
  {/if}
  {#if generation !== null}
    The price is 500g per generation: generation {generation}, so
    {breedingCostFor(generation).toLocaleString()}g.
  {/if}
</p>

<style>
  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  ul {
    list-style: none;
    margin: 0 0 0.6rem;
    padding: 0;
    display: grid;
    gap: 0.35rem;
  }

  /* Modul: NO FIXED COLUMN GRID HERE, deliberately.
     This panel sits in an auto-fit grid whose track can be as narrow as 21rem,
     and a container is not a viewport - a `1fr auto auto` row with three pieces
     of text in it crops at that width no matter how wide the window is. That
     is exactly how the guild buff tiers shipped cropped. Name and band on one
     flex line, reasoning on its own wrapping line under it, and every box
     min-width:0 so a long word shrinks the row instead of widening it. */
  li {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.28rem;
  }

  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
    min-width: 0;
    font-size: 0.82rem;
  }

  .name {
    font-weight: 600;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .band {
    flex: none;
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  /* The only outcome that moves a bloodline: a number neither parent had. */
  .band.up {
    color: var(--brass-lit, inherit);
  }

  .why {
    margin: 0;
    line-height: 1.35;
    overflow-wrap: anywhere;
  }

  .rules {
    display: grid;
    gap: 0.3rem;
    margin: 0 0 0.6rem;
    padding: 0.5rem 0.6rem;
    background: var(--bg);
    border-radius: var(--radius);
  }

  .rules p,
  .arrival {
    margin: 0;
    line-height: 1.4;
  }

  .arrival + .arrival {
    margin-top: 0.35rem;
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }

  .warn-line {
    margin: 0;
    line-height: 1.4;
    color: var(--danger);
  }
</style>
