<script lang="ts">
  // Modul: ONE CHEST RENDERER FOR BOTH KINDS OF CHEST (LOOT_DUNGEON_VISUALS.md
  // section 2). Two stills and the browser's compositor: the shake and the
  // burst are transforms and opacity (Web Animations API), the light is a
  // canvas with additive blending. Rays are radial and fade with distance,
  // so there is no box edge anywhere - the fault of the keyed video.
  //
  // The parent owns the rules (taps, the server's answer); this only draws.
  // `taps` going up shakes once per step, `opened` turning true bursts.
  import { onDestroy } from 'svelte';
  import {
    chestStills,
    clampRarity,
    particleCount,
    rayCount,
    shakeFor,
    BURST_MS,
    BURST_PEAK_MS,
    RARITY_LIGHT,
    type ChestArt,
  } from './chestStage';

  interface Props {
    art: ChestArt;
    rarity: number;
    taps: number;
    opened: boolean;
  }
  let { art, rarity, taps, opened }: Props = $props();

  const r = $derived(clampRarity(rarity));
  const stills = $derived(chestStills(art, r));

  let chestEl: HTMLDivElement | undefined = $state();
  let canvas: HTMLCanvasElement | undefined = $state();
  let showOpen = $state(false);
  let flash = $state(false);
  let lastTaps = 0;
  let raf = 0;

  const reduceMotion = () =>
    typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;

  $effect(() => {
    if (taps <= lastTaps || !chestEl) {
      lastTaps = taps;
      return;
    }
    lastTaps = taps;
    if (reduceMotion()) return;
    const { degrees, ms } = shakeFor(taps);
    // A shake that settles: out, back past centre, smaller, home.
    chestEl.animate(
      [
        { transform: 'rotate(0deg)' },
        { transform: `rotate(${-degrees}deg)` },
        { transform: `rotate(${degrees * 0.8}deg)` },
        { transform: `rotate(${-degrees * 0.45}deg)` },
        { transform: `rotate(${degrees * 0.2}deg)` },
        { transform: 'rotate(0deg)' },
      ],
      { duration: ms, easing: 'ease-out' },
    );
  });

  $effect(() => {
    if (!opened || showOpen) return;
    flash = true;
    // The swap sits under the flash's peak, so no frame shows the chest jump.
    const swap = setTimeout(() => (showOpen = true), BURST_PEAK_MS);
    const end = setTimeout(() => (flash = false), BURST_MS);
    startLight();
    return () => {
      clearTimeout(swap);
      clearTimeout(end);
    };
  });

  interface Spark { x: number; y: number; vx: number; vy: number; life: number; size: number }

  function startLight() {
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    // Soft gradients need no device pixels, and the canvas is 2.5x the stage:
    // at 2x DPR a phone would be filling a 36 MB bitmap every frame.
    const dpr = 1;
    const w = (canvas.width = canvas.clientWidth * dpr);
    const h = (canvas.height = canvas.clientHeight * dpr);
    const [cr, cg, cb] = RARITY_LIGHT[r];
    // Modul: THE CENTRE COMES FROM THE CHEST ON SCREEN, not from the canvas's
    // own box. Measured 2026-10-30 on the Chest screen: the rays rose about a
    // fifth of the stage to the LEFT of the chest, because the canvas's laid-out
    // box was not the 150% its CSS asks for inside the modal. Whatever the
    // layout does, the chest's rectangle is where the light belongs.
    const canvasBox = canvas.getBoundingClientRect();
    const chestBox = chestEl?.getBoundingClientRect() ?? canvasBox;
    const cx = (chestBox.left + chestBox.width / 2 - canvasBox.left) * dpr;
    const cy = (chestBox.top + chestBox.height * 0.45 - canvasBox.top) * dpr;
    const rays = rayCount(r);
    const sparks: Spark[] = [];
    const n = particleCount(r, reduceMotion(), navigator.hardwareConcurrency ?? 0);
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2;
      const v = (0.6 + Math.random() * 1.8) * dpr;
      sparks.push({ x: cx, y: cy, vx: Math.cos(a) * v, vy: Math.sin(a) * v - 1.2 * dpr, life: 1, size: (1 + Math.random() * 2.5) * dpr });
    }
    const started = performance.now();
    const frame = (now: number) => {
      const t = now - started;
      ctx.clearRect(0, 0, w, h);
      ctx.globalCompositeOperation = 'lighter';
      // Rays: radial gradients along thin wedges, slowly turning, fading in
      // over the burst and settling to a quiet glow behind the open chest.
      const strength = Math.min(1, t / BURST_MS) * (reduceMotion() ? 0.5 : 1);
      // Modul: THE LIGHT ENDS BEFORE THE CANVAS DOES (owner, 2026-11-19: "the
      // light is cut off"). Reach was max(w, h) x 0.85 for a Legendary, so
      // the wedges ran past the canvas edge still bright and stopped on a
      // straight line. Now they fade out inside the nearest edge, and a CSS
      // mask softens whatever is left.
      const room = Math.min(cx, w - cx, cy, h - cy);
      const reach = Math.max(1, room * (0.8 + 0.05 * r));
      for (let i = 0; i < rays; i++) {
        const a = (i / rays) * Math.PI * 2 + t / 6000;
        const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, reach);
        // Gentler than the first pass, which washed a small stage out to white.
        g.addColorStop(0, `rgba(${cr},${cg},${cb},${0.36 * strength})`);
        g.addColorStop(0.3, `rgba(${cr},${cg},${cb},${0.15 * strength})`);
        g.addColorStop(1, `rgba(${cr},${cg},${cb},0)`);
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.moveTo(cx, cy);
        ctx.arc(cx, cy, reach, a - 0.07, a + 0.07);
        ctx.closePath();
        ctx.fill();
      }
      for (const s of sparks) {
        if (s.life <= 0) continue;
        s.x += s.vx;
        s.y += s.vy;
        s.vy += 0.02 * dpr;
        s.life -= 0.008;
        ctx.fillStyle = `rgba(${cr},${cg},${cb},${Math.max(0, s.life)})`;
        ctx.beginPath();
        ctx.arc(s.x, s.y, s.size, 0, Math.PI * 2);
        ctx.fill();
      }
      raf = requestAnimationFrame(frame);
    };
    cancelAnimationFrame(raf);
    raf = requestAnimationFrame(frame);
  }

  onDestroy(() => cancelAnimationFrame(raf));
</script>

<div class="stage" data-testid="chest-stage" data-rarity={r} data-open={showOpen}>
  {#if !reduceMotion()}
    <!-- Ambience: dust in a shaft of light, the owner's Veo loop (5 s, its end
         cross-faded into its start so the loop has no seam). Light only on
         black, drawn with `screen`, behind the chest. -->
    <video class="dust" autoplay muted loop playsinline aria-hidden="true">
      <source src="/fx/vault-dust.webm" type="video/webm" />
      <source src="/fx/vault-dust.mp4" type="video/mp4" />
    </video>
  {/if}
  <canvas bind:this={canvas} class="light" aria-hidden="true"></canvas>
  <div class="chest" bind:this={chestEl}>
    <img src={showOpen ? stills.open : stills.closed} alt={showOpen ? 'An open chest' : 'A closed chest'} draggable="false" />
  </div>
  {#if showOpen && r === 4 && !reduceMotion()}
    <!-- Modul: THE ONE VIDEO LEFT ON THE CHEST (2026-10-30). Light only on
         pure black, drawn with `screen`, so black is see-through by itself and
         there is no keyed edge to shimmer. Only the Legendary/Ancient burst
         gets it; every other light on the stage is the canvas. -->
    <video class="fx" autoplay muted playsinline aria-hidden="true" onended={(e) => ((e.currentTarget as HTMLVideoElement).style.opacity = '0')}>
      <source src="/fx/legendary-burst.webm" type="video/webm" />
      <source src="/fx/legendary-burst.mp4" type="video/mp4" />
    </video>
  {/if}
  {#if flash}
    <div class="flash" style={`--light: rgb(${RARITY_LIGHT[r].join(',')})`} aria-hidden="true"></div>
  {/if}
</div>

<style>
  .stage {
    position: relative;
    width: 100%;
    aspect-ratio: 1;
    display: grid;
    place-items: end center;
  }
  /* Modul: NO EDGE ON THE SHAFT OF LIGHT (owner, 2026-11-19: "the light
     from above is cut off on the Wardrobe chest"). The dust loop's box began
     20% above the chest, so the beam from the ceiling stopped on a straight
     line in mid-air, and its sides were the video's own edges. Masked on all
     four sides now, it fades in from nothing. */
  .dust {
    position: absolute;
    inset: -20% -45% 0;
    width: 190%;
    height: 120%;
    object-fit: cover;
    z-index: 0;
    pointer-events: none;
    mix-blend-mode: screen;
    opacity: 0.85;
    -webkit-mask-image: linear-gradient(to bottom, transparent 0%, #000 35%, #000 82%, transparent 100%),
      linear-gradient(to right, transparent 8%, #000 30%, #000 70%, transparent 92%);
    -webkit-mask-composite: source-in;
    mask-image: linear-gradient(to bottom, transparent 0%, #000 35%, #000 82%, transparent 100%),
      linear-gradient(to right, transparent 8%, #000 30%, #000 70%, transparent 92%);
    mask-composite: intersect;
  }
  /* max-width: none - app.css caps every canvas at 100% of its box, which
     held this one to the stage's width while its height grew: the light sat
     off to the left and its wedges stopped on the canvas's edge (the 2026-10-30
     "rays to the left" measurement was this cap, not the modal). */
  .light {
    position: absolute;
    inset: -75%;
    width: 250%;
    height: 250%;
    max-width: none;
    max-height: none;
    pointer-events: none;
    z-index: 0;
    -webkit-mask-image: radial-gradient(closest-side, #000 60%, transparent 100%);
    mask-image: radial-gradient(closest-side, #000 60%, transparent 100%);
  }
  .chest {
    position: relative;
    z-index: 1;
    /* 68%: the OPEN still is taller by its lid, and at 78% the lid ran off
       the top of a square stage (seen on the loot shelf, 2026-10-30). */
    width: 68%;
    margin-bottom: 8%;
    transform-origin: 50% 90%;
  }
  /* Both stills are anchored at the base and drawn at one width, so the
     body stays put when the open one, taller by its lid, replaces it. */
  .chest img {
    display: block;
    width: 100%;
    height: auto;
    user-select: none;
  }
  .chest::after {
    /* The floor shadow the art no longer carries. */
    content: '';
    position: absolute;
    left: 10%;
    right: 10%;
    bottom: -4%;
    height: 10%;
    background: radial-gradient(closest-side, rgba(0, 0, 0, 0.45), transparent);
    z-index: -1;
  }
  /* The Legendary burst clip's black is not quite black, so `screen` left a
     grey rectangle the size of the video over the whole scene (seen
     2026-11-19). Contrast crushes the near-black to black, and a radial mask
     takes the box edge away. */
  .fx {
    position: absolute;
    inset: -30% -40%;
    width: 180%;
    height: 160%;
    object-fit: cover;
    z-index: 3;
    pointer-events: none;
    mix-blend-mode: screen;
    filter: contrast(1.35) brightness(0.9);
    -webkit-mask-image: radial-gradient(closest-side, #000 45%, transparent 100%);
    mask-image: radial-gradient(closest-side, #000 45%, transparent 100%);
    transition: opacity 0.4s;
  }
  .flash {
    position: absolute;
    inset: 0;
    z-index: 2;
    pointer-events: none;
    background: radial-gradient(circle at 50% 60%, var(--light) 0%, transparent 60%);
    mix-blend-mode: screen;
    animation: burst 0.9s ease-out forwards;
  }
  @keyframes burst {
    0% { opacity: 0; transform: scale(0.4); }
    20% { opacity: 1; transform: scale(1); }
    100% { opacity: 0; transform: scale(1.3); }
  }
  @media (prefers-reduced-motion: reduce) {
    .flash { animation-duration: 0.3s; }
  }
</style>
