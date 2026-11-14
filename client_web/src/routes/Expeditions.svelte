<script lang="ts">
  // Modul: EXPEDITIONS (2026-11-13, owner approved; docs/design/
  // ENDGAME_CONTENT_2026_11_13.md 4A). A benched grown ancestor goes away for
  // 4, 8 or 12 hours and brings back one loot chest of the account's highest
  // region; a better bloodline rolls better chests. Every number - slots,
  // durations, the upgrade chance - is the server's (ExpeditionEngine); this
  // screen draws and sends, nothing else.
  import ScreenBanner from '../lib/ui/ScreenBanner.svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchExpeditions, startExpedition, claimExpedition, buyExpeditionSlot, type ExpeditionCandidate } from '../lib/net/rest';
  import ConfirmButton from '../lib/ui/ConfirmButton.svelte';
  import { formatNumber } from '../lib/ui/format';
  import { EXPEDITION_GOLD_AT_12H_PCT, EXPEDITION_SILVER_AT_8H_PCT } from '../lib/net/commands';
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
  // Remembered per browser (2026-11-14): an endgame player sends the same
  // trip every check-in. A convenience only - unreadable storage just falls
  // back to 12 h, unprovisioned.
  const HOURS_KEY = 'folkidle.expedition.hours';
  const PROVISION_KEY = 'folkidle.expedition.provision';
  function readStored(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }
  let pickedHours = $state([4, 8, 12].includes(Number(readStored(HOURS_KEY))) ? Number(readStored(HOURS_KEY)) : 12);
  let busy = $state(false);
  // Provisions (2026-11-13): gold for a better haul, priced by the server in
  // hours of this player's own income; the toggle stays as set between sends.
  let provision = $state(readStored(PROVISION_KEY) === '1');
  $effect(() => {
    try {
      localStorage.setItem(HOURS_KEY, String(pickedHours));
      localStorage.setItem(PROVISION_KEY, provision ? '1' : '0');
    } catch {
      // private window or blocked storage: the choice simply is not kept
    }
  });

  const data = $derived(board.data ?? null);
  const freeSlots = $derived(data ? data.Slots - data.Active.length : 0);
  const picked = $derived<ExpeditionCandidate | undefined>(data?.Candidates.find((c) => c.CharacterId === pickedId));
  const provisionCost = $derived(data?.ProvisionCosts?.[data.Durations.indexOf(pickedHours)] ?? 0);
  const provisionPct = $derived(data?.ProvisionUpgradePct ?? 0);

  // The strongest free ancestor is chosen once the list arrives, like the
  // breeding lab's hero - the screen opens on an answer, not an empty picker.
  $effect(() => {
    if (pickedId === '' && data && data.Candidates.length > 0) pickedId = data.Candidates[0].CharacterId;
  });

  function nameOf(c: { Name: string; RaceId?: number }): string {
    return c.Name || (c.RaceId !== undefined ? raceName(c.RaceId) : 'An ancestor');
  }

  // Mirrors ExpeditionRules.ChestFor for the sentence only; the roll is the server's.
  function chestLine(hours: number, upgradePct: number, provisioned: boolean): string {
    const extra = provisioned ? provisionPct : 0;
    if (hours >= 12) return `a Silver chest, Gold at ${EXPEDITION_GOLD_AT_12H_PCT + upgradePct + extra}%`;
    if (hours >= 8) return `a Bronze chest, Silver at ${EXPEDITION_SILVER_AT_8H_PCT + upgradePct + extra}%`;
    return provisioned ? `a Bronze chest, Silver at ${provisionPct}%` : 'a Bronze chest';
  }

  function refusal(result: string | undefined): string {
    return result === 'NoSlot' ? 'Every expedition slot is in use.'
      : result === 'NoGrounds' ? 'Expeditions need the Breeding Grounds.'
      : result === 'NotEnoughGold' ? 'Not enough gold for the provisions.'
      : 'That ancestor cannot set out now.';
  }

  async function send() {
    if (!picked || busy) return;
    busy = true;
    try {
      const r = await startExpedition(picked.CharacterId, pickedHours, provision);
      if (r?.Result === 'Started') {
        pushLocalNotice(`${nameOf(picked)} sets out for ${pickedHours} hours${provision ? ', well provisioned' : ''}.`, 'info');
        pickedId = '';
      } else {
        pushLocalNotice(refusal(r?.Result), 'error');
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
        const r = await startExpedition(c.CharacterId, pickedHours, provision);
        if (r?.Result === 'Started') sent += 1;
        else if (r?.Result === 'NotEnoughGold') {
          pushLocalNotice(refusal(r.Result), 'error');
          break;
        }
      }
      if (sent > 0) pushLocalNotice(`${sent} ${sent === 1 ? 'ancestor sets' : 'ancestors set'} out for ${pickedHours} hours.`, 'info');
      pickedId = '';
    } finally {
      busy = false;
      await client.invalidateQueries({ queryKey: queryKeys.expeditions });
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    }
  }

  // The whole endgame check-in in one tap (2026-11-13): bring everyone back,
  // then send the strongest free ancestors out again for the chosen length
  // (and provisions, if the toggle is on). The same two calls in a row;
  // sendAll reads the board that claimAll's refetch just brought.
  async function claimAndResend() {
    await claimAll();
    await sendAll();
  }

  // The wall-clock time of a return, in the player's own locale - "back in
  // 7 h 12 min" answers "how long", this answers "will it be back before I go
  // to bed". A day name is added when the return is not today.
  function returnClock(epoch: number): string {
    const at = new Date(epoch * 1000);
    const time = at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return at.toDateString() === new Date().toDateString() ? time : `${at.toLocaleDateString([], { weekday: 'short' })} ${time}`;
  }

  const returnedCount = $derived(data ? data.Active.filter((e) => e.ReturnsAtEpoch <= nowSeconds).length : 0);

  async function buySlot() {
    const r = await buyExpeditionSlot();
    if (r?.Result === 'Bought') pushLocalNotice('A new expedition slot is yours.', 'info');
    else pushLocalNotice(r?.Result === 'NotEnoughGold' ? 'Not enough gold for that slot.' : 'No slot to buy.', 'error');
    await client.invalidateQueries({ queryKey: queryKeys.expeditions });
  }

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

<div class="exp-wrap">
<ScreenBanner art="banner_expeditions" line="Beyond the bench, the roads are long." />
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
    {#if (data.NextSlotCost ?? 0) > 0}
      <div data-testid="expedition-buy-slot">
        <ConfirmButton danger={false} label={`Add a slot - ${formatNumber(data.NextSlotCost ?? 0)} gold`} onConfirm={buySlot} />
        <span class="dim tiny">About {data.NextSlotHours} hours of your income.</span>
      </div>
    {/if}
    {#if returnedCount > 1}
      <button type="button" class="claim" data-testid="expedition-claim-all" disabled={busy} onclick={claimAll}>Claim all {returnedCount} chests</button>
    {/if}
    {#if returnedCount > 0 && data.Candidates.length > 0}
      <button type="button" class="claim primary" data-testid="expedition-claim-resend" disabled={busy} onclick={claimAndResend}>
        Claim {returnedCount === 1 ? 'the chest' : `all ${returnedCount}`} and send the strongest again ({pickedHours} h{provision && provisionCost > 0 ? ', provisioned' : ''})
      </button>
    {/if}
    {#if data.Active.length === 0}
      <p class="dim">Nobody is away.</p>
    {:else}
      <ul class="trips">
        {#each data.Active as e (e.Id)}
          {@const left = Math.max(0, e.ReturnsAtEpoch - nowSeconds)}
          <li data-testid="expedition-row" data-character-id={e.CharacterId} data-returned={left === 0} class:returned={left === 0}>
            <div class="who">
              <strong>{e.Name || 'An ancestor'}</strong>
              <span class="dim tiny">{e.Hours} h to region {e.Region} &middot; aptitudes {e.AptitudeTotal}{e.Provisioned ? ' · provisioned' : ''}</span>
            </div>
            {#if left === 0}
              <button type="button" class="claim primary" data-testid="expedition-claim" disabled={busy} onclick={() => claim(e.Id)}>Claim the chest</button>
            {:else}
              <span class="tiny">back in {formatDuration(left)} <span class="dim">&middot; at {returnClock(e.ReturnsAtEpoch)}</span></span>
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
        {#if provisionCost > 0}
          <button type="button" class="provision" class:on={provision} aria-pressed={provision} data-testid="expedition-provision" onclick={() => (provision = !provision)}>
            {provision ? 'Provisioned' : 'Provision the party'} - {formatNumber(provisionCost)} gold each
            <span class="dim tiny">+{provisionPct}% toward the better chest, about {Math.max(1, Math.floor(pickedHours / 2))} h of your income</span>
          </button>
        {/if}
        {#if picked}
          <p class="tiny">{nameOf(picked)} brings back {chestLine(pickedHours, picked.UpgradePct, provision && provisionCost > 0)}.</p>
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
</div>

<style>
  /* Centred on a wide screen (owner, 2026-11-14: it sat against the left edge). */
  .exp-wrap {
    max-width: 44rem;
    margin-inline: auto;
  }

  .expeditions {
    display: grid;
    gap: 0.5rem;
    max-width: 44rem;
    margin-inline: auto;
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

  .provision {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.1rem;
    min-height: 44px;
    text-align: left;
  }

  .provision.on {
    border-color: var(--section);
    box-shadow: inset 0 0 0 1px var(--section);
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

  /* A returned trip is the one thing on this screen waiting for a tap. */
  .trips li.returned {
    border-color: var(--accent);
    background: color-mix(in srgb, var(--accent) 10%, transparent);
  }
</style>
