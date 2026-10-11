<script lang="ts">
  // The pet picker, opened by the Pet tile in the Gear grid (Character.svelte):
  // who follows this character, and the owned pets to choose from. One pet per
  // character and each owned once, so choosing a pet that follows someone else
  // moves it here - the row says so before the tap. Bonuses are the server's
  // words (PetRegistry). With no pet owned it says where pets come from.
  //
  // Task 114: five rebirths open a SECOND slot on every character. The two
  // slot buttons pick which slot the choices fill; the server refuses slot 2
  // before then (SlotLocked), so the buttons only appear once it is open.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { assignPet, fetchPets, petKeys, PET_SOURCE, type Pet } from '../net/pets';
  import { EVENT_PHASE, oneIn } from '../net/seasonalEvent';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { requestScreen } from '../stores/navigation';
  import { spriteUrl } from './spriteUrl';
  import QueryError from './QueryError.svelte';

  interface Props {
    characterId: string | null;
    onClose: () => void;
  }

  const { characterId, onClose }: Props = $props();

  const client = useQueryClient();
  const pets = createQuery(() => ({ queryKey: petKeys.all, queryFn: fetchPets }));

  let busy = $state(false);
  let slot = $state(0);

  const owned = $derived((pets.data?.Pets ?? []).filter((p) => p.Owned));
  const secondOpen = $derived(pets.data?.SecondSlotOpen ?? false);
  const activeSlot = $derived(secondOpen ? slot : 0);
  const mine = $derived(
    characterId ? (owned.find((p) => p.CharacterId === characterId && (p.Slot ?? 0) === activeSlot) ?? null) : null,
  );
  const others = $derived(owned.filter((p) => p.Id !== mine?.Id));
  const monsterCount = $derived((pets.data?.Pets ?? []).filter((p) => p.Source === PET_SOURCE.Monster).length);
  const nameOf = (id: string | null) => pets.data?.Characters.find((c) => c.Id === id)?.Name ?? 'someone';
  // The shop sells while the event runs and through its grace days.
  const shopOpen = $derived(Number($playerState?.SeasonalEventPhase ?? 0) !== EVENT_PHASE.None);

  async function place(pet: Pet | null) {
    if (!characterId || busy) return;
    busy = true;
    try {
      const target = pet ?? mine;
      if (!target) return;
      const answer = await assignPet(target.Id, pet ? characterId : null, activeSlot);
      if (answer?.Result === 'SlotLocked') {
        pushLocalNotice(`The second pet slot opens after ${pets.data?.SecondSlotRebirths ?? 5} rebirths.`, 'error');
      } else if (!answer || answer.Result !== 'Ok') {
        pushLocalNotice('That pet could not be placed. Try again in a moment.', 'error');
      }
      await client.invalidateQueries({ queryKey: petKeys.all });
    } catch {
      pushLocalNotice('That pet could not be placed. Try again in a moment.', 'error');
    } finally {
      busy = false;
    }
  }
</script>

<div class="picker" data-testid="pet-panel">
  <header>
    <strong>Pet</strong>
    <button class="tiny-btn" onclick={onClose}>Close</button>
  </header>

  {#if pets.isError}
    <QueryError query={pets} what="your pets" />
  {:else if pets.data}
    {#if secondOpen}
      <div class="slots" data-testid="pet-slots">
        <button type="button" class="tiny-btn" class:active={activeSlot === 0} onclick={() => (slot = 0)}>Slot 1</button>
        <button type="button" class="tiny-btn" class:active={activeSlot === 1} onclick={() => (slot = 1)}>Slot 2</button>
      </div>
    {:else if owned.length > 0}
      <p class="dim tiny" data-testid="pet-second-slot-hint">
        A second pet slot opens after {pets.data.SecondSlotRebirths} rebirths.
      </p>
    {/if}
    {#if mine}
      <div class="current" data-testid="pet-current" data-pet={mine.Id}>
        <img src={spriteUrl(mine.Art)} alt="" decoding="async" />
        <div class="what">
          <strong>{mine.Name}</strong>
          <span class="tiny bonus">{mine.Bonuses.join(' · ')}</span>
        </div>
        <button type="button" class="tiny-btn" disabled={busy} onclick={() => place(null)} data-testid="pet-rest">
          Let rest
        </button>
      </div>
    {/if}

    {#if others.length > 0}
      <ul class="choices" data-testid="pet-choices">
        {#each others as pet (pet.Id)}
          <li>
            <button type="button" class="choice" disabled={busy} onclick={() => place(pet)} data-testid="pet-choice" data-pet={pet.Id}>
              <img src={spriteUrl(pet.Art)} alt="" loading="lazy" decoding="async" />
              <span class="what">
                <strong>{pet.Name}</strong>
                <span class="tiny bonus">{pet.Bonuses.join(' · ')}</span>
                {#if pet.CharacterId === characterId}
                  <span class="tiny dim">In this character's other slot - moves here</span>
                {:else if pet.CharacterId}
                  <span class="tiny dim">Follows {nameOf(pet.CharacterId)} - moves here</span>
                {/if}
              </span>
            </button>
          </li>
        {/each}
      </ul>
    {:else if owned.length === 0}
      <p class="dim tiny" data-testid="pet-none">
        No pet yet. Any kill has a {oneIn(pets.data.MonsterPetChancePerKill)} chance to bring one of
        {monsterCount} pets, and seasonal events sell and hide more. Each follows one character, gives it a bonus,
        and stays yours for good.
      </p>
      {#if shopOpen}
        <button type="button" class="tiny-btn" onclick={() => requestScreen('event')} data-testid="pet-shop">
          Open the event shop
        </button>
      {/if}
    {/if}
  {:else}
    <p class="dim tiny">Loading...</p>
  {/if}
</div>

<style>
  .picker {
    display: grid;
    gap: 0.4rem;
    margin-top: 0.7rem;
    padding: 0.6rem;
    border-radius: var(--radius-sm);
    border: 1px solid var(--border);
    background: var(--bg);
  }

  .slots {
    display: flex;
    gap: 0.4rem;
  }

  .slots button {
    flex-shrink: 0;
  }

  .slots .active {
    border-color: var(--accent);
    color: var(--accent);
  }

  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }

  header button,
  .current button {
    flex-shrink: 0;
  }

  .current,
  .choice {
    display: flex;
    align-items: center;
    gap: 0.6rem;
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

  .picker > .tiny-btn {
    justify-self: start;
  }
</style>
