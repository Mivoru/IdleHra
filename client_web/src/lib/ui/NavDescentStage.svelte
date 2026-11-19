<script lang="ts">
  // Modul: THE DESCENT AS A SCENE (owner, 2026-11-19: "graphics, animations,
  // transitions, sounds - a really good game experience"). The first replay
  // was a card in the page; going down is the mode's one moment, so it now
  // takes the screen: the Circle's own shaft scrolls past as the depths fall,
  // a rail on the left fills towards the record, each guardian arrives, is cut
  // down (or is not) and its Souls float up, a ribbon marks the depth that
  // beats the record, and the run ends on what it paid - chests, gold,
  // diamonds, tickets - drawn, not written.
  //
  // Nothing here decides anything: it is the server's run (NavRunResult and
  // NavLoot) shown at a pace. The pace is held to about six seconds whatever
  // the depth, Skip jumps to the result, and reduced motion opens on the
  // result at once.
  import type { NavRun, NavState, NavDepth } from '../net/nav';
  import { guardianArt } from '../net/nav';
  import MonsterPortrait from './MonsterPortrait.svelte';
  import Burst from './Burst.svelte';
  import { backgroundUrl } from './spriteUrl';
  import { currencyIcon } from './sprites';
  import { formatNumber } from './format';
  import { countUp } from './countUp.svelte';
  import { play } from './audio';
  import { prefersReducedMotion } from 'svelte/motion';
  import { onDestroy, onMount } from 'svelte';
  import Modal from './Modal.svelte';

  interface Props {
    run: NavRun;
    nav: NavState;
    onclose: () => void;
  }

  let { run, nav, onclose }: Props = $props();

  const CHEST_ART: Record<string, string> = {
    Bronze: '/chest/stage/loot-common-closed.webp',
    Silver: '/chest/stage/loot-rare-closed.webp',
    Gold: '/chest/stage/loot-epic-closed.webp',
    Ancient: '/chest/stage/loot-legendary-closed.webp',
  };

  // Three painted shafts, one per Circle, cycling past the third.
  const stageArt = $derived(backgroundUrl(`nav_stage_c${((Math.max(1, run.Circle) - 1) % 3) + 1}`));
  const recordBefore = $derived(run.RecordBefore ?? 0);
  const beatRecord = $derived(run.DeepestCleared > recordBefore && run.RecordBefore !== undefined);

  let phase = $state<'replay' | 'result'>('replay');
  let index = $state(0);
  let timer: ReturnType<typeof setTimeout> | null = null;
  let crossedAt = $state(-1);

  const depth = $derived<NavDepth | null>(phase === 'replay' ? (run.Depths[index] ?? null) : null);
  const guardian = $derived(depth ? nav.Guardians.find((g) => g.Key === depth.Guardian) : undefined);
  const souls = $derived(run.Depths.slice(0, index + 1).reduce((sum, d) => sum + (d.Cleared ? d.Souls : 0), 0));
  const soulsShown = countUp(() => (phase === 'result' ? run.Souls : souls));
  const stopper = $derived(nav.Guardians.find((g) => g.Key === run.StoppedBy));

  // The rail: how far down the shaft this depth is, against the deeper of the
  // record and the run itself.
  const railTop = $derived(Math.max(recordBefore, run.DeepestCleared + 1, 1));
  const railFill = $derived(depth ? Math.min(100, (depth.Depth / railTop) * 100) : 100);
  const railRecord = $derived(recordBefore > 0 ? Math.min(100, (recordBefore / railTop) * 100) : -1);
  // The shaft scrolls from its top to its bottom across the run.
  const scroll = $derived(run.Depths.length > 1 ? (index / (run.Depths.length - 1)) * 100 : 0);

  const loot = $derived(run.Loot ?? null);
  const chests = $derived(
    loot
      ? (['Ancient', 'Gold', 'Silver', 'Bronze'] as const)
          .map((tier) => ({ tier, n: loot[tier] }))
          .filter((c) => c.n > 0)
      : [],
  );

  function step(): number {
    return Math.max(160, Math.min(450, 6000 / Math.max(1, run.Depths.length)));
  }

  // One cue per depth, thinned when the depths go by fast so it never smears.
  function cue(i: number) {
    const d = run.Depths[i];
    if (!d) return;
    if (!d.Cleared) {
      play('navStopped');
      return;
    }
    if (d.Guardian === 'veles') {
      play('navVeles');
      return;
    }
    if (step() >= 260 || i % 2 === 0) play('navFall', 0.92 + (i % 3) * 0.06);
    if (crossedAt < 0 && run.RecordBefore !== undefined && d.Depth > recordBefore && recordBefore > 0) {
      crossedAt = i;
      play('navRecord');
    }
  }

  function showResult() {
    if (timer) clearTimeout(timer);
    timer = null;
    phase = 'result';
    if (loot) setTimeout(() => play('navReward'), 250);
    else if (beatRecord && crossedAt < 0) play('navRecord');
  }

  function advance() {
    if (index >= run.Depths.length - 1) {
      // Hold the last guardian - the one that stopped the run - a beat longer.
      timer = setTimeout(showResult, 1100);
      return;
    }
    index += 1;
    cue(index);
    timer = setTimeout(advance, step());
  }

  onMount(() => {
    // Every portrait the run will show, fetched before it starts: a new image
    // decoding mid-step left an empty ring for a whole depth at speed.
    for (const key of new Set(run.Depths.map((d) => d.Guardian))) {
      const img = new Image();
      img.src = guardianArt(key);
    }
    if (run.Depths.length === 0 || prefersReducedMotion.current) {
      showResult();
      return;
    }
    play('navDescend');
    cue(0);
    timer = setTimeout(advance, step());
  });

  onDestroy(() => {
    if (timer) clearTimeout(timer);
  });

  function close() {
    if (phase === 'replay') showResult();
    else onclose();
  }

</script>

<!-- Back (the phone's button, Escape) skips the replay, then closes the result;
     a tap on the dim area only closes the result. -->
<Modal label={`Descent into Circle ${run.Circle}`} onClose={close} dismissOnScrim={phase === 'result'} width="36rem" flush class="nav-stage-card">
  <div
    class="shell"
    class:fail={depth !== null && !depth.Cleared}
    class:warden={depth?.Guardian === 'veles'}
    style:--stage={`url(${stageArt})`}
    style:--scroll={`${scroll}%`}
  >
    <div class="bg" aria-hidden="true"></div>
    <div class="dust" aria-hidden="true">
      {#each Array.from({ length: 10 }) as _, i (i)}<span style:--i={i}></span>{/each}
    </div>
    <div class="vignette" aria-hidden="true"></div>

    {#if phase === 'replay' && depth}
      <section class="replay" data-testid="nav-replay">
        <div class="rail" aria-hidden="true">
          <span class="rail-fill" style:height={`${railFill}%`}></span>
          {#if railRecord >= 0}<span class="rail-record" style:top={`${railRecord}%`} title="Your record"></span>{/if}
        </div>

        <div class="centre">
          <span class="circle-tag">Circle {run.Circle}</span>
          {#key depth.Depth}
            <div class="depth-num">Depth <strong>{depth.Depth}</strong></div>
            <div class="foe" class:down={depth.Cleared} class:stood={!depth.Cleared}>
              <img class="foe-art" src={guardianArt(depth.Guardian)} alt={guardian?.Name ?? 'Guardian'} decoding="sync" />
              {#if depth.Cleared}<span class="slash" aria-hidden="true"></span>{/if}
              {#if depth.Cleared && depth.Souls > 0}<span class="float">+{formatNumber(depth.Souls)}</span>{/if}
            </div>
            <div class="foe-name">
              <strong>{guardian?.Name ?? ''}</strong>
              {#if depth.Elite}<span class="elite">{depth.Elite}</span>{/if}
            </div>
            <span class="ability">{guardian?.AbilityName ?? ''}</span>
            <div class="clock" aria-hidden="true">
              <span style:--fill={`${Math.min(100, (depth.Seconds / nav.SecondsPerGuardian) * 100)}%`}></span>
            </div>
            <span class="tiny verdict">{depth.Cleared ? `Down in ${depth.Seconds}s` : depth.Outcome}</span>
          {/key}
        </div>

        {#if crossedAt >= 0 && index >= crossedAt}
          <div class="ribbon" aria-live="polite">New record</div>
        {/if}

        <div class="foot">
          <span class="souls"><span class="soul-icon" aria-hidden="true"></span>+{formatNumber(Math.round(soulsShown.current))} Souls</span>
          <button class="ghost skip" onclick={showResult} data-testid="nav-skip">Skip</button>
        </div>
      </section>
    {:else}
      <section class="result" data-testid="nav-result">
        <span class="circle-tag">Circle {run.Circle}</span>
        <div class="result-depth" class:record={beatRecord}>
          {#if beatRecord}<span class="burst"><Burst count={16} reach={4.2} /></span>{/if}
          <span class="label">{beatRecord ? 'New record' : 'You reached'}</span>
          <span class="big">Depth {run.DeepestCleared}</span>
        </div>
        {#if stopper}
          <div class="stopped">
            <MonsterPortrait monsterId={0} name={stopper.Name} src={guardianArt(stopper.Key)} size="sm" />
            <span class="small">{stopper.Subject} stopped you at depth {run.DeepestCleared + 1}.</span>
          </div>
        {/if}

        <div class="haul">
          <div class="item" style:--d="0">
            <span class="soul-icon big-icon" aria-hidden="true"></span>
            <strong>+{formatNumber(Math.round(soulsShown.current))}</strong>
            <span class="tiny dim">Souls</span>
          </div>
          {#each chests as c, i (c.tier)}
            <div class="item chest chest-{c.tier.toLowerCase()}" style:--d={`${i + 1}`}>
              <img src={CHEST_ART[c.tier]} alt="" loading="eager" decoding="async" />
              <strong>{c.n > 1 ? `${c.n}x` : ''} {c.tier}</strong>
              <span class="tiny dim">chest{c.n === 1 ? '' : 's'}</span>
            </div>
          {/each}
          {#if loot && loot.GoldCoins > 0}
            <div class="item" style:--d={`${chests.length + 1}`}>
              <img class="coin" src={currencyIcon('gold') ?? ''} alt="" />
              <strong>{formatNumber(loot.GoldCoins)}</strong>
              <span class="tiny dim">gold</span>
            </div>
          {/if}
          {#if loot && loot.Diamonds > 0}
            <div class="item" style:--d={`${chests.length + 2}`}>
              <img class="coin" src={currencyIcon('diamond') ?? ''} alt="" />
              <strong>{loot.Diamonds}</strong>
              <span class="tiny dim">diamonds</span>
            </div>
          {/if}
          {#if loot && loot.Tickets > 0}
            <div class="item" style:--d={`${chests.length + 3}`}>
              <img class="coin" src="/dungeon/ticket-equipment.webp" alt="" />
              <strong>{loot.Tickets}</strong>
              <span class="tiny dim">dungeon ticket{loot.Tickets === 1 ? '' : 's'}</span>
            </div>
          {/if}
        </div>
        {#if chests.length > 0}
          <p class="tiny dim">The chests wait in your Village Chest.</p>
        {/if}
        <button class="primary" onclick={onclose} data-testid="nav-result-close">Continue</button>
      </section>
    {/if}
  </div>
</Modal>

<style>
  /* The Modal's card is only the frame here: the shell paints the scene. */
  :global(.modal-card.nav-stage-card) {
    background: #0b1214;
    border-color: rgb(120 200 190 / 35%);
    border-radius: 14px;
    overflow: hidden auto;
  }

  .shell {
    position: relative;
    min-height: min(30rem, calc(100dvh - 32px));
    border-radius: 14px;
    border: 1px solid rgb(120 200 190 / 35%);
    box-shadow: 0 0 0 1px rgb(0 0 0 / 60%), 0 20px 60px rgb(0 0 0 / 60%), inset 0 0 40px rgb(80 200 190 / 10%);
    color: #e8efe9;
    background: #0b1214;
    animation: drop 0.36s cubic-bezier(0.2, 0.9, 0.3, 1.2);
    transition: border-color 0.2s;
  }

  .shell.warden {
    border-color: rgb(232 193 58 / 70%);
    box-shadow: 0 0 0 1px rgb(0 0 0 / 60%), 0 20px 60px rgb(0 0 0 / 60%), inset 0 0 60px rgb(232 160 58 / 25%);
  }

  .shell.fail {
    border-color: rgb(224 85 63 / 80%);
    animation: shake 0.38s ease-in-out;
  }

  .bg {
    position: absolute;
    inset: 0;
    background-image: var(--stage);
    background-size: auto 220%;
    background-position: 50% var(--scroll, 0%);
    background-repeat: no-repeat;
    filter: saturate(0.9) brightness(0.62);
    transition: background-position 0.45s ease-out;
  }

  .vignette {
    position: absolute;
    inset: 0;
    pointer-events: none;
    background:
      radial-gradient(ellipse at 50% 42%, transparent 30%, rgb(4 8 10 / 75%) 100%),
      linear-gradient(180deg, rgb(4 8 10 / 50%), transparent 25%, transparent 70%, rgb(4 8 10 / 85%));
  }

  .shell.fail .vignette {
    background:
      radial-gradient(ellipse at 50% 42%, transparent 25%, rgb(90 10 6 / 70%) 100%),
      linear-gradient(180deg, rgb(4 8 10 / 50%), transparent 25%, transparent 70%, rgb(4 8 10 / 85%));
  }

  /* Motes drifting UP: the eye reads the camera going down. */
  .dust span {
    position: absolute;
    bottom: -8px;
    left: calc(6% + var(--i) * 9.3%);
    width: 3px;
    height: 3px;
    border-radius: 50%;
    background: rgb(150 240 220 / 70%);
    box-shadow: 0 0 6px rgb(120 240 220 / 80%);
    animation: mote calc(3.2s + var(--i) * 0.37s) linear infinite;
    animation-delay: calc(var(--i) * -0.61s);
  }

  .shell.warden .dust span {
    background: rgb(255 200 90 / 80%);
    box-shadow: 0 0 6px rgb(255 190 80 / 90%);
  }

  .replay,
  .result {
    position: relative;
    display: grid;
    min-height: inherit;
    padding: 1rem 1rem 0.9rem 2.6rem;
  }

  .result {
    padding: 1.2rem 1rem 1rem;
    justify-items: center;
    align-content: start;
    gap: 0.7rem;
    text-align: center;
  }

  .rail {
    position: absolute;
    left: 1rem;
    top: 1.2rem;
    bottom: 3.6rem;
    width: 6px;
    border-radius: 3px;
    background: rgb(255 255 255 / 12%);
  }

  .rail-fill {
    position: absolute;
    left: 0;
    top: 0;
    width: 100%;
    border-radius: 3px;
    background: linear-gradient(180deg, rgb(120 230 210 / 60%), rgb(120 230 210));
    box-shadow: 0 0 8px rgb(120 230 210 / 70%);
    transition: height 0.3s ease-out;
  }

  .rail-record {
    position: absolute;
    left: -5px;
    width: 16px;
    height: 2px;
    background: #e8c13a;
    box-shadow: 0 0 6px #e8c13a;
  }

  .centre {
    display: grid;
    justify-items: center;
    align-content: center;
    gap: 0.35rem;
    text-align: center;
  }

  .circle-tag {
    font-size: var(--fs-xs);
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: rgb(170 230 220 / 85%);
  }

  .depth-num {
    font-family: var(--font-display, serif);
    font-size: 1rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    animation: rise 0.22s ease-out;
  }

  .depth-num strong {
    font-size: 2.2rem;
    line-height: 1;
    margin-left: 0.25rem;
    text-shadow: 0 0 14px rgb(120 230 210 / 60%);
  }

  .shell.warden .depth-num strong {
    text-shadow: 0 0 18px rgb(255 190 80 / 85%);
    color: #ffd98a;
  }

  .foe {
    position: relative;
    margin: 0.3rem 0;
    animation: arrive 0.26s cubic-bezier(0.2, 0.9, 0.3, 1.3);
  }

  .foe-art {
    display: block;
    width: 8.5rem;
    height: 8.5rem;
    object-fit: cover;
    border-radius: 50%;
    box-shadow: 0 0 0 2px rgb(120 230 210 / 55%), 0 0 26px rgb(120 230 210 / 35%);
  }

  .shell.warden .foe-art {
    width: 10rem;
    height: 10rem;
    box-shadow: 0 0 0 2px rgb(255 200 90 / 80%), 0 0 40px rgb(255 170 60 / 55%);
  }

  .foe.stood .foe-art {
    box-shadow: 0 0 0 2px rgb(224 85 63 / 85%), 0 0 34px rgb(224 85 63 / 55%);
  }

  .foe.down .foe-art {
    animation: fall-away 0.4s ease-in 0.12s forwards;
  }

  .slash {
    position: absolute;
    inset: 35% -12%;
    background: linear-gradient(90deg, transparent, rgb(255 255 255 / 95%), transparent);
    transform: rotate(-28deg) scaleX(0);
    border-radius: 2px;
    filter: drop-shadow(0 0 6px rgb(160 255 240));
    animation: slash 0.24s ease-out 0.08s forwards;
  }

  .float {
    position: absolute;
    left: 50%;
    top: 10%;
    font-weight: 700;
    color: #9ff0de;
    text-shadow: 0 0 8px rgb(80 220 200 / 90%), 0 1px 2px #000;
    animation: float-up 0.9s ease-out forwards;
    pointer-events: none;
  }

  .foe-name {
    display: flex;
    gap: 0.4rem;
    align-items: center;
    font-size: 1.05rem;
  }

  .elite {
    font-size: var(--fs-xs);
    padding: 0 0.4rem;
    border: 1px solid #e0b050;
    border-radius: 999px;
    color: #f0c870;
  }

  .ability {
    font-weight: 600;
    color: #f0b860;
    font-size: var(--fs-sm, 0.9rem);
  }

  .clock {
    width: min(16rem, 80%);
    height: 6px;
    border-radius: 3px;
    background: rgb(255 255 255 / 14%);
    overflow: hidden;
  }

  .clock span {
    display: block;
    height: 100%;
    width: var(--fill);
    background: linear-gradient(90deg, #6fe8d8, #c8fff4);
    animation: fill 0.18s ease-out;
  }

  .shell.fail .clock span {
    background: linear-gradient(90deg, #b83a25, #ff7a5c);
  }

  .verdict {
    opacity: 0.85;
  }

  .ribbon {
    position: absolute;
    bottom: 3.7rem;
    left: 50%;
    padding: 0.25rem 1.2rem;
    transform: translateX(-50%);
    font-family: var(--font-display, serif);
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: #2a1a04;
    background: linear-gradient(90deg, #b8862a, #ffe08a, #b8862a);
    border-radius: 3px;
    box-shadow: 0 0 18px rgb(255 200 90 / 70%);
    animation: ribbon 0.5s cubic-bezier(0.2, 0.9, 0.3, 1.3);
  }

  .foot {
    display: flex;
    justify-content: space-between;
    align-items: center;
    align-self: end;
  }

  .souls {
    display: inline-flex;
    gap: 0.4rem;
    align-items: center;
    font-weight: 600;
    color: #9ff0de;
  }

  .soul-icon {
    display: inline-block;
    width: 0.8rem;
    height: 0.8rem;
    border-radius: 50% 50% 50% 50% / 60% 60% 40% 40%;
    background: radial-gradient(circle at 40% 35%, #e8fffa, #6fe8d8 45%, rgb(40 140 130 / 0%) 72%);
    box-shadow: 0 0 10px rgb(110 230 210 / 80%);
  }

  .soul-icon.big-icon {
    width: 2.4rem;
    height: 2.4rem;
  }

  .skip {
    color: #e8efe9;
    border-color: rgb(255 255 255 / 30%);
  }

  .result-depth {
    position: relative;
    display: grid;
    justify-items: center;
    animation: arrive 0.4s cubic-bezier(0.2, 0.9, 0.3, 1.3);
  }

  .result-depth .label {
    font-size: var(--fs-xs);
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: rgb(200 230 225 / 85%);
  }

  .result-depth.record .label {
    color: #ffd98a;
  }

  .result-depth .big {
    font-family: var(--font-display, serif);
    font-size: 2.4rem;
    line-height: 1.1;
    text-shadow: 0 0 16px rgb(120 230 210 / 55%);
  }

  .result-depth.record .big {
    color: #ffe08a;
    text-shadow: 0 0 20px rgb(255 190 80 / 80%);
  }

  .burst {
    position: absolute;
    left: 50%;
    top: 55%;
  }

  .stopped {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    padding: 0.3rem 0.7rem 0.3rem 0.3rem;
    border-radius: 999px;
    background: rgb(0 0 0 / 40%);
  }

  .haul {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 0.6rem;
    margin-top: 0.3rem;
  }

  .item {
    display: grid;
    justify-items: center;
    align-content: start;
    gap: 0.15rem;
    min-width: 5.2rem;
    padding: 0.55rem 0.5rem;
    border-radius: 10px;
    background: rgb(0 0 0 / 45%);
    border: 1px solid rgb(255 255 255 / 12%);
    opacity: 0;
    animation: pop 0.42s cubic-bezier(0.2, 0.9, 0.3, 1.4) forwards;
    animation-delay: calc(0.25s + var(--d) * 0.14s);
  }

  .item img {
    width: 3.2rem;
    height: 3.2rem;
    object-fit: contain;
  }

  .item img.coin {
    width: 2.4rem;
    height: 2.4rem;
  }

  .chest-gold {
    border-color: rgb(232 193 58 / 55%);
    box-shadow: 0 0 14px rgb(232 193 58 / 25%);
  }

  .chest-ancient {
    border-color: rgb(224 85 63 / 70%);
    box-shadow: 0 0 18px rgb(224 85 63 / 35%);
  }

  .chest-ancient img {
    filter: drop-shadow(0 0 10px rgb(255 120 80 / 70%));
  }

  .result .primary {
    margin-top: 0.4rem;
    min-width: 10rem;
  }

  @keyframes drop {
    from { opacity: 0; transform: translateY(-18px) scale(0.97); }
  }

  @keyframes shake {
    0%, 100% { transform: none; }
    20% { transform: translateX(-7px); }
    40% { transform: translateX(6px); }
    60% { transform: translateX(-4px); }
    80% { transform: translateX(2px); }
  }

  @keyframes mote {
    from { transform: translateY(0); opacity: 0; }
    15% { opacity: 1; }
    to { transform: translateY(-34rem); opacity: 0; }
  }

  @keyframes rise {
    from { transform: translateY(8px); }
  }

  @keyframes arrive {
    from { transform: translateY(-14px) scale(0.86); opacity: 0.4; }
  }

  @keyframes fall-away {
    to { transform: translateY(10px) scale(0.94); opacity: 0.55; filter: grayscale(0.6) brightness(0.75); }
  }

  @keyframes slash {
    from { transform: rotate(-28deg) scaleX(0); opacity: 1; }
    70% { transform: rotate(-28deg) scaleX(1); opacity: 1; }
    to { transform: rotate(-28deg) scaleX(1); opacity: 0; }
  }

  @keyframes float-up {
    from { transform: translate(-50%, 0); opacity: 0; }
    20% { opacity: 1; }
    to { transform: translate(-50%, -2.6rem); opacity: 0; }
  }

  @keyframes fill {
    from { width: 0; }
  }

  @keyframes ribbon {
    from { transform: translateX(-50%) scale(0.4); opacity: 0; }
  }

  @keyframes pop {
    from { opacity: 0; transform: translateY(10px) scale(0.7); }
    to { opacity: 1; transform: none; }
  }

  @media (prefers-reduced-motion: reduce) {
    .shell, .foe, .depth-num, .result-depth, .ribbon { animation: none; }
    .item { animation: none; opacity: 1; }
    .dust { display: none; }
    .bg { transition: none; }
  }
</style>
