<script lang="ts">
  // Task 55: a region boss's three optional challenges, under its region on the
  // Combat screen. Each met once pays a cosmetic chest - the words, the level
  // cap and the chest rarity all come from the server.
  import { rarityClass, type BossChallengeRegion } from '../net/cosmetics';

  interface Props {
    row: BossChallengeRegion;
  }

  let { row }: Props = $props();

  const CHEST_NAMES = ['', 'Common', 'Rare', 'Epic', 'Legendary'];
  const done = $derived(row.Challenges.filter((c) => c.Completed).length);
</script>

<div class="challenges {rarityClass(row.ChestRarity)}" data-testid="boss-challenges-{row.Region}">
  <p class="head">
    <strong>Boss challenges</strong>
    <span class="dim tiny">{done} / {row.Challenges.length} · each pays a <span class="chest">{CHEST_NAMES[row.ChestRarity]} chest</span> once</span>
  </p>
  <ul>
    {#each row.Challenges as c (c.Id)}
      <li class:done={c.Completed} title={c.Description}>
        <span class="mark" aria-hidden="true">{c.Completed ? '✓' : '○'}</span>
        <span class="title">{c.Title}</span>
        <span class="dim tiny desc">{c.Description}</span>
      </li>
    {/each}
  </ul>
</div>

<style>
  .rare {
    --c: #4f9be0;
  }
  .epic {
    --c: #a66be0;
  }
  .legendary {
    --c: #e0a526;
  }

  .challenges {
    margin: 0.25rem 0 1rem;
    padding: 0.5rem 0.75rem;
    border-left: 3px solid var(--c);
    background: color-mix(in srgb, var(--c) 7%, transparent);
  }

  .head {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem 0.6rem;
    align-items: baseline;
    margin: 0 0 0.3rem;
  }

  .chest {
    color: var(--c);
    font-weight: 600;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
  }

  li {
    display: grid;
    grid-template-columns: 1.2rem auto 1fr;
    gap: 0.4rem;
    align-items: baseline;
  }

  .desc {
    min-width: 0;
  }

  li.done .title {
    color: var(--c);
  }

  li.done .mark {
    color: var(--good, #4a4);
  }
</style>
