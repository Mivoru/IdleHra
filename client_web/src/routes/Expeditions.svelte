<script lang="ts">
  // Modul: EXPEDITIONS (2026-11-13, owner approved; docs/design/
  // ENDGAME_CONTENT_2026_11_13.md 4A). A benched grown ancestor goes away for
  // 4, 8 or 12 hours and brings back one loot chest of the account's highest
  // region; a better bloodline rolls better chests. Every number - slots,
  // durations, the upgrade chance - is the server's (ExpeditionEngine); this
  // screen draws and sends, nothing else.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchExpeditions, startExpedition, claimExpedition, type ExpeditionCandidate } from '../lib/net/rest';
  import { formatDuration } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { pushLocalNotice } from '../lib/stores/game';
  import { requestScreen } from '../lib/stores/navigation';
  import { raceName } from '../lib/ui/races';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const client = useQueryClient();
  const board = createQuery(() => ({ queryKey: queryKeys.expeditions, queryFn: fetchExpeditions, refetchInterval: 60_000 }));

  let nowSeconds = $state(Math.floor(connection.serverNowMs() / 1000));
  $effect(() => {
    const t = setInterval(() => (nowSeconds = Math.floor(connection.serverNowMs() / 1000)), 1000);
    return () => clearInterval(t);
  });

  let pickedId = $state('');
  let pickedHours = $state(12);
  let busy = $state(false);

  const data = $derived(board.data ?? null);
  const freeSlots = $derived(data ? data.Slots - data.Active.length : 0);
  const picked = $derived<ExpeditionCandidate | undefined>(data?.Candidates.find((c) => c.CharacterId === pickedId));

  // The strongest free ancestor is chosen once the list arrives, like the
  // breeding lab's hero - the screen opens on an answer, not an empty picker.
  $effect(() => {
    if (pickedId === '' && data && data.Candidates.length > 0) pickedId = data.Candidates[0].CharacterId;
  });

  function nameOf(c: { Name: string; RaceId?: number }): string {
    return c.Name || (c.RaceId !== undefined ? raceName(c.RaceId) : 'An ancestor');
  }

  function chestLine(hours: number, upgradePct: number): string {
    if (hours >= 12) return `a Silver chest, Gold at ${15 + upgradePct}%`;
    if (hours >= 8) return `a Bronze chest, Silver at ${25 + upgradePct}%`;
    return 'a Bronze chest';
  }

  async function send() {
    if (!picked || busy) return;
    busy = true;
    try {
      const r = await startExpedition(picked.CharacterId, pickedHours);
      if (r?.Result === 'Started') {
        pushLocalNotice(`${nameOf(picked)} sets out for ${pickedHours} hours.`, 'info');
        pickedId = '';
      } else {
        pushLocalNotice(
          r?.Result === 'NoSlot' ? 'Every expedition slot is in use.'
            : r?.Result === 'NoGrounds' ? 'Expeditions need the Breeding Grounds.'
            : 'That ancestor cannot set out now.',
          'error',
        );
      }
    } finally {
      busy = false;
      await client.invalidateQueries({ queryKey: queryKeys.expeditions });
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    }
  }

  // Bulk (2026-11-13): an endgame check-in is "claim everything, send
  // everyone" - one tap each, the same calls in a row (claims first, so the
  // slots they free are filled by the send).
  async function claimAll() {
    if (busy || !data) return;
    busy = true;
    let chests = 0;
    try {
      for (const e of data.Active.filter((x) => x.ReturnsAtEpoch <= nowSeconds)) {
        const r = await claimExpedition(e.Id);
        if (r?.Ok) {
          chests += 1;
          if (r.Title) pushLocalNotice(`New title: ${r.Title} - ${r.Claimed} expeditions.`, 'info');
        }
      }
      if (chests > 0) pushLocalNotice(`${chests} ${chests === 1 ? 'chest' : 'chests'} brought back - open them on the Chest screen.`, 'info');
    } finally {
      busy = false;
      await client.invalidateQueries({ queryKey: queryKeys.expeditions });
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    }
  }

  async function sendAll() {
    if (busy || !data) return;
    busy = true;
    let sent = 0;
    try {
      const free = data.Slots - data.Active.length;
      for (const c of data.Candidates.slice(0, Math.max(0, free))) {
        const r = await startExpedition(c.CharacterId, pickedHours);
        if (r?.Result === 'Started') sent += 1;
      }
      if (sent > 0) pushLocalNotice(`${sent} ${sent === 1 ? 'ancestor sets' : 'ancestors set'} out for ${pickedHours} hours.`, 'info');
      pickedId = '';
    } finally {
      busy = false;
      await client.invalidateQueries({ queryKey: queryKeys.expeditions });
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    }
  }

  const returnedCount = $derived(data ? data.Active.filter((e) => e.ReturnsAtEpoch <= nowSeconds).length : 0);

  async function claim(id: number) {
    if (busy) return;
    busy = true;
    try {
      const r = await claimExpedition(id);
      if (r?.Ok) {
        pushLocalNotice(`The expedition brought back a ${r.Tier} chest - open it on the Chest screen.`, 'info');
        if (r.Title) pushLocalNotice(`New title: ${r.Title} - ${r.Claimed} expeditions.`, 'info');
      }
      else pushLocalNotice(r?.Reason || 'Could not claim that expedition.', 'error');
    } finally {
      busy = false;
      await client.invalidateQueries({ queryKey: queryKeys.expeditions });
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    }
  }
</script>

<section class="panel expeditions" data-testid="expeditions">
  <h2>Expeditions</h2>
  <p class="dim small">
    Send a grown ancestor from the bench on an expedition. They come back with a loot chest of region
    {data?.Region ?? '-'} - the longer the trip and the better their aptitudes, the better the chest. While away
    they cannot be fielded or bred.
  </p>

  {#if board.isPending}
    <Skeleton rows={4} />
  {:else if board.isError || !data}
    <p class="warn">Could not load the expeditions.</p>
    <button class="retry" onclick={() => board.refetch()}>Retry</button>
  {:else if data.Slots === 0}
    <p class="warn">Expeditions open with the Breeding Grounds in your village.</p>
  {:else}
    <p class="tiny" data-testid="expedition-tally">
      {data.Claimed ?? 0} expeditions done{#if data.NextTitle}&nbsp;&middot; the title <strong>{data.NextTitle}</strong> at {data.NextTitleAt}{:else}&nbsp;&middot; every expedition title earned{/if}
    </p>
    <h3>Away <span class="dim tiny">{data.Active.length} of {data.Slots} slots</span></h3>
    {#if returnedCount > 1}
      <button type="button" class="claim" data-testid="expedition-claim-all" disabled={busy} onclick={claimAll}>Claim all {returnedCount} chests</button>
    {/if}
    {#if data.Active.length === 0}
      <p class="dim">Nobody is away.</p>
    {:else}
      <ul class="trips">
        {#each data.Active as e (e.Id)}
          {@const left = Math.max(0, e.ReturnsAtEpoch - nowSeconds)}
          <li data-testid="expedition-row" data-character-id={e.CharacterId} data-returned={left === 0}>
            <div class="who">
              <strong>{e.Name || 'An ancestor'}</strong>
              <span class="dim tiny">{e.Hours} h to region {e.Region} &middot; aptitudes {e.AptitudeTotal}</span>
            </div>
            {#if left === 0}
              <button type="button" class="claim" data-testid="expedition-claim" disabled={busy} onclick={() => claim(e.Id)}>Claim the chest</button>
            {:else}
              <span class="tiny">back in {formatDuration(left)}</span>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}

    <h3>Send someone</h3>
    {#if freeSlots <= 0}
      <p class="dim">Every slot is in use. Claim a returned expedition to free one.</p>
    {:else if data.Candidates.length === 0}
      <p class="dim">No grown ancestor is free on the bench. Breed, or bench someone in the Hall.</p>
    {:else}
      <div class="send">
        <!-- Buttons, not a <select>: the list refetches, and Android's native
             select dialog drops its choice when options re-render under it. -->
        <div class="who-pick" role="radiogroup" aria-label="Ancestor" data-testid="expedition-who">
          {#each data.Candidates.slice(0, 6) as c (c.CharacterId)}
            <button type="button" role="radio" aria-checked={pickedId === c.CharacterId} class:on={pickedId === c.CharacterId} onclick={() => (pickedId = c.CharacterId)}>
              <strong>{nameOf(c)}</strong> <span class="dim tiny">aptitudes {c.AptitudeTotal}</span>
            </button>
          {/each}
        </div>
        <div class="hours" role="group" aria-label="How long">
          {#each data.Durations as h (h)}
            <button type="button" class:on={pickedHours === h} aria-pressed={pickedHours === h} onclick={() => (pickedHours = h)}>{h} h</button>
          {/each}
        </div>
        {#if picked}
          <p class="tiny">{nameOf(picked)} brings back {chestLine(pickedHours, picked.UpgradePct)}.</p>
        {/if}
        <button type="button" class="go" data-testid="expedition-send" disabled={!picked || busy} onclick={send}>Send on expedition</button>
        {#if freeSlots > 1 && data.Candidates.length > 1}
          <button type="button" class="go" data-testid="expedition-send-all" disabled={busy} onclick={sendAll}>
            Send the {Math.min(freeSlots, data.Candidates.length)} strongest for {pickedHours} h
          </button>
        {/if}
      </div>
    {/if}
    <p class="dim tiny">
      Chests wait on the <button type="button" class="inline-link" onclick={() => requestScreen('chest')}>Chest screen</button>.
    </p>
  {/if}
</section>

<style>
  .expeditions {
    display: grid;
    gap: 0.5rem;
    max-width: 44rem;
  }

  h3 {
    margin: 0.6rem 0 0.1rem;
    font-size: 1rem;
  }

  .trips {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }

  .trips li {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
    padding: 0.5rem 0.7rem;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
  }

  .who {
    display: grid;
  }

  .send {
    display: grid;
    gap: 0.5rem;
  }

  .who-pick {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 12rem), 1fr));
    gap: 0.4rem;
  }

  .who-pick button {
    min-height: 44px;
    text-align: left;
  }

  .who-pick button.on {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent) inset;
  }

  .hours {
    display: flex;
    gap: 0.4rem;
  }

  .hours button,
  .go,
  .claim {
    min-height: 44px;
  }

  .hours button.on {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent) inset;
  }

  /* A link inside a sentence still needs a thumb-sized target (check:touch,
     390px): 44px tall via inline-flex, with the extra height taken back by
     negative margins so the line of text does not grow. */
  .inline-link {
    display: inline-flex;
    align-items: center;
    min-height: 44px;
    margin: -12px 0;
    padding: 0 2px;
    background: none;
    background-image: none;
    box-shadow: none;
    border: none;
    color: inherit;
    font: inherit;
    font-weight: 600;
    text-decoration: underline;
    cursor: pointer;
  }
</style>
