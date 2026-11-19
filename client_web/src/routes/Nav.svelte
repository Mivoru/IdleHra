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
  import { navKeys, fetchNav, navDescend, navBuy, navResearch, guardianArt, type NavRun, type NavGuardian, type NavState } from '../lib/net/nav';
  import { formatNumber } from '../lib/ui/format';
  import { formatDuration } from '../lib/net/commands';
  import { pushLocalNotice } from '../lib/stores/game';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import ScreenBanner from '../lib/ui/ScreenBanner.svelte';
  import Money from '../lib/ui/Money.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { play } from '../lib/ui/audio';
  import { onDestroy } from 'svelte';
  import { noteNavView } from '../lib/stores/navCharges';

  const client = useQueryClient();
  const nav = createQuery(() => ({ queryKey: navKeys.state, queryFn: fetchNav, refetchInterval: 60_000 }));
  const data = $derived(nav.data ?? null);
  // The menu dot follows what this screen shows (navCharges.ts).
  $effect(() => noteNavView(nav.data));

  // Who stands at a depth: the server sends the order, this only indexes it
  // (NavRules.GuardianFor - the warden every WardenEvery-th depth, else the
  // ten in turn).
  function guardianAt(state: NavState, depth: number): NavGuardian | undefined {
    const ten = state.Guardians.slice(0, -1);
    if (ten.length === 0) return undefined;
    if (state.WardenEvery > 0 && depth % state.WardenEvery === 0) return state.Guardians[state.Guardians.length - 1];
    return ten[(Math.max(1, depth) - 1) % ten.length];
  }

  function guardianByKey(state: NavState, key: string): NavGuardian | undefined {
    return state.Guardians.find((g) => g.Key === key);
  }

  // The next guardians below the record: what the next descent will meet.
  const ahead = $derived(
    data ? Array.from({ length: 4 }, (_, i) => record + 1 + i).map((depth) => ({ depth, g: guardianAt(data, depth) })) : [],
  );

  let slot = $state(0);
  let circle = $state(0);
  let busy = $state(false);
  let lastRun = $state<NavRun | null>(null);

  const shownRun = $derived(lastRun ?? data?.LastRun ?? null);

  // Modul: THE DESCENT IS PLAYED BACK, NOT JUST LISTED (2026-11-19). The
  // server decides a run in one request; dropping the finished list on the
  // screen made the mode's one moment - going down - a table. The depths are
  // revealed one by one instead: the guardian, its clock filling towards 30
  // seconds, the Souls adding up. The whole replay is held to about six
  // seconds whatever the depth, can be skipped, and a player who asked for
  // reduced motion gets the result at once. Nothing here decides anything:
  // it is the server's list, shown at a pace.
  let replayIndex = $state(-1);
  let replayTimer: ReturnType<typeof setTimeout> | null = null;
  const replaying = $derived(lastRun !== null && replayIndex >= 0 && replayIndex < lastRun.Depths.length);
  const replayDepth = $derived(replaying && lastRun ? lastRun.Depths[replayIndex] : null);
  const replaySouls = $derived(
    replaying && lastRun ? lastRun.Depths.slice(0, replayIndex + 1).reduce((sum, d) => sum + d.Souls, 0) : 0,
  );

  function reducedMotion(): boolean {
    return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
  }

  function stopReplay() {
    if (replayTimer) clearTimeout(replayTimer);
    replayTimer = null;
    replayIndex = -1;
  }

  function finishReplay(run: NavRun) {
    stopReplay();
    play(run.Rewards.some((line) => line.startsWith('New record')) ? 'navRecord' : 'navDescend');
    for (const line of run.Rewards) pushLocalNotice(line, 'info');
  }

  function startReplay(run: NavRun) {
    stopReplay();
    if (run.Depths.length === 0 || reducedMotion()) {
      finishReplay(run);
      return;
    }
    play('navDescend');
    const step = Math.max(160, Math.min(450, 6000 / run.Depths.length));
    replayIndex = 0;
    const next = () => {
      if (replayIndex >= run.Depths.length - 1) {
        // Hold the last guardian - the one that stopped the run - a beat longer.
        replayTimer = setTimeout(() => finishReplay(run), 1100);
        return;
      }
      replayIndex += 1;
      replayTimer = setTimeout(next, step);
    };
    replayTimer = setTimeout(next, step);
  }

  onDestroy(stopReplay);
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
        startReplay(r.Run);
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
      if (r?.Result !== 'Bought') pushLocalNotice(r?.Result === 'Maxed' ? 'That upgrade is at its top.' : r?.Result === 'Locked' ? 'Nav opens when you beat Malakor.' : 'Not enough Souls.', 'error');
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
      else pushLocalNotice(r?.Result === 'Busy' ? 'The Volkhv is already researching.' : r?.Result === 'Locked' ? 'Nav opens when you beat Malakor.' : 'Not enough gold.', 'error');
    } finally {
      busy = false;
      await refresh();
    }
  }

  function endedLine(state: NavState, run: NavRun): string {
    const who = guardianByKey(state, run.StoppedBy)?.Subject ?? 'A guardian';
    if (run.EndedBy === 'fell') return `${who} struck you down at depth ${run.DeepestCleared + 1}.`;
    if (run.EndedBy === 'time') return `${who} outlasted the ${state.SecondsPerGuardian}-second clock at depth ${run.DeepestCleared + 1}.`;
    return 'The run reached its limit.';
  }

  // Where each of the ten stands: depths n, n+10, n+20...
  function depthsOf(state: NavState, g: NavGuardian): string {
    if (g.Key === 'veles') return `every ${state.WardenEvery}th depth`;
    const n = state.Guardians.indexOf(g) + 1;
    return `depths ${n}, ${n + 10}, ${n + 20}...`;
  }
</script>

<div class="nav-wrap">
<ScreenBanner art="banner_nav" line="Below the roots, the dark goes on forever." />
<section class="panel nav-screen" data-testid="nav">
  <header class="top">
    <div>
      <h2>The Descent into Nav</h2>
      <p class="dim small">One guardian a depth, thirty seconds each. How deep can you go?</p>
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
              {slotLabel(r.Slot)}
              {#if data.Open}
                <span class="dim tiny" title={`Expected to reach about depth ${r.ExpectedDepth} of Circle ${r.ExpectedCircle}`}>~depth {r.ExpectedDepth}</span>
              {:else}
                <span class="dim tiny">{formatNumber(r.Dps)} dps</span>
              {/if}
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
      <button class="primary go" disabled={busy || replaying || data.Charges <= 0 || !data.Open} onclick={descend} data-testid="nav-descend">
        {busy ? 'Descending...' : replaying ? 'Going down...' : 'Descend'}
      </button>
      <p class="dim small charges" data-testid="nav-charges">
        {data.Charges} of {data.ChargeCap} descents
        {#if data.Charges < data.ChargeCap} - next in {formatDuration(data.NextChargeInSeconds)}{/if}
      </p>
    </div>

    {#if replaying && replayDepth && lastRun}
      {@const g = guardianByKey(data, replayDepth.Guardian)}
      <section class="stage" class:fail={!replayDepth.Cleared} class:warden={replayDepth.Guardian === 'veles'} data-testid="nav-replay">
        {#key replayDepth.Depth}
          <div class="stage-card">
            <MonsterPortrait monsterId={0} name={g?.Name ?? 'Guardian'} src={guardianArt(replayDepth.Guardian)} size="lg" />
            <div class="stage-text">
              <span class="stage-depth">Depth {replayDepth.Depth}</span>
              <strong>{g?.Name ?? ''}</strong>
              {#if replayDepth.Elite}<span class="elite">{replayDepth.Elite}</span>{/if}
              <span class="tiny ability">{g?.AbilityName ?? ''}</span>
              <div class="clock" aria-hidden="true">
                <span style={`--fill:${Math.min(100, (replayDepth.Seconds / data.SecondsPerGuardian) * 100)}%`}></span>
              </div>
              <span class="tiny">{replayDepth.Cleared ? `Down in ${replayDepth.Seconds}s` : replayDepth.Outcome}</span>
            </div>
          </div>
        {/key}
        <div class="stage-foot">
          <span class="small">+{formatNumber(replaySouls)} Souls</span>
          <button class="ghost" onclick={() => lastRun && finishReplay(lastRun)} data-testid="nav-skip">Skip</button>
        </div>
      </section>
    {:else if shownRun}
      {@const stopper = guardianByKey(data, shownRun.StoppedBy)}
      <section class="run" data-testid="nav-run">
        <h3>Last descent: depth {shownRun.DeepestCleared} <span class="dim small">+{formatNumber(shownRun.Souls)} Souls</span></h3>
        <p class="dim small">{endedLine(data, shownRun)} {shownRun.RevivesUsed > 0 ? `Second breath used ${shownRun.RevivesUsed}x.` : ''}</p>
        {#if stopper && shownRun.Hint}
          <div class="why" data-testid="nav-hint">
            <MonsterPortrait monsterId={0} name={stopper.Name} src={guardianArt(stopper.Key)} size="md" />
            <p class="small"><span class="ability">{stopper.AbilityName}</span> {shownRun.Hint}</p>
          </div>
        {/if}
        <ol class="depths">
          {#each shownRun.Depths.slice(-8) as d (d.Depth)}
            {@const g = guardianByKey(data, d.Guardian)}
            <li class:fail={!d.Cleared} class:warden={d.Guardian === 'veles'}>
              <MonsterPortrait monsterId={0} name={g?.Name ?? 'Guardian'} src={d.Guardian ? guardianArt(d.Guardian) : null} size="sm" />
              <span class="dn">Depth {d.Depth} <span class="dim tiny">{g?.Name ?? ''}</span></span>
              {#if d.Elite}<span class="elite">{d.Elite}</span>{/if}
              <span class="dim tiny t">{d.Seconds}s</span>
              <span class="tiny r">{d.Cleared ? `+${d.Souls}` : 'stopped'}</span>
            </li>
          {/each}
        </ol>
      </section>
    {/if}

    <div class="souls">
      <span class="soul-count" data-testid="nav-souls">{formatNumber(data.Souls)}</span>
      <span class="dim small">Souls to spend</span>
    </div>

    <section class="ahead" data-testid="nav-ahead">
      <h3>Below your record</h3>
      <ul class="guardians">
        {#each ahead as a (a.depth)}
          {#if a.g}
            <li class:warden={a.g.Key === 'veles'}>
              <MonsterPortrait monsterId={0} name={a.g.Name} src={guardianArt(a.g.Key)} size="md" />
              <div class="g-text">
                <span class="small"><strong>{a.g.Name}</strong> <span class="dim tiny">depth {a.depth}</span></span>
                <span class="tiny"><span class="ability">{a.g.AbilityName}</span> <span class="dim">{a.g.AbilityText}</span></span>
              </div>
            </li>
          {/if}
        {/each}
      </ul>
    </section>

    {#if data.Board && data.Board.Top.length > 0}
      <section class="board" data-testid="nav-board">
        <h3>Deepest in Nav</h3>
        <ol class="board-list">
          {#each data.Board.Top as b (b.Rank)}
            <li class:me={b.Me}>
              <span class="rank">{b.Rank}</span>
              <span class="who">{b.Name}</span>
              <span class="dim tiny">Circle {b.Circle}</span>
              <strong class="small">depth {b.Depth}</strong>
            </li>
          {/each}
        </ol>
        {#if data.Board.MyRank > 10}<p class="dim tiny">You are #{data.Board.MyRank}.</p>{/if}
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
              <button disabled={busy || !data.Open || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 1)}>
                {u.Maxed ? 'Top' : `+1 - ${formatNumber(u.Cost)}`}
              </button>
              <button class="ghost" disabled={busy || !data.Open || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 100)} title="As many levels as your Souls buy">Max</button>
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
              <button disabled={busy || !data.Open || data.ResearchActive !== ''} onclick={() => research(l.Key)} data-testid={`nav-research-${l.Key}`}>
                <Money amount={l.Gold} />
              </button>
            </div>
          </li>
        {/each}
      </ul>
    </section>
    </div>

    <details class="rules bestiary" data-testid="nav-bestiary">
      <summary>The guardians of Nav</summary>
      <ul class="guardians">
        {#each data.Guardians as g (g.Key)}
          <li class:warden={g.Key === 'veles'}>
            <MonsterPortrait monsterId={0} name={g.Name} src={guardianArt(g.Key)} size="md" />
            <div class="g-text">
              <span class="small"><strong>{g.Name}</strong> <span class="dim tiny">{depthsOf(data, g)}</span></span>
              <span class="tiny"><span class="ability">{g.AbilityName}</span> <span class="dim">{g.AbilityText}</span></span>
            </div>
          </li>
        {/each}
      </ul>
    </details>

    <details class="rules">
      <summary>How Nav works</summary>
      <ul class="small">
        <li>Each depth has one guardian. Kill it within {data.SecondsPerGuardian} seconds or the descent ends; fall and it ends too.</li>
        <li>Guardians grow 10% stronger every depth, so every tenth more damage is about one depth more - forever.</li>
        <li>Every cleared depth pays Souls; deeper pays more. A run costs one descent, and one comes back every 20 minutes, offline too.</li>
        <li>Depth {data.CircleOpensAt} opens the next Circle: five times stronger guardians, three times the Souls. Depths 10, 25 and 50 of each Circle pay a chest the first time.</li>
        <li>Ten servants of Nav stand in turn, each with its own ability - one build will not beat them all. Veles, the Lord of Nav, waits every {data.WardenEvery}th depth: more health, harder blows, triple Souls.</li>
        <li>The first Veles you beat earns the title "Veles' Guest"; opening Circles 2, 3 and 5 earns three more. News tells everyone when someone beats a Veles or opens a Circle.</li>
        <li>Some guardians are elite - Armoured (more health), Swift (attack faster), Brute (hit harder) or Frail (less health).</li>
        <li>The Workshop and the research only count in Nav. Nothing here changes the rest of the game.</li>
      </ul>
    </details>
  {/if}
</section>
</div>

<style>
  .nav-wrap {
    max-width: 60rem;
    margin: 0 auto;
  }

  .nav-screen {
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

  .depths li.warden .dn {
    color: var(--warn, #e0b050);
  }

  .depths li.fail {
    color: var(--danger);
  }

  .stage {
    margin-top: 0.8rem;
    padding: 0.7rem;
    border: 1px solid var(--line);
    border-radius: var(--radius);
    background: linear-gradient(180deg, color-mix(in srgb, var(--accent) 8%, transparent), transparent);
  }

  .stage.warden {
    border-color: var(--warn, #e0b050);
  }

  .stage.fail {
    border-color: var(--danger);
  }

  .stage-card {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.8rem;
    align-items: center;
    animation: rise 0.12s ease-out;
  }

  .stage-card :global(.portrait[data-size='lg']) {
    width: 6.5rem;
    height: 6.5rem;
  }

  .stage-text {
    display: grid;
    gap: 0.2rem;
    min-width: 0;
    justify-items: start;
  }

  .stage-depth {
    font-size: var(--fs-xs);
    letter-spacing: 0.08em;
    text-transform: uppercase;
    opacity: 0.8;
  }

  .clock {
    width: 100%;
    height: 6px;
    border-radius: 3px;
    background: var(--line);
    overflow: hidden;
  }

  .clock span {
    display: block;
    height: 100%;
    width: var(--fill);
    background: var(--accent);
    animation: fill 0.14s ease-out;
  }

  .stage.fail .clock span {
    background: var(--danger);
  }

  .stage-foot {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-top: 0.5rem;
  }

  @keyframes rise {
    from {
      transform: translateY(6px);
    }
    to {
      transform: none;
    }
  }

  @keyframes fill {
    from {
      width: 0;
    }
  }

  .why {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.6rem;
    align-items: center;
    margin: 0.4rem 0;
    padding: 0.4rem 0.5rem;
    border-left: 3px solid var(--accent);
    background: color-mix(in srgb, var(--accent) 7%, transparent);
    border-radius: var(--radius);
  }

  .why p {
    margin: 0;
  }

  .board {
    margin-top: 0.8rem;
  }

  .board-list {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
  }

  .board-list li {
    display: grid;
    grid-template-columns: 1.6rem minmax(0, 1fr) auto auto;
    gap: 0.5rem;
    align-items: center;
    padding: 0.25rem 0.4rem;
    border-bottom: 1px solid var(--line);
  }

  .board-list li.me {
    background: color-mix(in srgb, var(--accent) 10%, transparent);
    border-radius: var(--radius);
  }

  .rank {
    font-weight: 700;
    text-align: center;
  }

  .who {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .guardians {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }

  .guardians li {
    display: grid;
    grid-template-columns: auto 1fr;
    align-items: center;
    gap: 0.6rem;
    padding: 0.3rem 0.4rem;
    border: 1px solid var(--line);
    border-radius: var(--radius);
  }

  .guardians li.warden {
    border-color: var(--warn, #e0b050);
  }

  .g-text {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
  }

  .ability {
    font-weight: 600;
    color: var(--accent);
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
