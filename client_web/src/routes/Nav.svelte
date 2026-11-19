<script lang="ts">
  // Modul: THE DESCENT INTO NAV (2026-11-19, proposal in docs/design/
  // AUDIT_AND_ENDGAME_2026_11_19.md section 4). The one endless number: how
  // deep a character goes below the World Tree's roots before a guardian
  // outlasts the 30-second clock. Every number - guardian strength, Souls,
  // prices, timers - is the server's (NavRules/NavEngine); this screen draws
  // and sends, nothing else.
  //
  // The screen opens on its ONE action (Descend) and its one number (the
  // record); the rules sit in a disclosure at the foot - the screenshot audit
  // of the same day found screens that open on a paragraph of rules.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { navKeys, fetchNav, navDescend, navBuy, navResearch, type NavRun } from '../lib/net/nav';
  import { formatNumber } from '../lib/ui/format';
  import { formatDuration } from '../lib/net/commands';
  import { pushLocalNotice } from '../lib/stores/game';
  import { monsterName, loadContent, type ContentRegistry } from '../lib/net/content';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import Money from '../lib/ui/Money.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { onMount } from 'svelte';

  const client = useQueryClient();
  const nav = createQuery(() => ({ queryKey: navKeys.state, queryFn: fetchNav, refetchInterval: 60_000 }));
  const data = $derived(nav.data ?? null);

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  let slot = $state(0);
  let circle = $state(0);
  let busy = $state(false);
  let lastRun = $state<NavRun | null>(null);

  const shownRun = $derived(lastRun ?? data?.LastRun ?? null);
  const activeCircle = $derived(circle > 0 ? circle : (data?.Circle ?? 1));
  const record = $derived(data?.Records.find((r) => r.Circle === activeCircle)?.Depth ?? 0);

  // Slot 1 is the main character (2026-10-20); the others by their place.
  function slotLabel(s: number): string {
    return s === 0 ? 'Main' : `Slot ${s + 1}`;
  }

  async function refresh() {
    await client.invalidateQueries({ queryKey: navKeys.state });
  }

  async function descend() {
    if (busy) return;
    busy = true;
    try {
      const r = await navDescend(slot, activeCircle);
      if (r?.Result === 'Ran' && r.Run) {
        lastRun = r.Run;
        for (const line of r.Run.Rewards) pushLocalNotice(line, 'info');
      } else {
        pushLocalNotice(
          r?.Result === 'NoCharges' ? 'No descents left - one comes back every 20 minutes.'
            : r?.Result === 'Offline' ? 'Your character must be in the game to descend.'
              : r?.Result === 'NoCharacter' ? 'Nobody stands in that slot.'
                : r?.Result === 'Locked' ? 'Nav opens when you beat Malakor.'
                  : 'That Circle is not open yet.',
          'error',
        );
      }
    } finally {
      busy = false;
      await refresh();
    }
  }

  async function buy(key: string, levels: number) {
    if (busy) return;
    busy = true;
    try {
      const r = await navBuy(key, levels);
      if (r?.Result !== 'Bought') pushLocalNotice(r?.Result === 'Maxed' ? 'That upgrade is at its top.' : 'Not enough Souls.', 'error');
    } finally {
      busy = false;
      await refresh();
    }
  }

  async function research(key: string) {
    if (busy) return;
    busy = true;
    try {
      const r = await navResearch(key);
      if (r?.Result === 'Started') pushLocalNotice('The Volkhv begins the research.', 'info');
      else pushLocalNotice(r?.Result === 'Busy' ? 'The Volkhv is already researching.' : 'Not enough gold.', 'error');
    } finally {
      busy = false;
      await refresh();
    }
  }

  function endedLine(run: NavRun): string {
    if (run.EndedBy === 'fell') return 'A guardian struck you down.';
    if (run.EndedBy === 'time') return 'A guardian outlasted the 30-second clock.';
    return 'The run reached its limit.';
  }
</script>

<section class="panel nav-screen" data-testid="nav">
  <header class="top">
    <div>
      <h2>The Descent into Nav</h2>
      <p class="dim small">Below the World Tree's roots the dark goes on forever. How deep can you go?</p>
    </div>
    {#if data}
      <div class="record" data-testid="nav-record">
        <span class="big">{record}</span>
        <span class="dim tiny">deepest, Circle {activeCircle}</span>
      </div>
    {/if}
  </header>

  {#if nav.isPending}
    <Skeleton rows={4} />
  {:else if nav.isError || !data}
    <p class="dim">Nav is closed.</p>
  {:else}
    <div class="col">
    <div class="descend">
      <div class="pick">
        <span class="dim small">Who descends</span>
        <div class="chips">
          {#each data.Roster as r (r.Slot)}
            <button class="chip" class:on={slot === r.Slot} onclick={() => (slot = r.Slot)} data-testid={`nav-slot-${r.Slot}`}>
              {slotLabel(r.Slot)} <span class="dim tiny">{formatNumber(r.Dps)} dps</span>
            </button>
          {/each}
        </div>
      </div>
      {#if data.Circle > 1}
        <div class="pick">
          <span class="dim small">Circle</span>
          <div class="chips">
            {#each data.Records as c (c.Circle)}
              <button class="chip" class:on={activeCircle === c.Circle} onclick={() => (circle = c.Circle)}>
                {c.Circle} <span class="dim tiny">best {c.Depth}</span>
              </button>
            {/each}
          </div>
        </div>
      {/if}
      {#if !data.Open}
        <p class="locked" data-testid="nav-locked">{data.LockedReason}</p>
      {/if}
      <button class="primary go" disabled={busy || data.Charges <= 0 || !data.Open} onclick={descend} data-testid="nav-descend">
        {busy ? 'Descending...' : 'Descend'}
      </button>
      <p class="dim small charges" data-testid="nav-charges">
        {data.Charges} of {data.ChargeCap} descents
        {#if data.Charges < data.ChargeCap} - next in {formatDuration(data.NextChargeInSeconds)}{/if}
      </p>
    </div>

    <div class="souls">
      <span class="soul-count" data-testid="nav-souls">{formatNumber(data.Souls)}</span>
      <span class="dim small">Souls to spend</span>
    </div>

    {#if shownRun}
      <section class="run" data-testid="nav-run">
        <h3>Last descent: depth {shownRun.DeepestCleared} <span class="dim small">+{formatNumber(shownRun.Souls)} Souls</span></h3>
        <p class="dim small">{endedLine(shownRun)} {shownRun.RevivesUsed > 0 ? `Second breath used ${shownRun.RevivesUsed}x.` : ''}</p>
        <ol class="depths">
          {#each shownRun.Depths.slice(-8) as d (d.Depth)}
            <li class:fail={!d.Cleared}>
              <MonsterPortrait monsterId={d.GuardianId} name={monsterName(registry, d.GuardianId)} size="sm" />
              <span class="dn">Depth {d.Depth}</span>
              {#if d.Elite}<span class="elite">{d.Elite}</span>{/if}
              <span class="dim tiny t">{d.Seconds}s</span>
              <span class="tiny r">{d.Cleared ? `+${d.Souls}` : 'stopped'}</span>
            </li>
          {/each}
        </ol>
      </section>
    {/if}
    </div>

    <div class="col">
    <section class="workshop">
      <h3>Nav Workshop</h3>
      <p class="dim small">Souls buy strength that only counts down here - prices climb 12% a level and never stop.</p>
      <ul class="upgrades">
        {#each data.Workshop as u (u.Key)}
          <li data-testid={`nav-up-${u.Key}`}>
            <div class="u-text">
              <strong>{u.Name}</strong> <span class="dim tiny">lv {u.Level}{u.MaxLevel ? ` / ${u.MaxLevel}` : ''}</span>
              <span class="dim tiny">{u.Effect}</span>
            </div>
            <div class="u-buy">
              <button disabled={busy || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 1)}>
                {u.Maxed ? 'Top' : `+1 - ${formatNumber(u.Cost)}`}
              </button>
              <button class="ghost" disabled={busy || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 100)} title="As many levels as your Souls buy">Max</button>
            </div>
          </li>
        {/each}
      </ul>
    </section>

    <section class="research">
      <h3>The Volkhv's research</h3>
      <p class="dim small">Gold and time for lasting knowledge of the deep. One line at a time.</p>
      {#if data.ResearchActive}
        <p class="small" data-testid="nav-research-active">
          Researching <strong>{data.Research.find((l) => l.Key === data.ResearchActive)?.Name}</strong> - {formatDuration(data.ResearchLeftSeconds)} left
        </p>
      {/if}
      <ul class="upgrades">
        {#each data.Research as l (l.Key)}
          <li>
            <div class="u-text">
              <strong>{l.Name}</strong> <span class="dim tiny">lv {l.Level}</span>
              <span class="dim tiny">{l.Effect} - {formatDuration(l.Seconds)}</span>
            </div>
            <div class="u-buy">
              <button disabled={busy || data.ResearchActive !== ''} onclick={() => research(l.Key)} data-testid={`nav-research-${l.Key}`}>
                <Money amount={l.Gold} />
              </button>
            </div>
          </li>
        {/each}
      </ul>
    </section>
    </div>

    <details class="rules">
      <summary>How Nav works</summary>
      <ul class="small">
        <li>Each depth has one guardian. Kill it within {data.SecondsPerGuardian} seconds or the descent ends; fall and it ends too.</li>
        <li>Guardians grow 10% stronger every depth, so every tenth more damage is about one depth more - forever.</li>
        <li>Every cleared depth pays Souls; deeper pays more. A run costs one descent, and one comes back every 20 minutes, offline too.</li>
        <li>Depth {data.CircleOpensAt} opens the next Circle: five times stronger guardians, three times the Souls. Depths 10, 25 and 50 of each Circle pay a chest the first time.</li>
        <li>Some guardians are elite - Armoured (more health), Swift (attack faster), Brute (hit harder) or Frail (less health).</li>
        <li>The Workshop and the research only count in Nav. Nothing here changes the rest of the game.</li>
      </ul>
    </details>
  {/if}
</section>

<style>
  .nav-screen {
    max-width: 60rem;
    margin: 0 auto;
    display: grid;
    gap: 1rem;
  }

  .top {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 1rem;
  }

  .top h2 {
    margin: 0 0 0.25rem;
  }

  .record {
    display: grid;
    justify-items: center;
    padding: 0.4rem 0.9rem;
    border: 1px solid var(--accent);
    border-radius: var(--radius);
    min-width: 6rem;
  }

  .big {
    font-size: 2rem;
    font-weight: var(--fw-black);
    line-height: 1;
    font-variant-numeric: tabular-nums;
  }

  .col {
    display: grid;
    gap: 1rem;
    align-content: start;
    min-width: 0;
  }

  .descend {
    display: grid;
    gap: 0.6rem;
  }

  .pick {
    display: grid;
    gap: 0.3rem;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .chip {
    min-height: 44px;
  }

  .chip.on {
    border-color: var(--accent);
    box-shadow: inset 0 0 0 1px var(--accent);
  }

  .go {
    min-height: 52px;
    font-size: 1.1rem;
  }

  .charges {
    margin: 0;
  }

  .locked {
    margin: 0;
    padding: 0.5rem 0.7rem;
    border-left: 3px solid var(--accent);
    background: var(--bg-raised);
  }

  .souls {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
  }

  .soul-count {
    font-size: 1.5rem;
    font-weight: var(--fw-bold);
    color: var(--diamond);
    font-variant-numeric: tabular-nums;
  }

  .run h3,
  .workshop h3,
  .research h3 {
    margin: 0 0 0.25rem;
  }

  .depths {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .depths li {
    display: grid;
    grid-template-columns: auto 1fr auto auto auto;
    align-items: center;
    gap: 0.5rem;
    padding: 0.25rem 0.4rem;
    border-bottom: 1px solid var(--line);
  }

  .depths li.fail {
    color: var(--danger);
  }

  .elite {
    font-size: var(--fs-xs);
    padding: 0 0.4rem;
    border: 1px solid var(--warn, #e0b050);
    border-radius: 999px;
  }

  .upgrades {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }

  .upgrades li {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.6rem;
    padding: 0.4rem 0;
    border-bottom: 1px solid var(--line);
    flex-wrap: wrap;
  }

  .u-text {
    display: grid;
    min-width: 0;
    flex: 1 1 12rem;
  }

  .u-buy {
    display: flex;
    gap: 0.35rem;
  }

  .u-buy button {
    min-height: 44px;
  }

  .ghost {
    background: transparent;
  }

  .rules summary {
    cursor: pointer;
    min-height: 44px;
    display: flex;
    align-items: center;
  }

  @media (min-width: 60rem) {
    .nav-screen {
      grid-template-columns: 1fr 1fr;
    }

    .top,
    .rules {
      grid-column: 1 / -1;
    }
  }
</style>
