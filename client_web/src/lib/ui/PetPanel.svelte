<script lang="ts">
  // The character's pet, on the Gear tab: who follows this character, and
  // the owned pets to choose from. One pet per character and each owned once,
  // so choosing a pet that follows someone else moves it here - the row says
  // so before the tap. Bonuses are the server's words (PetRegistry).
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { assignPet, fetchPets, petKeys, type Pet } from '../net/pets';
  import { pushLocalNotice } from '../stores/game';
  import { spriteUrl } from './spriteUrl';
  import QueryError from './QueryError.svelte';

  interface Props {
    characterId: string | null;
  }

  const { characterId }: Props = $props();

  const client = useQueryClient();
  const pets = createQuery(() => ({ queryKey: petKeys.all, queryFn: fetchPets }));

  let busy = $state(false);
  let choosing = $state(false);

  const all = $derived(pets.data?.Pets ?? []);
  const owned = $derived(all.filter((p) => p.Owned));
  const mine = $derived(characterId ? (owned.find((p) => p.CharacterId === characterId) ?? null) : null);
  const nameOf = (id: string | null) => pets.data?.Characters.find((c) => c.Id === id)?.Name ?? 'someone';

  async function place(pet: Pet | null) {
    if (!characterId || busy) return;
    busy = true;
    try {
      const target = pet ?? mine;
      if (!target) return;
      const answer = await assignPet(target.Id, pet ? characterId : null);
      if (!answer || answer.Result !== 'Ok') {
        pushLocalNotice('That pet could not be placed. Try again in a moment.', 'error');
      } else {
        choosing = false;
      }
      await client.invalidateQueries({ queryKey: petKeys.all });
    } catch {
      pushLocalNotice('That pet could not be placed. Try again in a moment.', 'error');
    } finally {
      busy = false;
    }
  }
</script>

{#if pets.isError}
  <QueryError query={pets} what="your pets" />
{:else if owned.length > 0 && characterId}
  <div class="pet" data-testid="pet-panel">
    <h3 class="tiny">Pet</h3>
    {#if mine}
      <div class="current" data-testid="pet-current" data-pet={mine.Id}>
        <img src={spriteUrl(mine.Art)} alt="" decoding="async" />
        <div class="what">
          <strong>{mine.Name}</strong>
          <span class="tiny bonus">{mine.Bonuses.join(' · ')}</span>
        </div>
        <button type="button" class="tiny-btn" disabled={busy} onclick={() => (choosing = !choosing)} data-testid="pet-change">
          {choosing ? 'Close' : 'Change'}
        </button>
      </div>
    {:else}
      <div class="current empty">
        <span class="dim tiny">No pet follows this character.</span>
        <button type="button" class="tiny-btn" disabled={busy} onclick={() => (choosing = !choosing)} data-testid="pet-change">
          {choosing ? 'Close' : 'Choose a pet'}
        </button>
      </div>
    {/if}

    {#if choosing}
      <ul class="choices" data-testid="pet-choices">
        {#each owned.filter((p) => p.Id !== mine?.Id) as pet (pet.Id)}
          <li>
            <button type="button" class="choice" disabled={busy} onclick={() => place(pet)} data-testid="pet-choice" data-pet={pet.Id}>
              <img src={spriteUrl(pet.Art)} alt="" loading="lazy" decoding="async" />
              <span class="what">
                <strong>{pet.Name}</strong>
                <span class="tiny bonus">{pet.Bonuses.join(' · ')}</span>
                {#if pet.CharacterId}
                  <span class="tiny dim">Follows {nameOf(pet.CharacterId)} - moves here</span>
                {/if}
              </span>
            </button>
          </li>
        {/each}
        {#if mine}
          <li>
            <button type="button" class="choice rest" disabled={busy} onclick={() => place(null)} data-testid="pet-rest">
              Let {mine.Name} rest
            </button>
          </li>
        {/if}
      </ul>
    {/if}
  </div>
{:else if all.length > 0 && characterId}
  <p class="dim tiny" data-testid="pet-none">No pet yet. Pets come from seasonal events - each gives the character it follows a bonus.</p>
{/if}

<style>
  .pet {
    display: grid;
    gap: 0.4rem;
    margin-top: 0.6rem;
  }

  h3 {
    margin: 0;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .current,
  .choice {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .current.empty {
    justify-content: space-between;
  }

  img {
    width: 3rem;
    height: 3rem;
    object-fit: contain;
    flex: none;
  }

  .what {
    display: grid;
    min-width: 0;
    flex: 1;
    text-align: left;
  }

  .bonus {
    color: var(--good);
  }

  .choices {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .choice {
    width: 100%;
    padding: 0.3rem 0.5rem;
  }

  .rest {
    justify-content: center;
  }

  .current button {
    flex-shrink: 0;
  }
</style>
