<script lang="ts">
  // Modul: A PET IS AN EVENT, NOT A TOAST (owner, 2026-10-12: "when a pet
  // drops the player doesn't even know"). A monster pet is a one-in-100,000
  // kill and the Witch a one-in-20,000 pumpkin, and both arrived as one line
  // that said "a pet" without saying which, while a pet found during offline
  // catch-up said nothing at all. This window names it, shows it, says what it
  // gives and how it is found, and offers to put it on a character.
  //
  // THE SERVER DECIDES WHAT IS OWED. player_pets.AnnouncedAtUtc is null until
  // the window is closed, and PendingDuplicates counts monster-pet duplicates
  // since then - so a pet found offline, or already seen on the phone, is shown
  // exactly once on every device. Shop purchases are stored as already shown.
  //
  // No polling of its own: every command result invalidates every query
  // (stores/game.ts processCommandResults), and the drop's own result (67, 69,
  // 71) is one, so the pets query re-reads the moment a drop is saved.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { assignPet, fetchPets, markPetSeen, petKeys, petSourceLine, PET_SOURCE, type Pet, type PetsView } from '../net/pets';
  import { oneIn } from '../net/seasonalEvent';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { workersOf, workerName } from './workers';
  import { spriteUrl } from './spriteUrl';
  import { play } from './audio';
  import Burst from './Burst.svelte';
  import Modal from './Modal.svelte';

  const client = useQueryClient();
  const pets = createQuery(() => ({ queryKey: petKeys.all, queryFn: fetchPets }));

  // Closed this session even if the server did not hear it - a failed POST
  // must not trap the player under a window that reopens on every refetch.
  let closed = $state<Set<string>>(new Set());
  let busy = $state(false);
  let picking = $state(false);
  let slot = $state(0);

  const pet = $derived<Pet | null>(
    (pets.data?.Pets ?? []).find((p) => p.Owned && (p.Unseen || p.PendingDuplicates > 0) && !closed.has(p.Id)) ?? null,
  );
  const duplicate = $derived(pet !== null && !pet.Unseen);
  const secondOpen = $derived(pets.data?.SecondSlotOpen ?? false);
  const activeSlot = $derived(secondOpen ? slot : 0);
  const names = $derived(new Map((pets.data?.Characters ?? []).map((c) => [c.Id, c.Name])));
  const workers = $derived(workersOf($playerState));

  // A new pet is a sound; a duplicate is not worth one.
  let lastShown = '';
  $effect(() => {
    if (!pet || duplicate || pet.Id === lastShown) return;
    lastShown = pet.Id;
    picking = false;
    play('petFound');
  });

  function howFound(p: Pet, view: PetsView): string {
    if (p.Source === PET_SOURCE.Monster) {
      return `Found on any kill, any time: a ${oneIn(view.MonsterPetChancePerKill)} chance per kill, then one of the monster pets at random.`;
    }
    return petSourceLine(p);
  }

  /** What the character already has in the slot the pet would take. */
  function holder(characterId: string): Pet | null {
    return (pets.data?.Pets ?? []).find((p) => p.Owned && p.CharacterId === characterId && (p.Slot ?? 0) === activeSlot) ?? null;
  }

  async function close() {
    if (!pet) return;
    const id = pet.Id;
    closed = new Set([...closed, id]);
    picking = false;
    try {
      const answer = await markPetSeen(id);
      if (answer?.Pets && pets.data) {
        client.setQueryData<PetsView>(petKeys.all, { ...pets.data, Pets: answer.Pets });
      }
    } catch {
      // Shown again at the next sign-in, which is the safe direction.
    }
  }

  async function equip(characterId: string) {
    if (!pet || busy) return;
    busy = true;
    try {
      const answer = await assignPet(pet.Id, characterId, activeSlot);
      if (answer?.Result === 'Ok') {
        play('itemEquipped');
        pushLocalNotice(`${pet.Name} now follows ${names.get(characterId) ?? 'them'}.`, 'info');
        await close();
      } else if (answer?.Result === 'SlotLocked') {
        pushLocalNotice(`The second pet slot opens after ${pets.data?.SecondSlotRebirths ?? 5} rebirths.`, 'error');
      } else {
        pushLocalNotice('That pet could not be placed. Try again in a moment.', 'error');
      }
    } catch {
      pushLocalNotice('That pet could not be placed. Try again in a moment.', 'error');
    } finally {
      busy = false;
      await client.invalidateQueries({ queryKey: petKeys.all });
    }
  }
</script>

{#if pet && pets.data}
  {@const view = pets.data}
  {#key pet.Id}
    <Modal
      label={duplicate ? `${pet.Name} found you again` : `${pet.Name} found you`}
      tone="brass"
      width="24rem"
      onClose={close}
      dismissOnScrim={false}
      class="folk-sweep folk-glow"
      testid="pet-found"
    >
      <div class="pet-found" data-pet={pet.Id} data-duplicate={duplicate}>
        <div class="art">
          {#if !duplicate}<span class="burst"><Burst count={14} reach={4} /></span>{/if}
          <img src={spriteUrl(pet.Art)} alt="" decoding="async" />
        </div>
        <p class="kicker">{duplicate ? 'Found again' : 'A pet has found you'}</p>
        <h2>{pet.Name}</h2>

        <ul class="bonuses" aria-label="What it gives">
          {#each pet.Bonuses as bonus (bonus)}
            <li>{bonus}</li>
          {/each}
        </ul>
        <p class="dim small how">{howFound(pet, view)}</p>

        {#if duplicate}
          <p class="small" data-testid="pet-found-diamonds">
            You already have {pet.Name}, so
            {pet.PendingDuplicates > 1 ? `${pet.PendingDuplicates} × ` : ''}{view.DuplicateDiamonds} diamonds
            are waiting in your mail instead.
          </p>
        {:else if picking}
          {#if secondOpen}
            <div class="slots">
              <button type="button" class="tiny-btn" class:active={activeSlot === 0} onclick={() => (slot = 0)}>Slot 1</button>
              <button type="button" class="tiny-btn" class:active={activeSlot === 1} onclick={() => (slot = 1)}>Slot 2</button>
            </div>
          {/if}
          {#if workers.length === 0}
            <p class="dim small">Nobody stands in a roster slot yet. Place it later from the Pet tile on the Character screen.</p>
          {:else}
            <ul class="who" data-testid="pet-found-characters">
              {#each workers as worker (worker.id)}
                {@const current = holder(worker.id)}
                <li>
                  <button type="button" disabled={busy} onclick={() => equip(worker.id)} data-testid="pet-found-character">
                    <strong>{workerName(worker, names)}</strong>
                    {#if current && current.Id !== pet.Id}
                      <span class="tiny dim">{current.Name} goes back to rest</span>
                    {:else if pet.CharacterId === worker.id}
                      <span class="tiny dim">Already follows them</span>
                    {/if}
                  </button>
                </li>
              {/each}
            </ul>
          {/if}
        {/if}
      </div>

      {#snippet footer()}
        <div class="row">
          <button type="button" onclick={close} data-testid="pet-found-close">Close</button>
          {#if !duplicate && !picking}
            <button type="button" class="primary" onclick={() => (picking = true)} data-testid="pet-found-equip">Equip</button>
          {/if}
        </div>
      {/snippet}
    </Modal>
  {/key}
{/if}

<style>
  .pet-found {
    display: grid;
    gap: 0.5rem;
    justify-items: center;
    text-align: center;
  }

  .art {
    position: relative;
    display: grid;
    place-items: center;
  }

  .burst {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    pointer-events: none;
  }

  img {
    position: relative;
    width: 7rem;
    height: 7rem;
    object-fit: contain;
  }

  .kicker {
    margin: 0;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--accent);
  }

  h2 {
    margin: 0;
    overflow-wrap: anywhere;
  }

  .bonuses {
    list-style: none;
    margin: 0;
    padding: 0;
    color: var(--good);
    font-weight: 600;
  }

  .how {
    margin: 0;
  }

  .slots {
    display: flex;
    gap: 0.4rem;
  }

  .slots .active {
    border-color: var(--accent);
    color: var(--accent);
  }

  .who {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
    width: 100%;
  }

  .who button {
    width: 100%;
    min-height: 44px;
    display: grid;
    justify-items: start;
    text-align: left;
  }

  .row {
    display: flex;
    gap: 0.5rem;
    justify-content: flex-end;
  }

  .row button {
    min-height: 44px;
    min-width: 6rem;
  }
</style>
