<script lang="ts">
  // Modul: THE CHEST OPENING (owner, 2026-10-08). A full-screen stage in the
  // dungeon: the chest stands on the floor, the player taps it three times -
  // each tap shakes it - and on the third it bursts open in the colour of what
  // came out, then the prize is shown with Wear / Open another / Close.
  //
  // The rules live in chestOpening.ts (tested); this file is the stage. The
  // OPEN is sent on the third tap, not when the window opens, so closing the
  // window before then spends nothing. The burst waits for both the third
  // shake to finish and the server to answer, whichever is last, and the clip
  // is the RESULT's rarity, not the chest's.
  //
  // Same layer discipline as Modal.svelte (portalled, app root inert, focus
  // trapped, Escape/back through the overlay stack) without its card: this is
  // a whole screen, not a dialog box.
  import { onMount } from 'svelte';
  import { portal } from './portal';
  import { openModal } from './modalStack';
  import { registerOverlay } from '../stores/sheet';
  import { LAYER_Z } from '../net/backButton';
  import { volume, muted, pageActive, play } from './audio';
  import Avatar from './Avatar.svelte';
  import Burst from './Burst.svelte';
  import { COSMETIC_KIND, rarityClass, type OpenedCosmetic } from '../net/cosmetics';
  import {
    CHEST_BACKGROUND,
    CHEST_IDLE_IMAGE,
    OPEN_MS,
    SHAKE_MS,
    SHAKE_RATE,
    TAPS_TO_OPEN,
    chestClip,
    initialChestState,
    oddsLine,
    pressChest,
    tapWord,
    videoFlavour,
    type ChestOpeningState,
  } from './chestOpening';

  interface Props {
    chestRarity: number;
    /** Chests of this rarity still unopened, as the parent's view says now. */
    remaining: number;
    rarityNames: readonly string[];
    /** The server's per-mille odds row for this chest. */
    odds?: readonly number[];
    equippedAvatarId: string | null;
    equippedFrameId: string | null;
    raceId: number;
    female: boolean;
    /** Sends the open; null when the server refused (the parent says why). */
    open: () => Promise<OpenedCosmetic | null>;
    onWear: (opened: OpenedCosmetic) => void;
    onClose: () => void;
  }

  let { chestRarity, remaining, rarityNames, odds, equippedAvatarId, equippedFrameId, raceId, female, open, onWear, onClose }: Props = $props();

  let stage = $state<HTMLElement | null>(null);
  let inner = $state<HTMLElement | null>(null);
  let shakeEl = $state<HTMLVideoElement | null>(null);
  let openEl = $state<HTMLVideoElement | null>(null);

  let chest = $state<ChestOpeningState>(initialChestState());
  let opened = $state<OpenedCosmetic | null>(null);
  let finalShakeDone = $state(false);
  let openClipPlaying = $state(false);
  let worn = $state(false);
  let failed = $state('');
  // Restarts the CSS shake on the still (no-video path) for every tap.
  let cssShake = $state(0);
  let flash = $state(false);

  let flavour = $state<'webm' | 'mp4' | 'none'>('none');
  let coarse = $state(true);
  const timers: ReturnType<typeof setTimeout>[] = [];

  onMount(() => {
    const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
    const probe = document.createElement('video');
    flavour = reduced ? 'none' : videoFlavour((t) => probe.canPlayType(t), navigator.userAgent);
    coarse = matchMedia('(pointer: coarse)').matches;
    return () => timers.forEach(clearTimeout);
  });

  $effect(() => {
    if (!inner || !stage) return;
    return openModal(inner, stage);
  });

  $effect(() => registerOverlay(() => onClose(), LAYER_Z.modal));

  // The shake's crunch is the clip's own track; it follows the game's volume
  // and mute like every other effect, and stops when the player looks away.
  $effect(() => {
    if (!shakeEl) return;
    shakeEl.volume = $volume;
    shakeEl.muted = $muted || !$pageActive;
  });

  const shakeSrc = $derived(chestClip('shake')[flavour === 'mp4' ? 'mp4' : 'webm']);
  // Mounted as soon as the result is known, so it buffers during the last
  // shake and starts with no gap when that shake ends.
  const openSrc = $derived(opened ? chestClip(opened.Rarity as 1 | 2 | 3 | 4)[flavour === 'mp4' ? 'mp4' : 'webm'] : null);
  const word = $derived(tapWord(coarse));
  const chestName = $derived(rarityNames[chestRarity] ?? 'Common');
  const resultClass = $derived(opened ? rarityClass(opened.Rarity) : '');

  function later(fn: () => void, ms: number): void {
    timers.push(setTimeout(fn, ms));
  }

  function press(): void {
    const { next, sendOpen, shake } = pressChest(chest);
    if (!shake) return;
    chest = next;
    if (flavour !== 'none' && shakeEl) {
      shakeEl.currentTime = 0;
      shakeEl.playbackRate = SHAKE_RATE;
      shakeEl.play().catch(() => (flavour = 'none'));
    } else {
      cssShake++;
      play('buttonClick');
      if (sendOpen) later(onShakeEnded, 520);
    }
    if (sendOpen) {
      // Safety net: a clip that never reports `ended` must not hold the prize.
      later(onShakeEnded, SHAKE_MS / SHAKE_RATE + 600);
      open()
        .then((result) => {
          if (!result) {
            failed = 'The chest would not open. Nothing was used up.';
            chest = { ...chest, phase: 'failed' };
            return;
          }
          opened = result;
          maybeBurst();
        })
        .catch(() => {
          failed = 'The server did not answer. Nothing was used up - try again.';
          chest = { ...chest, phase: 'failed' };
        });
    }
  }

  function onShakeEnded(): void {
    if (chest.phase !== 'final-shake' || finalShakeDone) return;
    finalShakeDone = true;
    maybeBurst();
  }

  function maybeBurst(): void {
    if (chest.phase !== 'final-shake' || !finalShakeDone || !opened) return;
    chest = { ...chest, phase: 'opening' };
    play('chestShine');
    if (flavour === 'none') {
      flash = true;
      later(reveal, 650);
    } else {
      if (openEl) {
        openEl.currentTime = 0;
        openEl.play().catch(() => reveal());
      }
      later(reveal, OPEN_MS + 1500);
    }
  }

  function reveal(): void {
    if (chest.phase !== 'opening' || !opened) return;
    chest = { ...chest, phase: 'revealed' };
    play('lootRare', 1 + (opened.Rarity - 1) * 0.08);
  }

  function again(): void {
    timers.forEach(clearTimeout);
    timers.length = 0;
    chest = initialChestState();
    opened = null;
    finalShakeDone = false;
    openClipPlaying = false;
    worn = false;
    failed = '';
    flash = false;
    if (shakeEl) shakeEl.currentTime = 0;
  }

  function wear(): void {
    if (!opened || worn) return;
    onWear(opened);
    worn = true;
  }
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<div
  class="chest-stage"
  style:z-index={LAYER_Z.modal}
  style:--landscape="url({CHEST_BACKGROUND.landscape})"
  style:--portrait="url({CHEST_BACKGROUND.portrait})"
  bind:this={stage}
  use:portal
  data-testid="chest-opening"
  data-phase={chest.phase}
  data-flavour={flavour}
>
  <div
    class="inner {resultClass}"
    role="dialog"
    aria-modal="true"
    aria-label="Opening a {chestName} chest"
    tabindex="-1"
    bind:this={inner}
  >
    <header class="top">
      <div class="title">
        <span class="chip {rarityClass(chestRarity)}">{chestName} chest</span>
        {#if odds}<span class="odds">{oddsLine(odds, rarityNames)}</span>{/if}
      </div>
      <button class="close" aria-label="Close" data-testid="chest-close" onclick={onClose}>
        <!-- An icon, not the × glyph: the glyph sits on the font's baseline
             and read as off-centre in the ring. -->
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" fill="none" />
        </svg>
      </button>
    </header>

    <!-- Modul: THE SCENE IS THE PAINTING, NOT THE WINDOW (2026-10-08). The
         background is drawn `cover`, so how much of it is cut off depends on
         the window's shape - and a chest placed by the window drifted off the
         lit spot on the floor on a wide monitor. The scene box is sized
         exactly like `cover` (max of width-led and height-led) with the
         painting on it at 100%, so a position in % of the scene is a position
         on the painting, at any window size. -->
    <div class="scene">
    <!-- Modul: a div with a button's role, not a <button>. The global
         `button:disabled` style halves the opacity, and the box is disabled
         for the whole burst - so the chest played as glass. -->
    <div
      class="chest-hit"
      class:hidden-shake={openClipPlaying}
      class:locked={chest.phase !== 'waiting-taps'}
      role="button"
      tabindex={chest.phase === 'waiting-taps' ? 0 : -1}
      aria-disabled={chest.phase !== 'waiting-taps'}
      aria-label="{word} to shake the chest ({chest.taps} of {TAPS_TO_OPEN})"
      data-testid="chest-tap"
      onclick={press}
      onkeydown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          press();
        }
      }}
    >
      {#if flavour === 'none'}
        {#key cssShake}
          <img class="still" class:shaking={cssShake > 0} class:flash src={CHEST_IDLE_IMAGE} alt="" draggable="false" />
        {/key}
      {:else}
        <video
          class="clip"
          class:blend={flavour === 'mp4'}
          bind:this={shakeEl}
          src={shakeSrc}
          poster={CHEST_IDLE_IMAGE}
          preload="auto"
          playsinline
          onended={onShakeEnded}
          onerror={() => (flavour = 'none')}
        ></video>
        {#if openSrc}
          <video
            class="clip open"
            class:blend={flavour === 'mp4'}
            class:showing={openClipPlaying}
            bind:this={openEl}
            src={openSrc}
            preload="auto"
            muted
            playsinline
            onplaying={() => (openClipPlaying = true)}
            onended={reveal}
            onerror={reveal}
          ></video>
        {/if}
      {/if}
    </div>
    </div>

    {#if chest.phase === 'waiting-taps'}
      <div class="prompt" aria-hidden="true">
        {#key chest.taps}<span class="word">{word}</span>{/key}
        <span class="pips">
          {#each Array.from({ length: TAPS_TO_OPEN }) as _, i}
            <span class="pip" class:on={i < chest.taps}></span>
          {/each}
        </span>
      </div>
    {:else if chest.phase === 'opening'}
      <button class="skip" data-testid="chest-skip" onclick={reveal}>Skip</button>
    {/if}

    {#if chest.phase === 'revealed' && opened}
      <div class="reveal {resultClass}" data-testid="chest-reveal" role="status">
        {#if opened.Rarity >= 3}
          <span class="burst"><Burst count={opened.Rarity === 4 ? 18 : 12} reach={3.4} color="var(--c)" /></span>
        {/if}
        <p class="rarity-word">{rarityNames[opened.Rarity] ?? ''}!</p>
        <div class="portrait">
          {#if opened.Kind === COSMETIC_KIND.Avatar}
            <Avatar avatarId={opened.DefinitionId} frameId={equippedFrameId} size="lg" />
          {:else}
            <Avatar avatarId={equippedAvatarId} frameId={opened.DefinitionId} {raceId} {female} size="lg" />
          {/if}
        </div>
        <strong class="name" data-testid="chest-reveal-name">{opened.Name}</strong>
        <span class="kind">
          {rarityNames[opened.Rarity]} {opened.Kind === COSMETIC_KIND.Avatar ? 'avatar' : 'frame'}
          &middot; from a {chestName} chest
        </span>
        <div class="actions">
          <button class="primary" disabled={worn} onclick={wear} data-testid="chest-wear">{worn ? 'Worn' : 'Wear it'}</button>
          {#if remaining > 0}
            <button onclick={again} data-testid="chest-again">Open another ({remaining})</button>
          {/if}
          <button onclick={onClose}>Close</button>
        </div>
      </div>
    {:else if chest.phase === 'failed'}
      <div class="reveal failed" role="alert">
        <p>{failed}</p>
        <div class="actions"><button onclick={onClose}>Close</button></div>
      </div>
    {/if}
  </div>
</div>

<style>
  .chest-stage {
    /* The painting's shape, and where its lit floor spot is - the chest's
       base goes there. Portrait art for a portrait window. */
    --art-ratio: 1.7917; /* 2752 x 1536 */
    --floor: 78%;
    --chest-h: 62%;
    position: fixed;
    inset: 0;
    background: #0d0b09;
    color: #f3ece0;
    /* clip, not hidden: a hidden box can still be SCROLLED, and focusing the
       chest (which the scene may push past the window's edge) scrolled the
       whole stage 50px up on the first click. */
    overflow: clip;
    animation: stage-in 220ms ease-out;
  }

  .scene {
    position: absolute;
    left: 50%;
    top: 50%;
    width: max(100vw, calc(100dvh * var(--art-ratio)));
    height: max(100dvh, calc(100vw / var(--art-ratio)));
    transform: translate(-50%, -50%);
    background: var(--landscape) center / 100% 100% no-repeat;
  }

  /* Modul: this block MUST stay below the `.scene` rule. Same specificity,
     so whichever comes later wins - and the `background` shorthand above
     resets background-image. Placed first, phones got the LANDSCAPE painting
     squeezed into the portrait box (2026-10-09, reported from the APK). */
  @media (orientation: portrait) {
    .chest-stage {
      --art-ratio: 0.5581; /* 1080 x 1935 */
      --floor: 73%;
      /* Capped by the window: a portrait tablet crops the painting's sides
         and blows it up, and the chest with it. */
      --chest-h: min(68%, 64dvh);
    }
    .scene {
      background-image: var(--portrait);
    }
  }

  /* A soft dark rim, so the text at the top and the card at the bottom read
     on any part of the painting. */
  .scene::before {
    content: '';
    position: absolute;
    inset: 0;
    background:
      linear-gradient(to bottom, rgb(0 0 0 / 0.55), transparent 22%, transparent 70%, rgb(0 0 0 / 0.6)),
      radial-gradient(ellipse at 50% 70%, transparent 40%, rgb(0 0 0 / 0.35));
    pointer-events: none;
  }

  .inner {
    position: absolute;
    inset: 0;
    padding: calc(var(--safe-area-inset-top, env(safe-area-inset-top, 0px)) + 12px) 16px
      calc(var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)) + 16px);
    outline: none;
  }

  .common { --c: #b9b9b9; }
  .rare { --c: #4f9be0; }
  .epic { --c: #b77cf0; }
  .legendary { --c: #f0b52e; }

  .top {
    position: relative;
    z-index: 3;
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
  }

  .title {
    display: grid;
    gap: 4px;
    min-width: 0;
  }

  .chip {
    justify-self: start;
    padding: 4px 12px;
    border: 1px solid var(--c);
    border-radius: 999px;
    background: color-mix(in srgb, var(--c) 22%, rgb(0 0 0 / 0.55));
    font-weight: 700;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    font-size: 0.85rem;
  }

  .odds {
    font-size: 0.75rem;
    color: rgb(243 236 224 / 0.8);
    text-shadow: 0 1px 2px #000;
  }

  .close {
    flex: none;
    box-sizing: border-box;
    width: 44px;
    height: 44px;
    min-width: 44px;
    aspect-ratio: 1;
    display: grid;
    place-items: center;
    border-radius: 50%;
    border: 1px solid rgb(255 255 255 / 0.35);
    background: rgb(0 0 0 / 0.55);
    color: #fff;
    padding: 0;
    cursor: pointer;
  }

  .close svg {
    display: block;
  }

  /* The chest's box: the clips are 406x720, the chest sits at about 40-78%
     of that height, so this puts its base on the painted floor. */
  /* The clips are 406x720 and the chest's base sits at 66% of their height
     (measured), so this stands the chest on --floor of the painting. */
  .chest-hit {
    position: absolute;
    left: 50%;
    top: calc(var(--floor) - 0.66 * var(--chest-h));
    height: var(--chest-h);
    aspect-ratio: 406 / 720;
    transform: translateX(-50%);
    padding: 0;
    border: 0;
    background: none;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
    /* The source's light rays end in a hard edge at the strip's sides and
       top; fade them out. On the BOX, not on the <video>: Chrome draws a
       masked video that is PLAYING with its alpha applied twice, and the
       chest turned to glass for the whole burst (paused frames were fine). */
    -webkit-mask-image: linear-gradient(to right, transparent, #000 7%, #000 93%, transparent),
      linear-gradient(to bottom, transparent, #000 10%);
    -webkit-mask-composite: source-in;
    mask-image: linear-gradient(to right, transparent, #000 7%, #000 93%, transparent),
      linear-gradient(to bottom, transparent, #000 10%);
    mask-composite: intersect;
  }

  .chest-hit.locked {
    cursor: default;
  }

  .chest-hit:focus-visible {
    outline: 2px dashed rgb(255 255 255 / 0.5);
    outline-offset: -20%;
  }

  .clip,
  .still {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: contain;
    pointer-events: none;
  }

  .clip.blend {
    mix-blend-mode: screen;
  }

  .clip.open {
    opacity: 0;
  }

  .clip.open.showing {
    opacity: 1;
  }

  .hidden-shake > .clip:not(.open) {
    opacity: 0;
  }

  .still.shaking {
    animation: css-shake 340ms ease-in-out;
  }

  .still.flash {
    animation: css-flash 650ms ease-out forwards;
  }

  .prompt {
    position: absolute;
    left: 50%;
    top: 22dvh;
    transform: translateX(-50%);
    display: grid;
    justify-items: center;
    gap: 10px;
    pointer-events: none;
    z-index: 2;
  }

  .word {
    font-size: clamp(2.2rem, 7vw, 3.6rem);
    font-weight: 900;
    letter-spacing: 0.08em;
    color: #fff4d6;
    text-shadow: 0 0 18px rgb(255 190 80 / 0.85), 0 3px 0 #5a3a12, 0 6px 14px rgb(0 0 0 / 0.7);
    animation: bob 1.1s ease-in-out infinite, pop 260ms ease-out;
  }

  .pips {
    display: flex;
    gap: 10px;
  }

  .pip {
    width: 14px;
    height: 14px;
    border-radius: 50%;
    border: 2px solid #fff4d6;
    background: rgb(0 0 0 / 0.4);
    box-shadow: 0 0 6px rgb(0 0 0 / 0.6);
    transition: background 150ms;
  }

  .pip.on {
    background: #ffc65a;
    box-shadow: 0 0 10px #ffb22e;
  }

  .skip {
    position: absolute;
    right: 16px;
    bottom: calc(var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)) + 16px);
    z-index: 3;
    min-height: 44px;
    padding: 0 18px;
    border-radius: 999px;
    border: 1px solid rgb(255 255 255 / 0.35);
    background: rgb(0 0 0 / 0.55);
    color: #fff;
  }

  .reveal {
    position: absolute;
    left: 50%;
    bottom: calc(var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)) + 16px);
    transform: translateX(-50%);
    z-index: 4;
    width: min(26rem, calc(100vw - 32px));
    display: grid;
    justify-items: center;
    gap: 6px;
    padding: 18px 16px 16px;
    border: 2px solid var(--c, #8a7a60);
    border-radius: 16px;
    background: linear-gradient(to bottom, color-mix(in srgb, var(--c, #8a7a60) 30%, #1a140e), #120e0a 70%);
    box-shadow: 0 0 40px color-mix(in srgb, var(--c, #000) 55%, transparent), 0 12px 30px rgb(0 0 0 / 0.7);
    text-align: center;
    animation: card-in 380ms cubic-bezier(0.2, 1.4, 0.4, 1);
  }

  .burst {
    position: absolute;
    top: 30%;
    left: 50%;
    pointer-events: none;
  }

  .rarity-word {
    margin: 0;
    font-size: 1.6rem;
    font-weight: 900;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--c);
    text-shadow: 0 0 14px color-mix(in srgb, var(--c) 70%, transparent);
  }

  .portrait {
    filter: drop-shadow(0 0 14px color-mix(in srgb, var(--c) 70%, transparent));
  }

  .name {
    font-size: 1.15rem;
    overflow-wrap: anywhere;
  }

  .kind {
    font-size: 0.8rem;
    color: rgb(243 236 224 / 0.75);
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 8px;
    margin-top: 8px;
  }

  .actions button {
    flex-shrink: 0;
    min-height: 44px;
    padding: 0 16px;
    border-radius: 10px;
    border: 1px solid rgb(255 255 255 / 0.25);
    background: rgb(255 255 255 / 0.08);
    color: #fff;
    font-weight: 600;
  }

  .actions .primary {
    border-color: var(--c);
    background: color-mix(in srgb, var(--c) 45%, #000);
  }

  .reveal.failed p {
    margin: 0;
  }

  @keyframes stage-in {
    from { opacity: 0; }
  }

  @keyframes bob {
    0%, 100% { transform: translateY(0); }
    50% { transform: translateY(-8px); }
  }

  @keyframes pop {
    from { scale: 1.35; }
  }

  @keyframes card-in {
    from { opacity: 0; transform: translate(-50%, 30px) scale(0.92); }
  }

  @keyframes css-shake {
    0%, 100% { transform: rotate(0); }
    20% { transform: rotate(-5deg); }
    40% { transform: rotate(4deg); }
    60% { transform: rotate(-3deg); }
    80% { transform: rotate(2deg); }
  }

  @keyframes css-flash {
    0% { filter: brightness(1); }
    40% { filter: brightness(3) drop-shadow(0 0 40px #ffd27a); }
    100% { filter: brightness(1.4); }
  }

  @media (prefers-reduced-motion: reduce) {
    .word,
    .reveal,
    .chest-stage {
      animation: none;
    }
  }
</style>
