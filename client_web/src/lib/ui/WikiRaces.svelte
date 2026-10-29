<script lang="ts">
  // Modul: WHAT EACH RACE DOES, READ FROM THE SERVER (owner, 2026-10-29: "it is
  // described nowhere, that is wrong"). /api/v1/races is built from the
  // constants the fight reads (RaceBonusRegistry), so this page can neither
  // promise a bonus the game does not pay nor miss one it does.
  import { createQuery } from '@tanstack/svelte-query';
  import { loadRaceDescriptions, masteryScope, raceKeys } from '../net/races';
  import RaceIcon from './RaceIcon.svelte';

  const races = createQuery(() => ({ queryKey: raceKeys.descriptions, queryFn: loadRaceDescriptions, staleTime: Infinity }));
</script>

<p class="dim small">
  A character's race is set at birth and passed on by breeding. Its <strong>own bonuses</strong> always apply to a
  character of that race. <strong>Race mastery</strong> grows as you fight with characters of that race, and pays more at
  set levels - see the Progression screen's race mastery panel for your levels.
</p>

{#if races.isError}
  <p class="warn">The race list could not be loaded.</p>
{:else if !races.data}
  <p class="dim tiny">Loading the races&hellip;</p>
{:else}
  <div class="race-grid">
    {#each races.data as race (race.RaceId)}
      <div class="race-card" data-testid="wiki-race">
        <div class="head">
          <RaceIcon raceId={race.RaceId} size="sm" />
          <strong>{race.Name}</strong>
        </div>
        <ul class="lines">
          {#each race.Innate as line (line)}
            <li>{line}</li>
          {/each}
        </ul>
        {#if race.Mastery.length > 0}
          <span class="dim tiny label">Race mastery</span>
          <ul class="lines">
            {#each race.Mastery as line (line.Level)}
              <li><span class="lvl">Level {line.Level}</span> {line.Text}</li>
            {/each}
          </ul>
          <span class="dim tiny">{masteryScope(race)}</span>
        {/if}
      </div>
    {/each}
  </div>
{/if}

<style>
  .race-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 17rem), 1fr));
    gap: 0.5rem;
    margin-bottom: 0.6rem;
  }

  .race-card {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    padding: 0.55rem 0.7rem;
    border: 1px solid var(--border);
    border-radius: 0.5rem;
    min-width: 0;
  }

  .head {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  .lines {
    margin: 0;
    padding-left: 1.1rem;
    overflow-wrap: anywhere;
  }

  .label {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-top: 0.15rem;
  }

  .lvl {
    font-weight: 600;
    color: var(--accent);
  }
</style>
