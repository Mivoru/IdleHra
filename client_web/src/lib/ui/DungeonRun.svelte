<script lang="ts">
  // Modul: THE RUN, PLAYED AS A FIGHT (owner, 2026-10-31: "not a text log",
  // and "not wholly passive"). The server simulates the run in segments: each
  // ends at a telegraph, where the scene asks the server for the swipes,
  // takes them by touch, arrow keys or WASD, and sends them back to be JUDGED
  // there - the client never says "I dodged". A clean dodge brings two bonus
  // cards to choose from; a miss lands a heavy blow and a penalty. Every event
  // carries both bars in percent, so the bars animate from server numbers and
  // health is never inferred (the combat feed rule). 1x, 2x and Skip change
  // only how fast a segment is shown.
  import { onDestroy } from 'svelte';
  import { useQueryClient } from '@tanstack/svelte-query';
  import {
    answerTelegraph,
    dungeonKeys,
    pickPerk,
    showTelegraph,
    type DungeonRunAnswer,
    type DungeonInfo,
    type DungeonEvent,
    type DungeonPerk,
    type DungeonReward,
    type DungeonRunResult,
  } from '../net/dungeon';
  import { invalidateOwnedItems } from '../net/queryClient';
  import { prettifyBaseId } from '../net/content';
  import { requestScreen } from '../stores/navigation';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { play } from './audio';
  import { formatNumber } from './format';
  import { motionMs } from './motion';
  import MonsterPortrait from './MonsterPortrait.svelte';
  import PlayerAvatar from './PlayerAvatar.svelte';
  import Bar from './Bar.svelte';

  interface Props {
    answer: DungeonRunAnswer;
    dungeon: DungeonInfo;
    stage: number;
    onclose: () => void;
  }
  let { answer, dungeon, stage, onclose }: Props = $props();
  const client = useQueryClient();

  const BOSS_ART: Record<DungeonInfo['Key'], string> = {
    Xp: '/dungeon/boss-koschei.webp',
    Gold: '/dungeon/boss-zmey.webp',
    Equipment: '/dungeon/boss-babayaga.webp',
  };
  const GATE_ART: Record<DungeonInfo['Key'], string> = {
    Xp: '/dungeon/gate-xp.webp',
    Gold: '/dungeon/gate-gold.webp',
    Equipment: '/dungeon/gate-equipment.webp',
  };
  const TONE: Record<DungeonInfo['Key'], string> = { Xp: 'xp', Gold: 'gold', Equipment: 'equipment' };
  const ARROW: Record<string, string> = { Up: '↑', Down: '↓', Left: '←', Right: '→' };

  // The live run: what has been played and what the server still holds.
  // svelte-ignore state_referenced_locally
  const runId = answer.RunId ?? '';
  // svelte-ignore state_referenced_locally
  let events = $state<DungeonEvent[]>([...(answer.Events ?? answer.Result?.Events ?? [])]);
  // svelte-ignore state_referenced_locally
  let paused = $state(answer.Paused ?? false);
  // svelte-ignore state_referenced_locally
  let result = $state<DungeonRunResult | null>(answer.Result ?? null);
  // svelte-ignore state_referenced_locally
  let reward = $state<DungeonReward | null>(answer.Reward ?? null);
  // svelte-ignore state_referenced_locally
  let offer = $state<DungeonPerk[] | null>(answer.Offer ?? null);
  // svelte-ignore state_referenced_locally
  const partySize = answer.PartySize ?? answer.Result?.PartySize ?? 1;
  let busy = false;

  const total = $derived(events.length > 0 ? events[events.length - 1].AtSeconds : 0);
  const selfId = $derived(Number($playerState?.PlayerId ?? 0));

  // Fight seconds per real second at 1x, set per segment: a segment plays in
  // about four seconds, never slower than real time.
  let segStart = 0;
  let baseRate = $state(Math.max(1, total / 4));
  let speed = $state(1);
  let simT = $state(0);
  let index = $state(0); // events[0..index) have been reached
  let pausedUntil = 0; // real ms: banners hold the clock (wave cards, the boss intro)
  let finished = $state(false);
  let introBoss = $state(false);
  let banner = $state<{ text: string; kind: string; key: number } | null>(null);
  let bannerKey = 0;
  let enemyHit = $state(0);
  let selfHit = $state(0);
  let enemyHp = $state(100);
  let enemyShield = $state(0);
  let selfHp = $state(100);
  let fight = $state(0); // 0-4 waves, 5 the boss
  let wavesCleared = $state(0);
  let showGate = $state(true);
  /** Bonuses and penalties taken so far, as chips. */
  let taken = $state<{ name: string; penalty: boolean }[]>([]);

  // The telegraph on screen: the swipes asked for, how many are in, the clock.
  let telegraph = $state<{ dirs: string[]; windowMs: number; startedAt: number } | null>(null);
  let entered = $state<string[]>([]);
  /** A touch screen swipes; anything else uses the keyboard. */
  const touch = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches;
  let wrong = $state(false);
  let timeLeft = $state(1);

  const isBoss = $derived(fight >= 5);
  // svelte-ignore state_referenced_locally
  const waveFaces: number[] = answer.WaveMonsterIds ?? answer.Result?.WaveMonsterIds ?? [];
  const waveMonster = $derived(waveFaces[Math.min(fight, 4)] ?? 0);

  function showBanner(text: string, kind: string, holdMs = 1500) {
    banner = { text, kind, key: ++bannerKey };
    const k = bannerKey;
    setTimeout(() => {
      if (banner?.key === k) banner = null;
    }, motionMs(holdMs) || holdMs);
  }

  function reach(e: DungeonEvent) {
    switch (e.Kind) {
      case 'wave':
        fight = e.Value - 1;
        showBanner(`Wave ${e.Value}`, 'wave', 900);
        pausedUntil = performance.now() + (motionMs(700) || 0);
        break;
      case 'boss':
        fight = 5;
        introBoss = true;
        play('bossFirstClear');
        pausedUntil = performance.now() + (motionMs(2200) || 300);
        setTimeout(() => (introBoss = false), motionMs(2200) || 300);
        break;
      case 'cleared':
        wavesCleared += 1;
        break;
      case 'ability': {
        const name = e.Text.split(' - ')[0];
        showBanner(name, 'ability', 1700);
        // Not a combat clip: those are muted off the Combat screen (audio.ts).
        play('notification');
        break;
      }
      case 'meal':
        showBanner(e.Value === 5 ? 'Last meal' : `Meal ${e.Value}`, 'meal', 1000);
        break;
      case 'dodged':
        showBanner('Dodged!', 'dodged', 1100);
        break;
      case 'missed':
        selfHit++;
        showBanner('The blow lands!', 'missed', 1100);
        play('playerDied');
        break;
      case 'perk':
        taken = [...taken, { name: e.Text, penalty: false }];
        showBanner(`+ ${e.Text}`, 'perk', 1200);
        break;
      case 'penalty':
        taken = [...taken, { name: e.Text, penalty: true }];
        showBanner(e.Text, 'penalty', 1400);
        break;
      default:
        break;
    }
  }

  function barsAt(t: number) {
    // The bars between the event just reached and the next one, linearly.
    const prev = index > 0 ? events[index - 1] : null;
    const next = index < events.length ? events[index] : null;
    if (!prev) return;
    if (!next || next.AtSeconds <= prev.AtSeconds) {
      enemyHp = prev.EnemyHpPct;
      selfHp = prev.PlayerHpPct;
      enemyShield = prev.ShieldPct;
      return;
    }
    // A new fight starts at full - never sweep the bar up from the last kill.
    const freshFight = next.Kind === 'wave' || next.Kind === 'boss';
    const f = Math.min(1, Math.max(0, (t - prev.AtSeconds) / (next.AtSeconds - prev.AtSeconds)));
    const e = freshFight ? prev.EnemyHpPct : prev.EnemyHpPct + (next.EnemyHpPct - prev.EnemyHpPct) * f;
    const s = prev.PlayerHpPct + (next.PlayerHpPct - prev.PlayerHpPct) * f;
    if (e < enemyHp - 0.6) enemyHit++;
    if (s < selfHp - 0.6) selfHit++;
    enemyHp = e;
    selfHp = s;
    enemyShield = prev.ShieldPct;
  }

  function finish() {
    if (finished || !result) return;
    finished = true;
    const last = events[events.length - 1];
    if (last) {
      enemyHp = last.EnemyHpPct;
      selfHp = last.PlayerHpPct;
    }
    play(result.Won ? 'chestOpenLegendary' : 'playerDied');
    client.invalidateQueries({ queryKey: dungeonKeys.view });
    if (reward?.PieceId || reward?.ChestId) invalidateOwnedItems(client);
  }

  /** The segment is played out: finish, or open the pause the server is holding. */
  function segmentEnd() {
    if (result) {
      finish();
      return;
    }
    if (offer || telegraph || busy || !paused) return;
    void openTelegraph();
  }

  async function openTelegraph() {
    busy = true;
    try {
      const t = await showTelegraph(runId);
      if (!t || t.Outcome !== 'Ok') {
        pushLocalNotice('The run was lost on the way - start another.', 'error');
        onclose();
        return;
      }
      entered = [];
      wrong = false;
      timeLeft = 1;
      telegraph = { dirs: t.Dirs, windowMs: t.WindowMs, startedAt: performance.now() };
      play('notification');
    } finally {
      busy = false;
    }
  }

  /** One swipe or key. A wrong one ends the telegraph at once. */
  function input(dir: string) {
    if (!telegraph || busy) return;
    const want = telegraph.dirs[entered.length];
    entered = [...entered, dir];
    if (dir !== want) {
      wrong = true;
      void sendAnswer();
    } else if (entered.length === telegraph.dirs.length) {
      void sendAnswer();
    }
  }

  async function sendAnswer() {
    if (!telegraph || busy) return;
    busy = true;
    const dirs = entered;
    try {
      const reply = await answerTelegraph(runId, dirs);
      telegraph = null;
      if (!reply || reply.Outcome !== 'Ok') {
        pushLocalNotice('The run was lost on the way - start another.', 'error');
        onclose();
        return;
      }
      take(reply);
    } finally {
      busy = false;
    }
  }

  async function choose(p: DungeonPerk) {
    if (busy) return;
    busy = true;
    try {
      const reply = await pickPerk(runId, p.Id);
      if (!reply || reply.Outcome !== 'Ok') {
        pushLocalNotice(reply?.Reason || 'That bonus could not be taken.', 'error');
        return;
      }
      offer = null;
      take(reply);
    } finally {
      busy = false;
    }
  }

  /** A new segment from the server: queue its events and its pause. */
  function take(reply: DungeonRunAnswer) {
    const fresh = reply.Events ?? [];
    segStart = total;
    events = [...events, ...fresh];
    const end = fresh.length > 0 ? fresh[fresh.length - 1].AtSeconds : segStart;
    baseRate = Math.max(1, (end - segStart) / 4);
    paused = reply.Paused ?? false;
    offer = reply.Offer ?? null;
    if (reply.Result) result = reply.Result;
    if (reply.Reward) reward = reply.Reward;
    // A dodge's own event ("Dodged!") is in this segment; show it now, the cards over it.
    if (offer) {
      while (index < events.length) reach(events[index++]);
      barsAt(simT = total);
    }
  }

  // Swipes: a pointer that travels 30px is a swipe in its main direction.
  let swipeFrom: { x: number; y: number } | null = null;
  function down(e: PointerEvent) {
    if (!telegraph) return;
    swipeFrom = { x: e.clientX, y: e.clientY };
  }
  function up(e: PointerEvent) {
    if (!telegraph || !swipeFrom) return;
    const dx = e.clientX - swipeFrom.x;
    const dy = e.clientY - swipeFrom.y;
    swipeFrom = null;
    if (Math.max(Math.abs(dx), Math.abs(dy)) < 30) return;
    input(Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 'Right' : 'Left') : dy > 0 ? 'Down' : 'Up');
  }
  const KEYS: Record<string, string> = {
    ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    w: 'Up', s: 'Down', a: 'Left', d: 'Right', W: 'Up', S: 'Down', A: 'Left', D: 'Right',
  };
  function key(e: KeyboardEvent) {
    if (!telegraph || !KEYS[e.key]) return;
    e.preventDefault();
    input(KEYS[e.key]);
  }

  let raf = 0;
  let lastFrame = 0;
  function frame(now: number) {
    raf = requestAnimationFrame(frame);
    const dt = lastFrame ? Math.min(100, now - lastFrame) / 1000 : 0;
    lastFrame = now;
    if (telegraph) {
      timeLeft = Math.max(0, 1 - (now - telegraph.startedAt) / Math.max(1, telegraph.windowMs));
      if (timeLeft <= 0 && !busy) void sendAnswer();
      return;
    }
    if (finished || showGate || offer) return;
    if (now < pausedUntil) return;
    simT = Math.min(total, simT + dt * baseRate * speed);
    while (index < events.length && events[index].AtSeconds <= simT) {
      reach(events[index]);
      index++;
      if (now < pausedUntil) break;
    }
    barsAt(simT);
    if (index >= events.length && simT >= total) segmentEnd();
  }

  /** Skip plays the current segment out at once - up to the next dodge, or the end. */
  function skip() {
    for (; index < events.length; index++) {
      const e = events[index];
      if (e.Kind === 'cleared') wavesCleared += 1;
      if (e.Kind === 'wave' || e.Kind === 'boss') fight = e.Kind === 'boss' ? 5 : e.Value - 1;
      if (e.Kind === 'perk' || e.Kind === 'penalty') taken = [...taken, { name: e.Text, penalty: e.Kind === 'penalty' }];
    }
    simT = total;
    barsAt(simT);
    introBoss = false;
    banner = null;
    showGate = false;
    segmentEnd();
  }

  // Modul: THE GATE OPENS FIRST - the owner's clip (2026-10-31): the camera
  // walks into the gate and smoke fills the frame, cut from 10 s to 3 s,
  // H.264 so Safari plays it too. The smoke's last frame fades into the
  // fight. A clip that has not started within 1.2 s (blocked autoplay, slow
  // network, decode error) gives way to the CSS doors - the grey video icon
  // the owner saw once must never be what a player looks at.
  const GATE_CLIP: Record<DungeonInfo['Key'], string> = {
    Xp: '/dungeon/gate-xp.mp4',
    Gold: '/dungeon/gate-gold.mp4',
    Equipment: '/dungeon/gate-equipment.mp4',
  };
  let gateMode = $state<'video' | 'doors'>(motionMs(1000) === 0 ? 'doors' : 'video');
  let gateFading = $state(false);
  let gateStarted = false;
  function closeGate() {
    if (!showGate || gateFading) return;
    gateFading = true;
    setTimeout(() => (showGate = false), 400);
  }
  function gateFallback() {
    if (gateStarted || gateMode === 'doors') return;
    gateMode = 'doors';
    setTimeout(() => (showGate = false), motionMs(2400) || 0);
  }
  if (gateMode === 'doors') setTimeout(() => (showGate = false), motionMs(2400) || 0);
  else {
    setTimeout(gateFallback, 1200);
    setTimeout(closeGate, 5000); // a clip that stalls halfway still ends
  }
  raf = requestAnimationFrame(frame);
  onDestroy(() => cancelAnimationFrame(raf));
</script>

<svelte:window onkeydown={key} />

<section class="panel run" data-testid="dungeon-run" data-tone={TONE[dungeon.Key]}>
  <header class="run-head">
    <h2>{dungeon.Name} &middot; stage {stage}</h2>
    {#if !finished}
      <div class="speed" role="group" aria-label="Playback speed">
        <button class:on={speed === 1} onclick={() => (speed = 1)}>1x</button>
        <button class:on={speed === 2} onclick={() => (speed = 2)}>2x</button>
        <button onclick={skip} data-testid="dungeon-skip" disabled={!!telegraph || !!offer}>Skip</button>
      </div>
    {/if}
  </header>

  <div class="scene" class:tg={!!telegraph} style="--scene-bg: url('{GATE_ART[dungeon.Key]}')" onpointerdown={down} onpointerup={up} role="presentation">
    <ol class="pips" aria-label="Progress">
      {#each [0, 1, 2, 3, 4] as w (w)}
        <li class:done={w < wavesCleared} class:now={fight === w && !finished}></li>
      {/each}
      <li class="skull" class:now={isBoss && !finished} class:done={finished && result?.Won}>&#9760;</li>
    </ol>

    {#if taken.length > 0}
      <ul class="taken" aria-label="Bonuses and penalties this run" data-testid="dungeon-taken">
        {#each taken as t, i (i)}<li class:bad={t.penalty}>{t.name}</li>{/each}
      </ul>
    {/if}

    <div class="enemy" class:boss={isBoss}>
      {#key enemyHit}
        <span class="shake" class:dead={finished && result?.Won && isBoss}>
          {#if isBoss}
            <img class="boss-art" src={BOSS_ART[dungeon.Key]} alt={dungeon.Boss} />
          {:else if waveMonster > 0}
            <MonsterPortrait monsterId={waveMonster} name="Wave {fight + 1}" size="scene" />
          {/if}
        </span>
      {/key}
      <div class="enemy-bar">
        <span class="who">{isBoss ? dungeon.Boss : `Wave ${Math.min(fight + 1, 5)} of 5`}</span>
        <Bar value={enemyHp} max={100} color="var(--danger)" label={`${Math.ceil(enemyHp)}%`} />
        {#if enemyShield > 0}<div class="shield" style="width: {Math.min(100, enemyShield)}%"></div>{/if}
      </div>
    </div>

    <div class="self">
      {#key selfHit}
        <span class="self-face" class:hurt={selfHit > 0}>
          {#if selfId > 0}<PlayerAvatar playerId={selfId} size="md" />{/if}
          {#if partySize > 1}<span class="party-count" title="Champions in the party">&times;{partySize}</span>{/if}
        </span>
      {/key}
      <div class="self-bar">
        <span class="who">{partySize > 1 ? `Your party of ${partySize}` : 'You'}</span>
        <Bar value={selfHp} max={100} color="var(--good, #4caf50)" label={`${Math.ceil(selfHp)}%`} />
      </div>
    </div>

    {#if banner}
      {#key banner.key}
        <div class="banner" data-kind={banner.kind}>{banner.text}</div>
      {/key}
    {/if}

    {#if introBoss}
      <div class="intro">
        <img src={BOSS_ART[dungeon.Key]} alt="" />
        <strong>{dungeon.Boss}</strong>
        <span>{events.find((e) => e.Kind === 'boss')?.Text}</span>
      </div>
    {/if}

    {#if telegraph}
      <!-- The telegraph: swipe the arrows in order (touch), or use the arrow
           keys / WASD; the pad is there for a mouse. -->
      <!-- Modul: ONE SWIPE AT A TIME (owner, 2026-10-31): no buttons and no
           sequence shown ahead - only the swipe due now, as an arrow that
           moves the way to swipe and its words, and dots for how many are
           left. A phone says "Swipe left"; a computer says WASD once. -->
      {@const due = telegraph.dirs[Math.min(entered.length, telegraph.dirs.length - 1)]}
      <div class="telegraph" class:wrong data-testid="dungeon-telegraph" data-dirs={telegraph.dirs.join(',')}>
        <strong class="tg-title">{isBoss ? `${dungeon.Boss} strikes!` : 'Dodge!'}</strong>
        {#key entered.length}
          <div class="due" data-dir={due.toLowerCase()} aria-live="assertive">
            <span class="due-arrow">{ARROW[due]}</span>
            <span class="due-word">{touch ? `Swipe ${due.toLowerCase()}` : due}</span>
          </div>
        {/key}
        {#if telegraph.dirs.length > 1}
          <ol class="steps" aria-label="{entered.length} of {telegraph.dirs.length}">
            {#each telegraph.dirs as _, i (i)}<li class:done={i < entered.length} class:now={i === entered.length}></li>{/each}
          </ol>
        {/if}
        <div class="clock"><i style="width: {timeLeft * 100}%"></i></div>
        {#if !touch}<p class="tg-hint">Use WASD or the arrow keys to dodge</p>{/if}
      </div>
    {/if}

    {#if offer}
      <div class="offer" data-testid="dungeon-offer">
        <strong class="tg-title">Clean dodge! Choose a blessing</strong>
        <div class="cards">
          {#each offer as p (p.Id)}
            <button class="card" data-testid="dungeon-perk" data-perk={p.Id} onclick={() => choose(p)}>
              <strong>{p.Name}</strong>
              <span>{p.Line}</span>
            </button>
          {/each}
        </div>
        <p class="tg-hint">It lasts until the end of this run.</p>
      </div>
    {/if}

    {#if showGate}
      <!-- Modul: the gate opens in CSS from the gate's own painting, split
           into two doors. It replaced a webm clip (owner, 2026-10-31): a
           video sometimes failed to load and left a grey video icon, and
           Safari on iOS does not play webm at all. An image already in the
           cache cannot fail that way. -->
      {#if gateMode === 'video'}
        <div class="gate-clip" class:fading={gateFading} data-testid="dungeon-gate-open">
          <video
            src={GATE_CLIP[dungeon.Key]}
            poster={GATE_ART[dungeon.Key]}
            autoplay
            muted
            playsinline
            preload="auto"
            disablepictureinpicture
            onplaying={() => (gateStarted = true)}
            onended={closeGate}
            onerror={gateFallback}
          ></video>
          <strong class="gate-title">{dungeon.Name}</strong>
        </div>
      {:else}
        <div class="gate-open" style="--gate-art: url('{GATE_ART[dungeon.Key]}')" data-testid="dungeon-gate-open">
          <div class="glow"></div>
          <div class="door left"></div>
          <div class="door right"></div>
          <strong class="gate-title">{dungeon.Name}</strong>
        </div>
      {/if}
    {/if}

    {#if finished && result}
      <div class="verdict" class:won={result.Won}>{result.Won ? 'Victory' : 'Defeat'}</div>
    {/if}
  </div>

  {#if finished && result}
    <div class="result" class:won={result.Won} data-testid="dungeon-result" data-piece-id={reward?.PieceId ?? 0}>
      <strong>{result.Won ? 'Victory' : 'Defeat'}</strong>
      <span class="dim small">
        {result.Won && result.Seconds === 0
          ? 'Swept: a stage you have beaten, paid without the fight'
          : result.Won
          ? `${dungeon.Boss} falls after ${result.Seconds.toFixed(0)} s of fighting`
          : result.ReachedBoss
            ? `${dungeon.Boss} had ${result.BossHpLeftPct}% left`
            : `You fell in wave ${result.WavesCleared + 1}`}
        &middot; {result.MealsEaten} meal{result.MealsEaten === 1 ? '' : 's'} eaten
        {#if result.Dodged + result.Missed > 0}&middot; dodged {result.Dodged} of {result.Dodged + result.Missed}{/if}
      </span>
      {#if reward}
        <ul class="reward">
          {#if reward.Xp > 0}<li>+{formatNumber(reward.Xp)} XP{result.Won ? '' : ' (a quarter, for trying)'}</li>{/if}
          {#if reward.Gold > 0}<li>+{formatNumber(reward.Gold)} gold{result.Won ? '' : ' (a quarter, for trying)'}</li>{/if}
          {#if reward.PieceBaseId}<li>{reward.PieceRarity} {prettifyBaseId(reward.PieceBaseId)}</li>{/if}
          {#if reward.Chest}<li>A {reward.Chest} loot chest</li>{/if}
          {#if reward.Diamonds > 0}<li>+{reward.Diamonds} diamonds for the first clear</li>{/if}
        </ul>
      {:else if !result.Won}
        <p class="dim small">Nothing this time - a lost run here pays a Bronze chest one time in four.</p>
      {/if}
      <div class="acts">
        {#if reward?.PieceId}<button onclick={() => requestScreen('chest')}>See the piece</button>{/if}
        {#if reward?.ChestId}<button onclick={() => requestScreen('chest')}>Open the chest</button>{/if}
        <button class="quiet" onclick={onclose}>Back to the gates</button>
      </div>
    </div>
  {/if}
</section>

<style>
  [data-tone='xp'] {
    --tone: #4fd1a5;
  }
  [data-tone='gold'] {
    --tone: #f0a640;
  }
  [data-tone='equipment'] {
    --tone: #b685f0;
  }
  .run-head {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 0.5rem;
  }
  h2 {
    margin: 0;
    font-size: 1rem;
  }
  .speed {
    display: flex;
    gap: 0.25rem;
  }
  .speed button {
    min-height: 44px;
    min-width: 44px;
    padding: 0 0.6rem;
    white-space: nowrap;
  }
  .speed button.on {
    background: var(--tone);
    color: #111;
  }

  .scene {
    position: relative;
    overflow: hidden;
    border-radius: 10px;
    border: 2px solid var(--tone);
    min-height: 22rem;
    display: grid;
    grid-template-rows: auto 1fr auto;
    background:
      linear-gradient(rgb(0 0 0 / 0.55), rgb(0 0 0 / 0.75)),
      var(--scene-bg) center / cover;
    color: #fff;
  }
  .pips {
    list-style: none;
    display: flex;
    justify-content: center;
    gap: 0.45rem;
    margin: 0;
    padding: 0.6rem;
  }
  .pips li {
    width: 14px;
    height: 14px;
    border-radius: 50%;
    border: 2px solid rgb(255 255 255 / 0.6);
  }
  .pips li.done {
    background: var(--tone);
    border-color: var(--tone);
  }
  .pips li.now {
    box-shadow: 0 0 10px 2px var(--tone);
    border-color: #fff;
  }
  .pips .skull {
    width: auto;
    height: auto;
    border: 0;
    font-size: 1.05rem;
    line-height: 14px;
    opacity: 0.75;
  }
  .pips .skull.now {
    box-shadow: none;
    color: var(--danger);
    opacity: 1;
    text-shadow: 0 0 10px var(--danger);
  }

  .enemy {
    display: grid;
    justify-items: center;
    align-content: center;
    gap: 0.4rem;
    padding: 0 1rem;
  }
  .shake {
    display: inline-block;
    animation: struck 0.22s ease-out;
  }
  .shake.dead {
    animation: fall 0.9s ease-in forwards;
  }
  .boss-art {
    width: min(13rem, 52vw);
    aspect-ratio: 1;
    object-fit: cover;
    border-radius: 50%;
    border: 3px solid var(--tone);
    box-shadow: 0 0 28px -4px var(--tone);
  }
  .enemy-bar,
  .self-bar {
    width: min(22rem, 100%);
    display: grid;
    gap: 0.15rem;
    position: relative;
  }
  .who {
    font-size: 0.78rem;
    font-weight: 700;
    text-shadow: 0 1px 2px #000;
  }
  .shield {
    height: 5px;
    border-radius: 3px;
    background: linear-gradient(90deg, #d8d2c0, #fff);
    box-shadow: 0 0 6px #fff;
  }
  .self {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.6rem 1rem 0.8rem;
    background: linear-gradient(transparent, rgb(0 0 0 / 0.6));
  }
  .self-face {
    display: inline-block;
  }
  .self-face.hurt {
    animation: hurt 0.25s ease-out;
  }

  .banner {
    position: absolute;
    left: 50%;
    top: 22%;
    transform: translateX(-50%);
    padding: 0.35rem 1rem;
    border-radius: 999px;
    font-weight: 800;
    letter-spacing: 0.03em;
    background: rgb(0 0 0 / 0.75);
    border: 1px solid var(--tone);
    color: var(--tone);
    text-shadow: 0 0 8px var(--tone);
    animation: pop 0.3s ease-out;
    white-space: nowrap;
    pointer-events: none;
    z-index: 4;
  }
  .banner[data-kind='meal'] {
    color: #9be08b;
    border-color: #9be08b;
    text-shadow: none;
  }
  .banner[data-kind='wave'] {
    color: #fff;
    border-color: rgb(255 255 255 / 0.5);
    text-shadow: none;
  }

  .intro {
    position: absolute;
    inset: 0;
    display: grid;
    place-content: center;
    justify-items: center;
    gap: 0.4rem;
    text-align: center;
    background: rgb(0 0 0 / 0.88);
    z-index: 5;
    animation: fadein 0.35s ease-out;
    padding: 1rem;
  }
  .intro img {
    width: min(11rem, 45vw);
    aspect-ratio: 1;
    border-radius: 50%;
    object-fit: cover;
    border: 3px solid var(--tone);
    animation: slidein 0.5s ease-out;
  }
  .intro strong {
    font-size: 1.3rem;
    color: var(--tone);
  }
  .intro span {
    font-size: 0.85rem;
    opacity: 0.9;
  }

  .gate-open {
    position: absolute;
    inset: 0;
    z-index: 6;
    background: #000;
    overflow: hidden;
    perspective: 900px;
    animation: gate-fade 2.4s ease-in forwards;
  }
  .gate-open .door {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 50%;
    background-image: var(--gate-art);
    background-size: 200% 100%;
    background-repeat: no-repeat;
    box-shadow: inset 0 0 40px rgb(0 0 0 / 0.6);
  }
  .gate-open .door.left {
    left: 0;
    background-position: left center;
    transform-origin: left center;
    animation: door-left 1.6s cubic-bezier(0.55, 0, 0.3, 1) 0.5s forwards;
  }
  .gate-open .door.right {
    right: 0;
    background-position: right center;
    transform-origin: right center;
    animation: door-right 1.6s cubic-bezier(0.55, 0, 0.3, 1) 0.5s forwards;
  }
  .gate-open .glow {
    position: absolute;
    inset: 0;
    background: radial-gradient(ellipse at center, #fff8e0 0%, #ffcf6a 18%, rgb(255 140 40 / 0.5) 40%, transparent 70%);
    opacity: 0;
    transform: scale(0.3);
    animation: glow 1.9s ease-out 0.5s forwards;
  }
  .gate-title {
    position: absolute;
    left: 0;
    right: 0;
    bottom: 12%;
    text-align: center;
    color: #fff;
    font-size: 1.2rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    text-shadow: 0 2px 8px #000;
    animation: title 2.4s ease forwards;
  }
  @keyframes door-left {
    to { transform: rotateY(-100deg); }
  }
  @keyframes door-right {
    to { transform: rotateY(100deg); }
  }
  @keyframes glow {
    40% { opacity: 1; }
    to { opacity: 1; transform: scale(2.4); }
  }
  @keyframes title {
    0% { opacity: 0; transform: translateY(8px); }
    15%, 55% { opacity: 1; transform: none; }
    to { opacity: 0; }
  }
  @keyframes gate-fade {
    0%, 80% { opacity: 1; }
    to { opacity: 0; }
  }
  @media (prefers-reduced-motion: reduce) {
    .gate-open, .gate-open * { animation-duration: 0.01s !important; animation-delay: 0s !important; }
  }

  .verdict {
    position: absolute;
    left: 50%;
    top: 40%;
    transform: translate(-50%, -50%);
    font-size: 2.2rem;
    font-weight: 900;
    letter-spacing: 0.06em;
    color: var(--danger);
    text-shadow: 0 2px 12px #000;
    animation: verdict 0.4s ease-out;
    pointer-events: none;
    z-index: 4;
  }
  .verdict.won {
    color: var(--accent);
  }

  .result {
    display: grid;
    gap: 0.35rem;
    padding-top: 0.7rem;
  }
  .result strong {
    font-size: 1.1rem;
  }
  .result.won strong {
    color: var(--accent);
  }
  .small {
    font-size: 0.82rem;
  }
  .reward {
    margin: 0;
    padding-left: 1.1rem;
    font-size: 0.88rem;
  }
  .acts {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
  }
  .acts button {
    min-height: 44px;
  }

  @keyframes struck {
    0% { transform: translateX(0); filter: brightness(1.8); }
    35% { transform: translateX(7px); }
    70% { transform: translateX(-4px); }
    100% { transform: translateX(0); filter: none; }
  }
  @keyframes hurt {
    0% { transform: translateX(0); filter: drop-shadow(0 0 6px red); }
    50% { transform: translateX(-5px); }
    100% { transform: translateX(0); filter: none; }
  }
  @keyframes fall {
    to { transform: translateY(18px) scale(0.9); opacity: 0.25; filter: grayscale(1); }
  }
  @keyframes pop {
    from { transform: translate(-50%, 0) scale(0.7); opacity: 0; }
    to { transform: translate(-50%, 0) scale(1); opacity: 1; }
  }
  @keyframes verdict {
    from { transform: translate(-50%, -50%) scale(0.6); opacity: 0; }
  }
  @keyframes fadein {
    from { opacity: 0; }
  }
  @keyframes slidein {
    from { transform: translateX(40px); opacity: 0; }
  }
  @media (prefers-reduced-motion: reduce) {
    .shake, .self-face.hurt, .banner, .intro, .intro img, .verdict { animation: none; }
  }
  .gate-clip {
    position: absolute;
    inset: 0;
    z-index: 6;
    background: #000;
    transition: opacity 0.4s ease;
  }
  .gate-clip.fading {
    opacity: 0;
  }
  .gate-clip video {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  /* While a telegraph is up a swipe is an answer, not a scroll. */
  .scene.tg {
    touch-action: none;
  }
  .taken {
    position: absolute;
    top: 2.1rem;
    left: 0.4rem;
    right: 0.4rem;
    z-index: 3;
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
    margin: 0;
    padding: 0;
    list-style: none;
    pointer-events: none;
  }
  .taken li {
    font-size: 0.62rem;
    font-weight: 700;
    padding: 0.1rem 0.4rem;
    border-radius: 999px;
    background: rgb(20 60 40 / 0.85);
    color: #bff5d8;
    border: 1px solid #4fd1a5;
  }
  .taken li.bad {
    background: rgb(70 20 20 / 0.85);
    color: #ffc9c9;
    border-color: var(--danger);
  }
  .party-count {
    position: absolute;
    right: -6px;
    bottom: -4px;
    font-size: 0.7rem;
    font-weight: 800;
    background: var(--tone);
    color: #111;
    border-radius: 999px;
    padding: 0 0.3rem;
  }
  .self-face {
    position: relative;
  }
  .banner[data-kind='dodged'],
  .banner[data-kind='perk'] {
    background: rgb(20 80 50 / 0.9);
    border-color: #4fd1a5;
  }
  .banner[data-kind='missed'],
  .banner[data-kind='penalty'] {
    background: rgb(100 20 20 / 0.92);
    border-color: var(--danger);
  }
  .telegraph,
  .offer {
    position: absolute;
    inset: 0;
    z-index: 7;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.6rem;
    padding: 0.8rem;
    background: radial-gradient(ellipse at center, rgb(0 0 0 / 0.55), rgb(0 0 0 / 0.85));
    color: #fff;
    text-align: center;
  }
  .telegraph.wrong {
    background: radial-gradient(ellipse at center, rgb(90 0 0 / 0.6), rgb(0 0 0 / 0.9));
  }
  .tg-title {
    font-size: 1.15rem;
    letter-spacing: 0.04em;
    text-shadow: 0 2px 6px #000;
  }
  .tg-hint {
    margin: 0;
    font-size: 0.7rem;
    opacity: 0.8;
  }
  .due {
    display: grid;
    justify-items: center;
    gap: 0.2rem;
    animation: due-in 0.18s ease-out;
  }
  .due-arrow {
    font-size: 4.5rem;
    line-height: 1;
    font-weight: 900;
    color: var(--tone);
    text-shadow: 0 0 18px var(--tone), 0 2px 4px #000;
  }
  .due[data-dir='up'] .due-arrow { animation: nudge-up 0.55s ease-in-out infinite; }
  .due[data-dir='down'] .due-arrow { animation: nudge-down 0.55s ease-in-out infinite; }
  .due[data-dir='left'] .due-arrow { animation: nudge-left 0.55s ease-in-out infinite; }
  .due[data-dir='right'] .due-arrow { animation: nudge-right 0.55s ease-in-out infinite; }
  @keyframes nudge-up { 50% { transform: translateY(-18px); } }
  @keyframes nudge-down { 50% { transform: translateY(18px); } }
  @keyframes nudge-left { 50% { transform: translateX(-18px); } }
  @keyframes nudge-right { 50% { transform: translateX(18px); } }
  @keyframes due-in {
    from { opacity: 0; transform: scale(0.8); }
  }
  .due-word {
    font-size: 1.1rem;
    font-weight: 800;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    text-shadow: 0 2px 6px #000;
  }
  .steps {
    display: flex;
    gap: 6px;
    margin: 0;
    padding: 0;
    list-style: none;
  }
  .steps li {
    width: 9px;
    height: 9px;
    border-radius: 50%;
    border: 1px solid rgb(255 255 255 / 0.6);
  }
  .steps li.done {
    background: #4fd1a5;
    border-color: #4fd1a5;
  }
  .steps li.now {
    border-color: var(--tone);
    box-shadow: 0 0 6px var(--tone);
  }
  @media (prefers-reduced-motion: reduce) {
    .due-arrow { animation: none !important; }
  }
  .clock {
    width: min(16rem, 80%);
    height: 6px;
    border-radius: 3px;
    background: rgb(255 255 255 / 0.15);
    overflow: hidden;
  }
  .clock i {
    display: block;
    height: 100%;
    background: var(--tone);
  }
  .cards {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 10rem));
    gap: 0.6rem;
    width: 100%;
    justify-content: center;
  }
  .card {
    display: grid;
    align-content: start;
    gap: 0.35rem;
    min-height: 8rem;
    padding: 0.7rem 0.6rem;
    border: 2px solid var(--tone);
    border-radius: 10px;
    background: linear-gradient(to bottom, rgb(40 30 20 / 0.95), rgb(15 10 5 / 0.95));
    color: #fff;
    text-align: left;
    white-space: normal;
    animation: card-in 0.3s ease-out both;
  }
  .card:nth-child(2) {
    animation-delay: 0.08s;
  }
  .card strong {
    font-size: 0.95rem;
    color: var(--tone);
  }
  .card span {
    font-size: 0.75rem;
    line-height: 1.3;
  }
  .card:hover,
  .card:focus-visible {
    transform: translateY(-3px);
    box-shadow: 0 0 16px var(--tone);
  }
  @keyframes card-in {
    from {
      opacity: 0;
      transform: translateY(12px) scale(0.96);
    }
  }
</style>
