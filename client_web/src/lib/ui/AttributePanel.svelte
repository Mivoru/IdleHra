<script lang="ts">
  /*
    THE ATTRIBUTE ALLOCATION WINDOW.

    Attributes became a player's choice on 2026-09-06, and a choice needs a
    place to be made. Before this the four numbers sat in a row of the stat
    block, unexplained - the game had never once said what any of them did.

    What this has to show, in order of what a player actually asks:

      1. how many points are waiting              (the header, loudest thing)
      2. what each attribute IS                   (name, tagline, colour)
      3. what a point buys RIGHT NOW              (live preview, not a formula)
      4. what committing further unlocks          (the milestone track)
      5. how to undo it                           (respec)

    The milestone track is the part that makes this worth looking at twice: it
    turns "put points somewhere" into a route with landmarks on it, and it is
    why the panel is a track per attribute rather than four spinners.
  */
  import {
    ATTRIBUTES,
    ATTRIBUTE_MILESTONES,
    ATTRIBUTE_CURVES,
    diminishedPercent,
    spendAttributePoint,
    respecAttributes,
  } from '../net/commands';

  let {
    values,
    unspent,
    onnotice,
  }: {
    values: Record<'STR' | 'DEX' | 'CON' | 'LCK', number>;
    unspent: number;
    onnotice: (message: string) => void;
  } = $props();

  // Which card's track is expanded. Only one at a time - five rungs each across
  // four attributes is eighty lines of text if they all open, which is a wall
  // rather than a panel.
  let openTrack = $state<number | null>(null);

  function spend(attributeId: number, amount: number) {
    const outcome = spendAttributePoint(attributeId, amount, unspent);
    if (!outcome.ok) onnotice(outcome.reason);
  }

  function respec() {
    // Placed points are gone from the pool until this returns them, and a
    // misclick here would undo a season of decisions - so it asks.
    if (!confirm('Return every placed attribute point to the pool?\n\nYou keep the points and place them again. Nothing else changes.')) return;
    const outcome = respecAttributes();
    if (!outcome.ok) onnotice(outcome.reason);
  }

  /** The rungs of one attribute, with whether each is reached. */
  function trackOf(attributeId: number, value: number) {
    return ATTRIBUTE_MILESTONES.filter((m) => m.attribute === attributeId).map((m) => ({
      ...m,
      reached: value >= m.threshold,
    }));
  }

  function nextMilestone(attributeId: number, value: number) {
    return trackOf(attributeId, value).find((m) => !m.reached) ?? null;
  }

  /**
   * What this attribute is buying at its current value, in the units a player
   * reads. Mirrors StatsCalculator - the flat effects are linear and the
   * percentages are on the same square root the server uses.
   *
   * Modul: NOT named `derived`. A local binding of that name shadows the
   * `$derived` rune, so the compiler read `$derived(...)` below as a store
   * auto-subscription on this function and the whole Character screen threw
   * `store_invalid_shape` at runtime - which svelte-check does not catch,
   * because it is legal TypeScript.
   */
  function derivedLines(key: string, value: number): string[] {
    if (key === 'STR') {
      return [`${(value * 2).toLocaleString()} attack power`, `${value.toLocaleString()} armour penetration`];
    }
    if (key === 'DEX') {
      return [
        `${value.toLocaleString()} accuracy`,
        `${diminishedPercent(ATTRIBUTE_CURVES.critChancePerRootPoint, value).toFixed(1)}% crit chance`,
        `${diminishedPercent(ATTRIBUTE_CURVES.attackSpeedPerRootPoint, value).toFixed(1)}% attack speed`,
      ];
    }
    if (key === 'CON') {
      return [
        `${(value * 15).toLocaleString()} max health`,
        `${value.toLocaleString()} armour`,
        `${diminishedPercent(ATTRIBUTE_CURVES.blockStrengthPerRootPoint, value).toFixed(1)}% block`,
      ];
    }
    // Modul: loot luck reweights the rarity ROLL - it is not a drop chance,
    // which is a flat 15% and nothing moves it. Said plainly here because the
    // old label ("loot luck") let everyone assume the opposite.
    return [
      `${diminishedPercent(ATTRIBUTE_CURVES.lootLuckPerRootPoint, value).toFixed(1)}% rarer drops`,
      `${diminishedPercent(ATTRIBUTE_CURVES.rarityElevationPerRootPoint, value).toFixed(1)}% chance to elevate a drop a tier`,
    ];
  }

  /**
   * What ONE more point would add, so the card can answer "is this worth it"
   * without the player doing arithmetic on a square root. The curved effects
   * pay less the more you have, which is the whole point of the curve and is
   * invisible unless it is shown.
   */
  function nextPointGain(key: string, value: number): string {
    if (key === 'STR') return '+2 attack power, +1 penetration';
    if (key === 'DEX') {
      const crit =
        diminishedPercent(ATTRIBUTE_CURVES.critChancePerRootPoint, value + 1) -
        diminishedPercent(ATTRIBUTE_CURVES.critChancePerRootPoint, value);
      return `+1 accuracy, +${crit.toFixed(2)}% crit`;
    }
    if (key === 'CON') return '+15 health, +1 armour';
    const elevate =
      diminishedPercent(ATTRIBUTE_CURVES.rarityElevationPerRootPoint, value + 1) -
      diminishedPercent(ATTRIBUTE_CURVES.rarityElevationPerRootPoint, value);
    return `+${elevate.toFixed(3)}% elevation`;
  }

  const totalPlaced = $derived(
    ATTRIBUTES.reduce((sum, a) => sum + Math.max(0, (values[a.key] ?? 0) - a.start), 0),
  );
</script>

<section class="attrpanel">
  <header>
    <div>
      <h3>Attributes</h3>
      <p class="dim tiny">Every level pays 7 points. {totalPlaced.toLocaleString()} placed so far.</p>
    </div>
    <div class="pool" class:ready={unspent > 0}>
      <strong>{unspent.toLocaleString()}</strong>
      <span>points to spend</span>
    </div>
  </header>

  <div class="cards">
    {#each ATTRIBUTES as attribute}
      {@const value = values[attribute.key] ?? 0}
      {@const next = nextMilestone(attribute.id, value)}
      {@const reachedCount = trackOf(attribute.id, value).filter((m) => m.reached).length}
      <article class="card" style="--accent: {attribute.accent}">
        <div class="cardhead">
          <div class="title">
            <h4>{attribute.label}</h4>
            <span class="dim tiny">{attribute.tagline}</span>
          </div>
          <div class="value">{value.toLocaleString()}</div>
        </div>

        <ul class="derived">
          {#each derivedLines(attribute.key, value) as line}
            <li>{line}</li>
          {/each}
        </ul>

        <!-- The track, as a row of pips: reached rungs filled, the next one
             outlined. A player can see at a glance how far along an attribute
             they are without opening anything. -->
        <button
          class="track"
          onclick={() => (openTrack = openTrack === attribute.id ? null : attribute.id)}
          title="Show the milestone track"
        >
          <span class="pips">
            {#each trackOf(attribute.id, value) as milestone}
              <span class="pip" class:on={milestone.reached}></span>
            {/each}
          </span>
          <span class="dim tiny">
            {#if next}
              {reachedCount}/5 — next at {next.threshold}: {next.name}
            {:else}
              5/5 — track complete
            {/if}
          </span>
        </button>

        {#if openTrack === attribute.id}
          <ol class="milestones">
            {#each trackOf(attribute.id, value) as milestone}
              <li class:reached={milestone.reached}>
                <span class="at">{milestone.threshold}</span>
                <span class="name">{milestone.name}</span>
                <span class="dim tiny">{milestone.effect}</span>
              </li>
            {/each}
          </ol>
        {/if}

        <div class="spend">
          <span class="dim tiny gain">{nextPointGain(attribute.key, value)}</span>
          <span class="buttons">
            <button disabled={unspent < 1} onclick={() => spend(attribute.id, 1)}>+1</button>
            <button disabled={unspent < 10} onclick={() => spend(attribute.id, 10)}>+10</button>
            {#if next && unspent >= next.threshold - value && next.threshold > value}
              <button
                class="tonext"
                onclick={() => spend(attribute.id, next.threshold - value)}
                title="Enough to reach {next.name}"
              >
                → {next.name}
              </button>
            {/if}
          </span>
        </div>
      </article>
    {/each}
  </div>

  <footer>
    <button class="respec" disabled={totalPlaced < 1} onclick={respec}>Return every placed point</button>
    <span class="dim tiny">Free. You place them again — nothing else changes.</span>
  </footer>
</section>

<style>
  .attrpanel {
    margin-top: 1rem;
  }

  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 0.6rem;
  }
  header h3 {
    margin: 0;
  }
  header p {
    margin: 0.1rem 0 0;
  }

  /* The pool is the loudest thing on the panel, because "have I got points
     waiting" is the question that brings anyone here. */
  .pool {
    display: flex;
    flex-direction: column;
    align-items: center;
    min-width: 5.5rem;
    padding: 0.35rem 0.7rem;
    border: 1px solid var(--border);
    border-radius: 0.5rem;
    line-height: 1.1;
  }
  .pool strong {
    font-size: 1.4rem;
  }
  .pool span {
    font-size: 0.7rem;
    opacity: 0.7;
  }
  .pool.ready {
    border-color: var(--accent, #c9a227);
    box-shadow: 0 0 0 1px var(--accent, #c9a227) inset;
  }

  .cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 0.6rem;
  }

  .card {
    position: relative;
    border: 1px solid var(--border);
    border-radius: 0.6rem;
    padding: 0.6rem 0.7rem;
    background: color-mix(in srgb, var(--accent) 6%, transparent);
    overflow: hidden;
  }
  /* A colour spine, so the four are told apart before they are read. */
  .card::before {
    content: '';
    position: absolute;
    inset: 0 auto 0 0;
    width: 3px;
    background: var(--accent);
  }

  .cardhead {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.5rem;
  }
  .cardhead h4 {
    margin: 0;
    color: var(--accent);
  }
  .title span {
    display: block;
  }
  .value {
    font-size: 1.5rem;
    font-variant-numeric: tabular-nums;
    line-height: 1;
  }

  ul.derived {
    list-style: none;
    margin: 0.5rem 0 0.4rem;
    padding: 0;
    font-size: 0.78rem;
    opacity: 0.85;
  }
  ul.derived li + li {
    margin-top: 0.1rem;
  }

  button.track {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    width: 100%;
    padding: 0.25rem 0;
    background: none;
    border: none;
    border-top: 1px solid var(--border);
    text-align: left;
    cursor: pointer;
  }
  .pips {
    display: inline-flex;
    gap: 3px;
    flex: 0 0 auto;
  }
  .pip {
    width: 9px;
    height: 9px;
    border-radius: 50%;
    border: 1px solid var(--accent);
    opacity: 0.45;
  }
  .pip.on {
    background: var(--accent);
    opacity: 1;
  }

  ol.milestones {
    list-style: none;
    margin: 0.35rem 0;
    padding: 0;
    font-size: 0.75rem;
  }
  ol.milestones li {
    display: grid;
    grid-template-columns: 2.4rem 1fr;
    gap: 0.1rem 0.4rem;
    padding: 0.2rem 0;
    opacity: 0.45;
  }
  ol.milestones li.reached {
    opacity: 1;
  }
  ol.milestones .at {
    grid-row: span 2;
    font-variant-numeric: tabular-nums;
    text-align: right;
    opacity: 0.7;
  }
  ol.milestones .name {
    font-weight: 600;
  }
  ol.milestones li.reached .name {
    color: var(--accent);
  }

  .spend {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.4rem;
    margin-top: 0.35rem;
    border-top: 1px solid var(--border);
    padding-top: 0.4rem;
    flex-wrap: wrap;
  }
  .gain {
    flex: 1 1 auto;
  }
  .buttons {
    display: inline-flex;
    gap: 0.25rem;
    flex-wrap: wrap;
  }
  .spend button {
    padding: 0.1rem 0.45rem;
    font-size: 0.75rem;
    min-width: 2.2rem;
  }
  .spend button.tonext {
    border-color: var(--accent);
    color: var(--accent);
    min-width: 0;
  }

  footer {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    margin-top: 0.6rem;
    flex-wrap: wrap;
  }
  button.respec {
    font-size: 0.78rem;
    padding: 0.2rem 0.6rem;
  }
</style>
