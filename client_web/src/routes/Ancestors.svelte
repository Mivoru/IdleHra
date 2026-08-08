<script lang="ts">
  // Modul: THE HALL OF ANCESTORS - the roster that outlives a season.
  //
  // Levels, gear, gold and the village all reset every ninety days. What
  // survives is a handful of people and the aptitudes bred into them, and a cap
  // is what turns that into a choice: without one, a season accumulates every
  // child ever born and its last week is worth as much as its first.
  //
  // Three jobs, and all three were missing:
  //   - field a member (nothing could change a SlotIndex, so a bred child was
  //     unplayable forever),
  //   - mark who carries through the rollover,
  //   - read the pedigree.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchAncestorsHall, type HallMember } from '../lib/net/rest';
  import {
    purchaseAncestorSlot,
    setAncestorKept,
    assignCharacterSlot,
    APTITUDE_MAX,
  } from '../lib/net/commands';
  import { raceName } from '../lib/ui/races';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const client = useQueryClient();
  const hall = createQuery(() => ({ queryKey: queryKeys.ancestorsHall, queryFn: fetchAncestorsHall }));

  const data = $derived(hall.data);
  const members = $derived(data?.Members ?? []);
  const carried = $derived(members.filter((m) => m.WouldCarry).length);

  function refresh() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
      client.invalidateQueries({ queryKey: queryKeys.breedingRoster });
    }, 900);
  }

  function total(m: HallMember): number {
    return m.AptitudeStrength + m.AptitudeSkill + m.AptitudeEndurance + m.AptitudeFortune;
  }

  function mark(m: HallMember) {
    const outcome = setAncestorKept(m.CharacterId, !m.IsKept);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  function field(m: HallMember, slotIndex: number) {
    const outcome = assignCharacterSlot(m.CharacterId, slotIndex);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  function buySlot() {
    const outcome = purchaseAncestorSlot();
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  const short = (id: string) => id.slice(0, 8);

  // The pedigree, as the data actually supports it: who each member came from.
  // A villager parent is not a character and is deliberately not stored, so
  // half a parentage is the honest answer rather than an invented name.
  function parentage(m: HallMember): string {
    const father = m.ParentPaternalId ? short(m.ParentPaternalId) : '';
    const mother = m.ParentMaternalId ? short(m.ParentMaternalId) : '';

    if (!father && !mother) return 'a founder of the line';
    if (father && mother) return `${father} x ${mother}`;
    return `${father || mother} and somebody from the village`;
  }

  // Generations, oldest first. This IS the family tree the spec asks for, laid
  // out as the one thing a bloodline is actually ordered by.
  const generations = $derived(
    [...new Set(members.map((m) => m.GenerationIndex))].sort((a, b) => a - b),
  );
</script>

<div class="wrap">
  <section class="panel">
    <header>
      <div>
        <h2>Hall of Ancestors</h2>
        <p class="dim small">
          A season takes back your levels, your gear and your village. It does
          not take these. When the season turns, only
          <strong>{data?.Cap ?? 10}</strong> of them carry.
        </p>
      </div>
      {#if data}
        <span class="tally" class:full={carried >= data.Cap}>
          {carried} / {data.Cap}
        </span>
      {/if}
    </header>

    {#if hall.isPending}
      <Skeleton rows={4} />
    {:else if hall.isError}
      <p class="warn">The Hall could not be loaded.</p>
    {:else if data}
      {#if data.NextSlotCostDiamonds > 0}
        <div class="buy">
          <button disabled={data.Diamonds < data.NextSlotCostDiamonds} onclick={buySlot}>
            One more slot &middot; {data.NextSlotCostDiamonds.toLocaleString()} diamonds
          </button>
          <p class="dim tiny">
            {data.SlotsPurchased} of {data.MaxCap - (data.Cap - data.SlotsPurchased)} bought.
            Slots survive the season, like everything else diamonds buy.
          </p>
        </div>
      {:else}
        <p class="dim tiny">All four extra slots bought - {data.MaxCap} is the ceiling.</p>
      {/if}

      {#each generations as generation (generation)}
        <h3>{generation === 0 ? 'The founders' : `Generation ${generation}`}</h3>
        <ul>
          {#each members.filter((m) => m.GenerationIndex === generation) as m (m.CharacterId)}
            <li class:doomed={!m.WouldCarry}>
              <RaceIcon raceId={m.RaceId} />

              <div class="who">
                <strong>
                  {raceName(m.RaceId)} {m.IsFemale ? 'woman' : 'man'}
                  {#if m.IsEpicMutation}<span class="epic" title="Epic mutation">&#9733;</span>{/if}
                </strong>
                <span class="dim tiny">
                  lv {m.Level} &middot; {parentage(m)}
                  {#if m.IsInbred} &middot; <span class="risk">inbred</span>{/if}
                </span>
              </div>

              <span class="apts" title="Strength / Skill / Endurance / Fortune">
                <span>{m.AptitudeStrength}</span>
                <span>{m.AptitudeSkill}</span>
                <span>{m.AptitudeEndurance}</span>
                <span>{m.AptitudeFortune}</span>
                <span class="sum">{total(m)} / {APTITUDE_MAX * 4}</span>
              </span>

              <div class="acts">
                <!-- Fielding. The whole point of breeding a child at the end
                     of a season is to begin the next one as them, and until
                     this button existed there was no way to do it. -->
                {#if m.PlayableSlot >= 0}
                  <span class="fielded">slot {m.PlayableSlot + 1}</span>
                {:else}
                  <select
                    aria-label="Field this ancestor"
                    onchange={(e) => {
                      const slot = Number((e.currentTarget as HTMLSelectElement).value);
                      (e.currentTarget as HTMLSelectElement).value = '';
                      if (!Number.isNaN(slot) && slot >= 0) field(m, slot);
                    }}
                  >
                    <option value="">Field...</option>
                    {#each Array(data.PlayableSlots) as _, slot}
                      <option value={slot}>into slot {slot + 1}</option>
                    {/each}
                  </select>
                {/if}

                <!-- The main character's id IS the account's own id, so they
                     can never be the one let go. A toggle that could not be
                     turned off would be a lie, so there is none. -->
                {#if m.IsMainCharacter}
                  <span class="pin" title="Your first character always carries">always</span>
                {:else}
                  <button class:on={m.IsKept} onclick={() => mark(m)}>
                    {m.IsKept ? 'Kept' : 'Keep'}
                  </button>
                {/if}
              </div>
            </li>
          {/each}
        </ul>
      {/each}

      {#if members.length > data.Cap}
        <p class="dim tiny">
          Faded rows are the ones the rollover would let go. Marked members go
          first, then the strongest blood.
        </p>
      {/if}
    {/if}
  </section>
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.6rem;
  }

  h2 {
    margin: 0 0 0.2rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1rem 0 0.35rem;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  header p {
    margin: 0;
    max-width: 52ch;
  }

  .tally {
    flex: none;
    padding: 0.15rem 0.5rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    color: var(--brass-lit);
    font-variant-numeric: tabular-nums;
  }

  .tally.full {
    border-color: var(--warn);
    color: var(--warn);
  }

  .buy {
    display: grid;
    gap: 0.2rem;
    margin-top: 0.6rem;
  }

  .buy button,
  .acts button {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.3rem 0.5rem;
    cursor: pointer;
  }

  .buy button {
    border-color: var(--brass);
  }

  .buy button:disabled {
    opacity: 0.5;
    border-color: var(--border);
    cursor: default;
  }

  .acts button.on {
    border-color: var(--brass);
    color: var(--brass-lit);
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    min-width: 0;
  }

  /* Whoever the rollover would let go, shown before it happens rather than
     after. A cap nobody can see is a surprise, not a decision. */
  li.doomed {
    opacity: 0.45;
    border-style: dashed;
  }

  .who {
    display: grid;
    gap: 0.05rem;
    min-width: 0;
  }

  .epic {
    color: var(--brass-lit);
  }

  .risk {
    color: var(--danger);
  }

  .apts {
    display: flex;
    gap: 0.25rem;
    margin-left: auto;
    font-variant-numeric: tabular-nums;
    font-size: 0.85rem;
  }

  .apts span {
    min-width: 1.5rem;
    padding: 0.05rem 0.2rem;
    text-align: center;
    border-radius: 3px;
    background: var(--bg);
    color: var(--brass-lit);
  }

  .apts .sum {
    min-width: 4rem;
    color: var(--text-dim);
  }

  .acts {
    display: flex;
    align-items: center;
    gap: 0.3rem;
    flex: none;
  }

  .acts select {
    font: inherit;
    font-size: 0.8rem;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.25rem;
  }

  .fielded,
  .pin {
    font-size: 0.72rem;
    color: var(--brass-lit);
    padding: 0.15rem 0.35rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
  }

  .pin {
    color: var(--text-dim);
    border-color: var(--border);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
    margin: 0.3rem 0 0;
  }

  .warn {
    color: var(--warn);
  }
</style>
