<script lang="ts">
  // Modul: THE RUN, PLAYED BACK AS A FIGHT (owner, 2026-10-31: "not a text
  // log"). The server has already decided everything; its events carry both
  // bars in percent at every step, so the scene animates the bars BETWEEN
  // events from those numbers and never infers health from text or from a
  // difference (the combat feed rule). The enemy shakes when its bar drops,
  // the champion when theirs does. Banners name what happened. 1x, 2x and
  // Skip change only how fast the answer is shown - the outcome was fixed
  // before the first frame.
  import { onDestroy } from 'svelte';
  import { useQueryClient } from '@tanstack/svelte-query';
  import type { DungeonRunAnswer, DungeonInfo, DungeonEvent } from '../net/dungeon';
  import { prettifyBaseId } from '../net/content';
  import { requestScreen } from '../stores/navigation';
  import { playerState } from '../stores/game';
  import { play } from './audio';
  import { formatNumber } from './format';
  import { motionMs } from './motion';
  import MonsterPortrait from './MonsterPortrait.svelte';
  import PlayerAvatar from './PlayerAvatar.svelte';
  import Bar from './Bar.svelte';

  interface Props {
    answer: DungeonRunAnswer;
    dungeon: DungeonInfo;
    onclose: () => void;
  }
  let { answer, dungeon, onclose }: Props = $props();
  useQueryClient();

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

  const result = $derived(answer.Result!);
  const events = $derived(result.Events);
  const total = $derived(events.length > 0 ? events[events.length - 1].AtSeconds : 0);
  const selfId = $derived(Number($playerState?.PlayerId ?? 0));

  // Fight seconds per real second at 1x: a run of any length plays in about
  // fifteen seconds, never slower than real time.
  const baseRate = $derived(Math.max(1, total / 15));
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

  const isBoss = $derived(fight >= 5);
  const waveMonster = $derived(result.WaveMonsterIds?.[Math.min(fight, 4)] ?? 0);

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
      case 'victory':
      case 'defeat':
      case 'timeout':
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
    if (finished) return;
    finished = true;
    const last = events[events.length - 1];
    if (last) {
      enemyHp = last.EnemyHpPct;
      selfHp = last.PlayerHpPct;
    }
    play(result.Won ? 'chestOpenLegendary' : 'playerDied');
  }

  let raf = 0;
  let lastFrame = 0;
  function frame(now: number) {
    raf = requestAnimationFrame(frame);
    const dt = lastFrame ? Math.min(100, now - lastFrame) / 1000 : 0;
    lastFrame = now;
    if (finished || showGate) return;
    if (now < pausedUntil) return;
    simT = Math.min(total, simT + dt * baseRate * speed);
    while (index < events.length && events[index].AtSeconds <= simT) {
      reach(events[index]);
      index++;
      if (now < pausedUntil) break;
    }
    barsAt(simT);
    if (index >= events.length && simT >= total) finish();
  }

  function skip() {
    for (; index < events.length; index++) {
      const e = events[index];
      if (e.Kind === 'cleared') wavesCleared += 1;
      if (e.Kind === 'wave' || e.Kind === 'boss') fight = e.Kind === 'boss' ? 5 : e.Value - 1;
    }
    simT = total;
    introBoss = false;
    banner = null;
    showGate = false;
    finish();
  }

  // The gate opens first - the light clip over the gate's own painting.
  setTimeout(() => (showGate = false), motionMs(1600) || 0);
  raf = requestAnimationFrame(frame);
  onDestroy(() => cancelAnimationFrame(raf));

  const reward = $derived(answer.Reward);
</script>

<section class="panel run" data-testid="dungeon-run" data-tone={TONE[dungeon.Key]}>
  <header class="run-head">
    <h2>{dungeon.Name} &middot; stage {result.Stage}</h2>
    {#if !finished}
      <div class="speed" role="group" aria-label="Playback speed">
        <button class:on={speed === 1} onclick={() => (speed = 1)}>1x</button>
        <button class:on={speed === 2} onclick={() => (speed = 2)}>2x</button>
        <button onclick={skip} data-testid="dungeon-skip">Skip</button>
      </div>
    {/if}
  </header>

  <div class="scene" style="--scene-bg: url('{GATE_ART[dungeon.Key]}')">
    <ol class="pips" aria-label="Progress">
      {#each [0, 1, 2, 3, 4] as w (w)}
        <li class:done={w < wavesCleared} class:now={fight === w && !finished}></li>
      {/each}
      <li class="skull" class:now={isBoss && !finished} class:done={finished && result.Won}>&#9760;</li>
    </ol>

    <div class="enemy" class:boss={isBoss}>
      {#key enemyHit}
        <span class="shake" class:dead={finished && result.Won && isBoss}>
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
        </span>
      {/key}
      <div class="self-bar">
        <span class="who">You</span>
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

    {#if showGate}
      <div class="gate-open">
        <video src="/fx/dungeon-gate.webm" autoplay muted playsinline></video>
      </div>
    {/if}

    {#if finished}
      <div class="verdict" class:won={result.Won}>{result.Won ? 'Victory' : 'Defeat'}</div>
    {/if}
  </div>

  {#if finished}
    <div class="result" class:won={result.Won} data-testid="dungeon-result" data-piece-id={reward?.PieceId ?? 0}>
      <strong>{result.Won ? 'Victory' : 'Defeat'}</strong>
      <span class="dim small">
        {result.Won
          ? `${dungeon.Boss} falls after ${result.Seconds.toFixed(0)} s of fighting`
          : result.ReachedBoss
            ? `${dungeon.Boss} had ${result.BossHpLeftPct}% left`
            : `You fell in wave ${result.WavesCleared + 1}`}
        &middot; {result.MealsEaten} meal{result.MealsEaten === 1 ? '' : 's'} eaten
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
    display: grid;
    place-items: center;
  }
  .gate-open video {
    width: 100%;
    height: 100%;
    object-fit: cover;
    mix-blend-mode: screen;
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
</style>
