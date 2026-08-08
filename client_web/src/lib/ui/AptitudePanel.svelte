<script lang="ts">
  // Modul: WHAT THE BLOODLINE IS WORTH, shown.
  //
  // Aptitudes are bred, stored and consumed by combat, health, gathering and
  // loot - and until this panel existed a player had no way to see any of it.
  // A bonus nobody can see is indistinguishable from one that does not work,
  // which is a defect this project has shipped repeatedly.
  //
  // The bands are drawn rather than stated because the interesting fact about
  // an aptitude is not its number, it is that the next point is worth less
  // than the last one - and that a cap of 50 is a long way from 20.
  import { playerState } from '../stores/game';
  import {
    APTITUDES,
    aptitudeBonusPercent,
    APTITUDE_MAX,
    APTITUDE_VILLAGE_CEILING,
  } from '../net/commands';

  const snap = $derived($playerState);

  const rows = $derived(
    APTITUDES.map((apt) => {
      const value = Number(
        snap?.[apt.field as keyof typeof snap] ?? 0,
      );
      return {
        ...apt,
        value,
        bonus: aptitudeBonusPercent(value),
        /** What one more point would add, which is the number that shows the curve. */
        next: aptitudeBonusPercent(value + 1) - aptitudeBonusPercent(value),
      };
    }),
  );
</script>

<section class="panel apt">
  <header>
    <h3>Bloodline</h3>
    <p class="dim small">
      What your line has bred into itself. These survive the season - levels and
      gear do not.
    </p>
  </header>

  <ul>
    {#each rows as row (row.field)}
      <li>
        <div class="line">
          <strong>{row.name}</strong>
          <span class="num">{row.value} / {APTITUDE_MAX}</span>
          <span class="worth">+{row.bonus.toFixed(1)}%</span>
        </div>

        <!-- Two marks on the track: the village ceiling at 20, and the cap.
             Everything past the first mark is generations of selection, which
             is the part worth knowing before you plan a season around it. -->
        <div class="track" role="img" aria-label={`${row.value} of ${APTITUDE_MAX}`}>
          <span class="fill" style={`width: ${(row.value / APTITUDE_MAX) * 100}%`}></span>
          <span class="mark" style={`left: ${(APTITUDE_VILLAGE_CEILING / APTITUDE_MAX) * 100}%`}></span>
        </div>

        <p class="dim tiny">{row.blurb} &middot; next point +{row.next.toFixed(1)}%</p>
      </li>
    {/each}
  </ul>

  <p class="dim tiny foot">
    The mark is {APTITUDE_VILLAGE_CEILING} — as far as village blood can carry a
    line. Past it is breeding alone.
  </p>
</section>

<style>
  .apt {
    display: grid;
    gap: 0.5rem;
  }

  h3 {
    margin: 0 0 0.1rem;
  }

  header p {
    margin: 0;
  }

  ul {
    display: grid;
    gap: 0.5rem;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    display: grid;
    gap: 0.2rem;
    min-width: 0;
  }

  .line {
    display: flex;
    align-items: baseline;
    gap: 0.4rem;
  }

  .num {
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
    font-size: 0.82rem;
  }

  .worth {
    margin-left: auto;
    color: var(--brass-lit);
    font-variant-numeric: tabular-nums;
    font-size: 0.85rem;
  }

  .track {
    position: relative;
    height: 6px;
    border-radius: 3px;
    background: var(--bg);
    overflow: hidden;
  }

  .fill {
    display: block;
    height: 100%;
    background: var(--brass-lit);
  }

  .mark {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 1px;
    background: var(--text-dim);
    opacity: 0.7;
  }

  li p {
    margin: 0;
  }

  .foot {
    margin: 0;
  }
</style>
