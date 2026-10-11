<script lang="ts">
  // Modul: EVERY PET, READ FROM THE SERVER (owner, 2026-10-11: "nowhere is
  // there info about pets, their list, nobody even knows we can get a pet").
  // /api/v1/pets is PetRegistry itself - names, art, bonuses and where each
  // comes from - so this page cannot list a pet the game does not have, or
  // miss one it added.
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchPets, petKeys, petSourceLine, PET_SOURCE, type Pet } from '../net/pets';
  import { oneIn } from '../net/seasonalEvent';
  import { spriteUrl } from './spriteUrl';
  import { formatNumber } from './format';

  const pets = createQuery(() => ({ queryKey: petKeys.all, queryFn: fetchPets }));

  const GROUPS: { title: string; sources: number[] }[] = [
    { title: 'Monster pets - found on any kill', sources: [PET_SOURCE.Monster] },
    { title: 'Event pets - from a seasonal event', sources: [PET_SOURCE.Shop, PET_SOURCE.RareDrop, PET_SOURCE.Boss] },
    { title: 'The rebirth pet', sources: [PET_SOURCE.Rebirth] },
  ];

  const groups = $derived(
    GROUPS.map((g) => ({ ...g, pets: (pets.data?.Pets ?? []).filter((p: Pet) => g.sources.includes(p.Source)) })).filter(
      (g) => g.pets.length > 0,
    ),
  );
</script>

<p class="dim small">
  A pet follows one character and adds its bonus to that character - shown on the Character screen's Gear tab, in the
  tile after the tools. Every character has one pet slot, and a second opens on every character after
  {pets.data?.SecondSlotRebirths ?? 5} rebirths. Each pet is owned once and follows one character at a time; putting it on
  someone else moves it. Pets are never lost, not even to a rebirth.
</p>
{#if pets.data}
  <ul class="styled-list">
    <li>
      <strong>Any kill</strong> has a {oneIn(pets.data.MonsterPetChancePerKill)} chance to bring one of the monster pets, at
      random - whatever you fight, online or away.
    </li>
    <li>
      Found a monster pet you already have? It pays <strong>{formatNumber(pets.data.DuplicateDiamonds)} diamonds</strong> by
      mail instead.
    </li>
  </ul>
{/if}

{#if pets.isError}
  <p class="warn">The pet list could not be loaded.</p>
{:else if !pets.data}
  <p class="dim tiny">Loading the pets&hellip;</p>
{:else}
  {#each groups as group (group.title)}
    <h4>{group.title}</h4>
    <div class="pet-grid">
      {#each group.pets as pet (pet.Id)}
        <div class="pet-card" data-testid="wiki-pet">
          <img src={spriteUrl(pet.Art)} alt="" loading="lazy" decoding="async" />
          <div class="what">
            <strong>{pet.Name}</strong>
            <span class="bonus">{pet.Bonuses.join(' · ')}</span>
            <span class="dim tiny">{petSourceLine(pet)}</span>
          </div>
        </div>
      {/each}
    </div>
  {/each}
{/if}

<style>
  .pet-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 14rem), 1fr));
    gap: 0.5rem;
    margin-bottom: 0.6rem;
  }

  .pet-card {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    min-width: 0;
  }

  img {
    flex: 0 0 auto;
    width: 3rem;
    height: 3rem;
    object-fit: contain;
  }

  .what {
    display: grid;
    min-width: 0;
    overflow-wrap: anywhere;
    font-size: 0.85rem;
  }

  .bonus {
    color: var(--accent);
    font-size: 0.8rem;
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }

  .small {
    font-size: 0.88rem;
  }

  .warn {
    color: var(--warn);
  }
</style>
