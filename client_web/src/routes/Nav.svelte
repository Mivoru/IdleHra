<script lang="ts">
  // Modul: PRICES COMPACT FROM 1,000 (owner, 2026-11-19: past 999 Souls the
  // rows went ragged). "+1 - 1 400" and "+1 - 999" were buttons of different
  // widths, so every Max button sat somewhere else; "1.4 k" keeps a price to
  // four characters, the button has a fixed width and the exact figure is
  // in the tooltip.
  const PRICE_COMPACT_FROM = 999;
  const price = (n: number) => formatNumber(n, PRICE_COMPACT_FROM);

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
  import { navKeys, fetchNav, navDescend, navBuy, navResearch, navBuyFrame, guardianArt, type NavRun, type NavGuardian, type NavState } from '../lib/net/nav';
  import CosmeticFrame from '../lib/ui/CosmeticFrame.svelte';
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import { formatDuration } from '../lib/net/commands';
  import { pushLocalNotice } from '../lib/stores/game';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import ScreenBanner from '../lib/ui/ScreenBanner.svelte';
  import Money from '../lib/ui/Money.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { play } from '../lib/ui/audio';
  import NavDescentStage from '../lib/ui/NavDescentStage.svelte';
  import { noteNavView } from '../lib/stores/navCharges';

  const client = useQueryClient();
  const nav = createQuery(() => ({ queryKey: navKeys.state, queryFn: fetchNav, refetchInterval: 60_000 }));
  const data = $derived(nav.data ?? null);
  // The menu dot follows what this screen shows (navCharges.ts).
  $effect(() => noteNavView(nav.data));

  // Last week's board reward arrives once, on the read that paid it.
  const shownNotices = new Set<string>();
  $effect(() => {
    for (const line of nav.data?.Notices ?? []) {
      if (shownNotices.has(line)) continue;
      shownNotices.add(line);
      pushLocalNotice(line, 'info');
      play('navRecord');
    }
  });

  let boardTab = $state<'week' | 'all'>('week');
  const plainUpgrades = $derived(data ? data.Workshop.filter((u) => !u.Guardian) : []);
  const banes = $derived(data ? data.Workshop.filter((u) => u.Guardian && !u.Locked) : []);
  const lockedBanes = $derived(data ? data.Workshop.filter((u) => u.Guardian && u.Locked) : []);
  const blessed = $derived(data ? data.Blessings.GoldPct + data.Blessings.XpPct + data.Blessings.LuckPct > 0 : false);

  function baneOf(state: NavState, guardianKey: string) {
    return state.Workshop.find((u) => u.Guardian === guardianKey);
  }

  // "The Vampire" opens a sentence; in the middle of one it is "the Vampire".
  function inSentence(subject: string): string {
    return subject.startsWith('The ') ? `the ${subject.slice(4)}` : subject;
  }

  function minutes(seconds: number): string {
    return `${Math.round(seconds / 60)} minutes`;
  }

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
    data
      ? Array.from({ length: 4 }, (_, i) => record + 1 + i).map((depth) => ({
          depth,
          g: guardianAt(data, depth),
          reward: data.Records.find((r) => r.Circle === activeCircle)?.Next?.find((n) => n.Depth === depth),
        }))
      : [],
  );

  let slot = $state(0);
  let circle = $state(0);
  let busy = $state(false);
  let lastRun = $state<NavRun | null>(null);

  const shownRun = $derived(lastRun ?? data?.LastRun ?? null);

  // Modul: THE DESCENT IS PLAYED BACK, NOT JUST LISTED (2026-11-19). The
  // server decides a run in one request; the scene (NavDescentStage) plays it
  // back over the screen - the shaft, each guardian, the record, the haul -
  // and closes on Continue. While it is open the Descend button waits.
  let stageRun = $state<NavRun | null>(null);
  const replaying = $derived(stageRun !== null);

  // A level bought flashes on its row, so the purchase is seen as well as heard.
  let flashed = $state('');
  let flashTimer: ReturnType<typeof setTimeout> | null = null;
  function flash(key: string) {
    flashed = key;
    if (flashTimer) clearTimeout(flashTimer);
    flashTimer = setTimeout(() => (flashed = ''), 900);
  }
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
        stageRun = r.Run;
      } else {
        pushLocalNotice(
          r?.Result === 'NoCharges' ? `No descents left - one comes back every ${minutes(data?.ChargeSeconds ?? 1200)}.`
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
      if (r?.Result === 'Bought') {
        play('navBuy');
        flash(key);
      } else
        pushLocalNotice(
          r?.Result === 'Maxed' ? 'That upgrade is at its top.'
            : r?.Result === 'Locked' ? (data?.Open ? 'That guardian has not stopped you yet.' : 'Nav opens when you beat Malakor.')
              : 'Not enough Souls.',
          'error',
        );
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
      if (r?.Result === 'Started') {
        play('navResearch');
        flash(key);
        pushLocalNotice('The Volkhv begins the research.', 'info');
      }
      else
        pushLocalNotice(
          r?.Result === 'Busy' ? 'The Volkhv is already researching.'
            : r?.Result === 'Locked' ? 'Nav opens when you beat Malakor.'
              : r?.Result === 'NotOpen' ? 'Open a deeper Circle first.'
                : r?.Result === 'Maxed' ? 'That line is at its top for now - a deeper Circle raises it.'
                  : 'Not enough gold.',
          'error',
        );
    } finally {
      busy = false;
      await refresh();
    }
  }

  async function buyFrame(id: string) {
    if (busy) return;
    busy = true;
    try {
      const r = await navBuyFrame(id);
      if (r?.Result === 'Bought') pushLocalNotice('The frame is yours - wear it from the Wardrobe.', 'info');
      else pushLocalNotice(r?.Result === 'Owned' ? 'You already own that frame.' : r?.Result === 'NotReached' ? 'Go deeper first.' : r?.Result === 'Locked' ? 'Nav opens when you beat Malakor.' : 'Not enough Souls.', 'error');
    } finally {
      busy = false;
      await refresh();
    }
  }

  function frameNeed(f: { Circle: number; Depth: number }): string {
    return f.Circle > 1 ? `depth ${f.Depth} of Circle ${f.Circle}` : `depth ${f.Depth}`;
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

{#if stageRun && data}
  <NavDescentStage run={stageRun} nav={data} onclose={() => (stageRun = null)} />
{/if}

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
    <!-- Modul: this used to read "Nav is closed." for ANY failed load, so a
         dropped request on a phone looked like the feature being switched off
         (owner screenshot, 2026-11-20). A closed Nav arrives as data
         (Open=false, LockedReason) and never lands here. -->
    <p class="dim" data-testid="nav-load-error">
      Nav could not be loaded{nav.error ? ` (${nav.error.message})` : ''}.
    </p>
    <button class="tiny-btn" onclick={() => nav.refetch()}>Try again</button>
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
      <button class="primary go" disabled={busy || replaying || data.Charges <= 0 || !data.Open} onclick={descend} data-testid="nav-descend" data-guide="nav-descend">
        {busy ? 'Descending...' : replaying ? 'Going down...' : 'Descend'}
      </button>
      <p class="dim small charges" data-testid="nav-charges">
        {data.Charges} of {data.ChargeCap} descents
        {#if data.Charges < data.ChargeCap} - next in {formatDuration(data.NextChargeInSeconds)}{/if}
      </p>
      {#if data.Omen}
        {@const og = guardianByKey(data, data.Omen.Key)}
        <div class="omen" data-testid="nav-omen">
          <MonsterPortrait monsterId={0} name={data.Omen.Name} src={guardianArt(data.Omen.Key)} size="sm" />
          <p class="small">
            <strong>This week {inSentence(og?.Subject ?? data.Omen.Name)} is restless:</strong>
            <span class="ability">{og?.AbilityName}</span> {data.Omen.AbilityPct}% as strong, its depths pay {data.Omen.SoulsFactor}x Souls.
            <span class="dim tiny">Changes in {formatDuration(data.Omen.EndsInSeconds)}.</span>
          </p>
        </div>
      {/if}
    </div>

    {#if shownRun}
      {@const stopper = guardianByKey(data, shownRun.StoppedBy)}
      <section class="run" data-testid="nav-run">
        <h3>Last descent: depth {shownRun.DeepestCleared} <span class="dim small">+{formatNumber(shownRun.Souls)} Souls</span></h3>
        <p class="dim small">{endedLine(data, shownRun)} {shownRun.RevivesUsed > 0 ? `Second breath used ${shownRun.RevivesUsed}x.` : ''}</p>
        {#if stopper && shownRun.Hint}
          {@const bane = baneOf(data, stopper.Key)}
          <div class="why" data-testid="nav-hint">
            <MonsterPortrait monsterId={0} name={stopper.Name} src={guardianArt(stopper.Key)} size="md" />
            <div class="why-text">
              <p class="small"><span class="ability">{stopper.AbilityName}</span> {shownRun.Hint}</p>
              {#if bane && !bane.Locked}
                <div class="why-buy">
                  <span class="tiny">Bane: <strong>{bane.Name}</strong> <span class="dim">lv {bane.Level} / {bane.MaxLevel} - its rule {Math.round(bane.Total)}% weaker</span></span>
                  <button disabled={busy || !data.Open || bane.Maxed || data.Souls < bane.Cost} onclick={() => buy(bane.Key, 1)} class="price" title={numberTitle(bane.Cost, PRICE_COMPACT_FROM)} data-testid="nav-hint-bane">
                    {bane.Maxed ? 'Top' : `+1 - ${price(bane.Cost)}`}
                  </button>
                </div>
              {/if}
            </div>
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

    <section class="spirits" data-testid="nav-spirits">
      <h3>Spirits of Nav</h3>
      <p class="dim small">
        Your {data.Spirits.Count} best adults outside the three slots walk beside every descent. Each aptitude point above {data.Spirits.SpiritFloor} adds: Strength damage, Endurance health, Skill speed, Fortune Souls.
      </p>
      {#if data.Spirits.Walking.length === 0}
        <p class="small">No adults rest on your bench yet - breed, and let the grown children rest.</p>
      {:else}
        <ul class="spirit-list">
          {#each data.Spirits.Walking as sp, i (i)}
            <li>
              <span class="who small">{sp.Name}</span>
              <span class="apt tiny" title="Strength / Skill / Endurance / Fortune">{sp.Strength} / {sp.Skill} / {sp.Endurance} / {sp.Fortune}</span>
            </li>
          {/each}
        </ul>
        {#if data.Spirits.DamagePct + data.Spirits.HealthPct + data.Spirits.SpeedPct + data.Spirits.SoulsPct === 0}
          <p class="dim tiny" data-testid="nav-spirit-sum">None of them is above {data.Spirits.SpiritFloor} in anything yet - stronger children make stronger spirits.</p>
        {:else}
          <p class="small spirit-sum" data-testid="nav-spirit-sum">
            +{data.Spirits.DamagePct}% damage, +{data.Spirits.HealthPct}% health, +{data.Spirits.SpeedPct}% speed, +{data.Spirits.SoulsPct}% Souls
          </p>
        {/if}
      {/if}
    </section>

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
                {#if a.reward}
                  <span class="tiny reward" data-testid="nav-depth-reward">
                    First clear: <span class="chest-{a.reward.Chest.toLowerCase()}">{a.reward.Chest} chest</span>{#if a.reward.Diamonds > 0}, {a.reward.Diamonds} diamonds{/if}{#if a.reward.Tickets > 0}, a ticket for each dungeon{/if}
                  </span>
                {/if}
              </div>
            </li>
          {/if}
        {/each}
      </ul>
    </section>

    <section class="board" data-testid="nav-board">
      <div class="board-head">
        <h3>Deepest in Nav</h3>
        <div class="tabs" role="tablist">
          <button role="tab" class="chip" class:on={boardTab === 'week'} aria-selected={boardTab === 'week'} onclick={() => (boardTab = 'week')} data-testid="nav-board-week">This week</button>
          <button role="tab" class="chip" class:on={boardTab === 'all'} aria-selected={boardTab === 'all'} onclick={() => (boardTab = 'all')} data-testid="nav-board-all">All time</button>
        </div>
      </div>
      {#if boardTab === 'week'}
        {#if data.WeekBoard && data.WeekBoard.Top.length > 0}
          <ol class="board-list" data-testid="nav-week-list">
            {#each data.WeekBoard.Top as b (b.Rank)}
              <li class:me={b.Me}>
                <span class="rank">{b.Rank}</span>
                <span class="who">{b.Name}</span>
                <span class="dim tiny">Circle {b.Circle}</span>
                <strong class="small">depth {b.Depth}</strong>
              </li>
            {/each}
          </ol>
        {:else}
          <p class="small">Nobody has descended this week yet.</p>
        {/if}
        <p class="dim tiny">
          On Monday the top three win a chest - Ancient, Gold, Silver - if at least {data.WeekBoard?.MinRunners ?? 2} descended. Ends in {formatDuration(data.WeekBoard?.EndsInSeconds ?? 0)}.
        </p>
      {:else if data.Board && data.Board.Top.length > 0}
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
      {:else}
        <p class="small">No records yet.</p>
      {/if}
    </section>
    <section class="frames" data-testid="nav-frames">
      <h3>Frames of Nav</h3>
      <p class="dim small">Souls for something to wear - each frame shows where you have been.</p>
      <ul class="frame-list">
        {#each data.Frames as f (f.Id)}
          <li class:owned={f.Owned}>
            <span class="frame-preview"><CosmeticFrame frameId={f.Id} /></span>
            <span class="small f-name">{f.Name}</span>
            {#if f.Owned}
              <span class="dim tiny">Owned</span>
            {:else if !f.Reached}
              <span class="dim tiny">Reach {frameNeed(f)}</span>
            {:else}
              <button disabled={busy || !data.Open || data.Souls < f.Price} onclick={() => buyFrame(f.Id)} data-testid={`nav-frame-${f.Id}`}>{price(f.Price)} Souls</button>
            {/if}
          </li>
        {/each}
      </ul>
    </section>
    </div>

    <div class="col">
    <section class="workshop">
      <h3>Nav Workshop</h3>
      <p class="dim small">Souls buy strength that only counts down here - prices climb 12% a level and never stop.</p>
      <ul class="upgrades">
        {#each plainUpgrades as u (u.Key)}
          <li class:flash={flashed === u.Key} data-testid={`nav-up-${u.Key}`}>
            <div class="u-text">
              <strong>{u.Name}</strong> <span class="dim tiny">lv {u.Level}{u.MaxLevel ? ` / ${u.MaxLevel}` : ''}</span>
              <span class="dim tiny">{u.Effect}</span>
            </div>
            <div class="u-buy">
              <button disabled={busy || !data.Open || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 1)} class="price" title={numberTitle(u.Cost, PRICE_COMPACT_FROM)}>
                {u.Maxed ? 'Top' : `+1 - ${price(u.Cost)}`}
              </button>
              <button class="ghost" disabled={busy || !data.Open || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 100)} title="As many levels as your Souls buy">Max</button>
            </div>
          </li>
        {/each}
      </ul>

      <h4 class="sub">Banes</h4>
      <p class="dim small">Each wears down one guardian's rule, 4% a level. A bane opens when its guardian first stops you.</p>
      <ul class="upgrades banes" data-testid="nav-banes">
        {#each banes as u (u.Key)}
          {@const g = guardianByKey(data, u.Guardian)}
          <li class:flash={flashed === u.Key} data-testid={`nav-up-${u.Key}`}>
            <MonsterPortrait monsterId={0} name={g?.Name ?? ''} src={guardianArt(u.Guardian)} size="sm" />
            <div class="u-text">
              <strong>{u.Name}</strong> <span class="dim tiny">lv {u.Level} / {u.MaxLevel}</span>
              <span class="dim tiny">{g?.AbilityName} {Math.round(u.Total)}% weaker</span>
            </div>
            <div class="u-buy">
              <button disabled={busy || !data.Open || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 1)} class="price" title={numberTitle(u.Cost, PRICE_COMPACT_FROM)}>
                {u.Maxed ? 'Top' : `+1 - ${price(u.Cost)}`}
              </button>
              <button class="ghost" disabled={busy || !data.Open || u.Maxed || data.Souls < u.Cost} onclick={() => buy(u.Key, 100)} title="As many levels as your Souls buy">Max</button>
            </div>
          </li>
        {/each}
      </ul>
      {#if lockedBanes.length > 0}
        <details class="locked-banes" data-testid="nav-banes-locked">
          <summary class="small">{lockedBanes.length} banes still locked</summary>
          <ul class="locked-list">
            {#each lockedBanes as u (u.Key)}
              {@const g = guardianByKey(data, u.Guardian)}
              <li data-testid={`nav-up-${u.Key}`}>
                <MonsterPortrait monsterId={0} name={g?.Name ?? ''} src={guardianArt(u.Guardian)} size="sm" />
                <span class="tiny"><strong>{u.Name}</strong> <span class="dim">- opens when {inSentence(g?.Subject ?? 'its guardian')} stops you.</span></span>
              </li>
            {/each}
          </ul>
        </details>
      {/if}
    </section>

    <section class="research">
      <h3>The Volkhv's research</h3>
      <p class="dim small">Gold and time for lasting knowledge of the deep. One line at a time. The lines marked "everywhere" count in the whole game, and reach higher with every Circle.</p>
      {#if blessed}
        <p class="small blessings" data-testid="nav-blessings">
          Nav gives everywhere: +{data.Blessings.GoldPct}% combat gold, +{data.Blessings.XpPct}% combat XP, +{data.Blessings.LuckPct}% loot luck
        </p>
      {/if}
      {#if data.ResearchActive}
        <p class="small" data-testid="nav-research-active">
          Researching <strong>{data.Research.find((l) => l.Key === data.ResearchActive)?.Name}</strong> - {formatDuration(data.ResearchLeftSeconds)} left
        </p>
      {/if}
      <ul class="upgrades">
        {#each data.Research as l (l.Key)}
          <li class:closed={!l.Open} class:flash={flashed === l.Key}>
            <div class="u-text">
              <strong>{l.Name}</strong>
              {#if l.Outward}<span class="everywhere tiny">everywhere</span>{/if}
              <span class="dim tiny">lv {l.Level}{l.Cap > 0 ? ` / ${l.Cap}` : ''}</span>
              <span class="dim tiny">{l.Effect}{l.Open && !l.Maxed ? ` - ${formatDuration(l.Seconds)}` : ''}</span>
            </div>
            <div class="u-buy">
              {#if !l.Open}
                <span class="dim tiny">Opens with Circle {l.OpensAtCircle}</span>
              {:else if l.Maxed}
                <span class="dim tiny">Top for now</span>
              {:else}
                <button disabled={busy || !data.Open || data.ResearchActive !== ''} onclick={() => research(l.Key)} data-testid={`nav-research-${l.Key}`}>
                  <Money amount={l.Gold} />
                </button>
              {/if}
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
        <li>Every cleared depth pays Souls; deeper pays more. A run costs one descent, and one comes back every {minutes(data.ChargeSeconds)}, offline too.</li>
        <li>Depth {data.CircleOpensAt} opens the next Circle: five times stronger guardians, three times the Souls. Every depth pays the first time you clear it: gold and a Bronze chest, every 5th a Gold chest, every 10th an Ancient chest with diamonds and a ticket for each dungeon. Deeper Circles pay more.</li>
        <li>Ten servants of Nav stand in turn, each with its own ability - one build will not beat them all. Veles, the Lord of Nav, waits every {data.WardenEvery}th depth: more health, harder blows, triple Souls.</li>
        <li>The first Veles you beat earns the title "Veles' Guest"; opening Circles 2, 3 and 5 earns three more. News tells everyone when someone beats a Veles or opens a Circle.</li>
        <li>Some guardians are elite - Armoured (more health), Swift (attack faster), Brute (hit harder) or Frail (less health).</li>
        <li>Each week one guardian is restless: its rule is half again as strong and its depths pay double Souls.</li>
        <li>When a guardian first stops you, its bane opens in the Workshop - each level takes 4% off its rule.</li>
        <li>Your best adults outside the three slots walk with you as spirits. Breed better ones, and they push deeper.</li>
        <li>The Workshop counts only in Nav. Three research lines - Veles' bounty, Memory of the dead and Mokosh's thread - open with Circles 2 and 3 and count in the whole game.</li>
        <li>"Deepest this week" starts over every Monday; the top three win a chest when at least two descended.</li>
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

  .reward {
    color: var(--text-dim, inherit);
  }

  .chest-gold {
    color: var(--warn, #e0b050);
    font-weight: 600;
  }

  .chest-ancient {
    color: var(--rarity-10, #e0553f);
    font-weight: 600;
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

  /* A bought level: the row catches soul-light for a moment. */
  .upgrades li.flash {
    animation: soul-flash 0.9s ease-out;
  }

  @keyframes soul-flash {
    0% { background: color-mix(in srgb, #6fe8d8 38%, transparent); box-shadow: inset 3px 0 0 #6fe8d8; }
    100% { background: transparent; box-shadow: inset 3px 0 0 transparent; }
  }

  @media (prefers-reduced-motion: reduce) {
    .upgrades li.flash { animation: none; }
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

  .omen {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 0.5rem;
    align-items: center;
    padding: 0.35rem 0.5rem;
    border: 1px dashed var(--line);
    border-radius: var(--radius);
  }

  .omen p,
  .why-text p {
    margin: 0;
  }

  .why-text {
    display: grid;
    gap: 0.35rem;
    min-width: 0;
  }

  .why-buy {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.4rem;
  }

  .why-buy button {
    min-height: 44px;
  }

  .spirits h3,
  .frames h3,
  .ahead h3 {
    margin: 0 0 0.25rem;
  }

  .spirit-list {
    list-style: none;
    margin: 0.3rem 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
  }

  .spirit-list li {
    display: flex;
    justify-content: space-between;
    gap: 0.5rem;
    padding: 0.2rem 0.4rem;
    border-bottom: 1px solid var(--line);
  }

  .apt {
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .spirit-sum {
    margin: 0.3rem 0 0;
    color: var(--accent);
    font-weight: 600;
  }

  .board-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .board-head h3 {
    margin: 0;
  }

  .tabs {
    display: flex;
    gap: 0.3rem;
  }

  .sub {
    margin: 0.9rem 0 0.2rem;
  }

  .banes .u-text {
    flex: 1 1 9rem;
  }

  .u-buy {
    margin-left: auto;
  }

  .u-buy button {
    white-space: nowrap;
  }

  .u-buy button.price,
  .why-buy button.price {
    min-width: 7.5rem;
    font-variant-numeric: tabular-nums;
  }

  .locked-banes summary {
    cursor: pointer;
    min-height: 44px;
    display: flex;
    align-items: center;
  }

  .locked-list {
    list-style: none;
    margin: 0.2rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .locked-list li {
    display: grid;
    grid-template-columns: auto 1fr;
    align-items: center;
    gap: 0.5rem;
    opacity: 0.8;
  }

  .research li.closed .u-text strong {
    opacity: 0.75;
  }

  .everywhere {
    display: inline-block;
    width: fit-content;
    padding: 0 0.4rem;
    border-radius: 999px;
    border: 1px solid var(--accent);
    color: var(--accent);
  }

  .blessings {
    margin: 0.2rem 0;
    color: var(--accent);
    font-weight: 600;
  }

  .frame-list {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(8rem, 1fr));
    gap: 0.5rem;
  }

  .frame-list li {
    display: grid;
    justify-items: center;
    gap: 0.25rem;
    padding: 0.5rem 0.3rem;
    border: 1px solid var(--line);
    border-radius: var(--radius);
    text-align: center;
  }

  .frame-list li.owned {
    border-color: var(--accent);
  }

  .frame-list button {
    min-height: 44px;
  }

  .frame-preview {
    position: relative;
    display: block;
    width: 4rem;
    height: 4rem;
    border-radius: 50%;
    background: var(--bg-raised);
  }

  .f-name {
    font-weight: 600;
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
