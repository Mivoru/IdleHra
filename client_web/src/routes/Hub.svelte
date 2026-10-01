<script lang="ts">
  // Modul: the map IS the menu.
  //
  // Signing in landed the player on the Combat screen with a row of twenty-odd
  // nav words above it. This is the hub the art was drawn for: one painted
  // valley with five places in it, and you go somewhere by clicking the place.
  //
  // The button positions are percentages of the painting, not pixels - the
  // background scales with the viewport and a pixel offset would slide the
  // Guild plate off its castle the moment the window changed size.
  import { backgroundUrl } from '../lib/ui/sprites';
  import type { ScreenKey } from '../lib/ui/screens';
  import HomeCards from '../lib/ui/HomeCards.svelte';
  import MonumentGlyph from '../lib/ui/MonumentGlyph.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchGreatWorks, greatWorksKeys } from '../lib/net/greatWorks';

  interface Props {
    onNavigate: (screen: ScreenKey) => void;
  }

  const { onNavigate }: Props = $props();

  // Measured against the mock-up: each entry is the CENTRE of its plate as a
  // fraction of the painting's width and height.
  const PLACES: { key: ScreenKey; label: string; x: number; y: number }[] = [
    { key: 'combat', label: 'Combat', x: 29.5, y: 42.5 },
    { key: 'guildops', label: 'Guild', x: 78.5, y: 34.0 },
    { key: 'village', label: 'Village', x: 52.0, y: 54.5 },
    { key: 'market', label: 'Market', x: 27.8, y: 77.5 },
    // Two lines on purpose: WORLD over BOSS reads as one sign, where a single
    // line has to shrink to fit the disc and stops matching the others.
    { key: 'worldboss', label: 'World\nBoss', x: 83.0, y: 81.5 },
  ];

  const scene = backgroundUrl('main_hub');
  const plate = backgroundUrl('button_round');

  // Modul: TASK 84 - THE GREAT WORKS STAND IN THE VALLEY. Each monument is a
  // landmark on the map and grows a layer per built stage; a monument with no
  // stage built is not drawn at all, so the first deposit that completes a
  // stage is what visibly changes the Home map. Centres are percentages of the
  // painting like the plates above, picked on open grass away from every plate.
  // The stage count and names are the server's (the query below); only WHERE
  // each stands is a fact about the painting, so it lives here.
  const MONUMENT_SPOTS: Record<number, { x: number; y: number }> = {
    1: { x: 43, y: 32 },
    2: { x: 60, y: 24 },
    3: { x: 67, y: 67 },
    4: { x: 12, y: 48 },
    5: { x: 91, y: 58 },
  };

  const greatWorks = createQuery(() => ({
    queryKey: greatWorksKeys.all,
    queryFn: fetchGreatWorks,
    refetchInterval: 60_000,
    // Modul: a stage is built on the Village screen, and the next place the
    // player looks is this Map. The client-wide 30 s staleTime kept the old
    // answer across that step, so a freshly built monument was missing here.
    refetchOnMount: 'always',
  }));

  const built = $derived((greatWorks.data?.Works ?? []).filter((w) => w.Stage > 0 && MONUMENT_SPOTS[w.Region]));
</script>

<div class="hub">
  <!-- Modul: TASK 73 - THE ANSWER COMES BEFORE THE PAINTING. -->
  <HomeCards />

  <div class="scene" style="background-image: url('{scene}')">
    {#each built as work (work.Region)}
      <span
        class="monument-spot"
        data-testid="hub-monument-{work.Region}"
        data-stage={work.Stage}
        title="{work.Name} - stage {work.Stage} of {work.Stages.length}"
        style="left: {MONUMENT_SPOTS[work.Region].x}%; top: {MONUMENT_SPOTS[work.Region].y}%"
      >
        <MonumentGlyph stage={work.Stage} size="100%" label="{work.Name}, stage {work.Stage} of {work.Stages.length}" />
      </span>
    {/each}
    {#each PLACES as place (place.key)}
      <button
        class="place"
        style="left: {place.x}%; top: {place.y}%; background-image: url('{plate}')"
        onclick={() => onNavigate(place.key)}
      >
        <span>{place.label}</span>
      </button>
    {/each}
  </div>
</div>

<style>
  .hub {
    padding: 1rem;
  }

  .scene {
    position: relative;
    width: 100%;
    /* A strip under the cards, not the page: capped and centred so it stays
       about 350px tall on a desktop. The plates are percentages of THIS box,
       so shrinking it keeps them on their landmarks. */
    max-width: 40rem;
    margin: 1rem auto 0;
    /* The painting's own proportions, so the plates stay on their landmarks. */
    aspect-ratio: 1920 / 1072;
    background-size: cover;
    background-position: center;
    border-radius: var(--radius);
    overflow: hidden;
  }

  /* Task 84: a landmark, not a control - it never takes a tap, so it can sit
     anywhere on the painting without burying a plate. */
  .monument-spot {
    position: absolute;
    transform: translate(-50%, -50%);
    width: 7%;
    min-width: 1.6rem;
    aspect-ratio: 1;
    pointer-events: none;
    filter: drop-shadow(0 1px 2px rgba(0, 0, 0, 0.6));
  }

  .place {
    /* Makes cqw above measure THIS element. */
    container-type: inline-size;
    position: absolute;
    transform: translate(-50%, -50%);
    width: 10.5%;
    /* The plate's own proportions after the re-crop - very nearly square,
       because it is a disc. */
    aspect-ratio: 512 / 502;
    /* Modul: 4.5rem forced every plate to 72px on a 360px phone, where 10.5%
       of the painting is 36 - so the plates were double the size the map was
       drawn for and crowded over each other. The floor exists so they stay
       tappable, and 2.75rem (44px) is the size a thumb actually needs. */
    min-width: 2.75rem;
    display: grid;
    place-items: center;
    padding: 0;
    border: none;
    background-color: transparent;
    background-size: contain;
    background-repeat: no-repeat;
    background-position: center;
    /* 70%, as specified - the plate reads better with the valley showing
       through it than as a solid disc. */
    opacity: 0.7;
    cursor: pointer;
    /* Modul: opacity only. Hover used to also scale the plate, and a hovered
       element whose geometry is moving is never "stable" - every automated
       click retried until it timed out, and a real cursor made the label slide
       under itself. The brightness change is the whole affordance. */
    transition: opacity 120ms ease, filter 120ms ease;
  }

  .place:focus-visible {
    opacity: 1;
    filter: brightness(1.08);
  }

  @media (hover: hover) and (pointer: fine) {
    .place:hover {
      opacity: 1;
      filter: brightness(1.08);
    }
  }

  .place span {
    /* Black on wood, per the mock-up - and the only text on this screen that
       does NOT follow the app's light-on-dark theme, because it sits on a
       painted plank rather than on the page. */
    color: #17110a;
    font-weight: 800;
    /* Modul: SIZED AGAINST THE PLATE, not the window.
       This was `0.85vw`, which ties the label to the viewport - and once the
       plates were allowed to shrink on a phone the two stopped tracking each
       other: an 8.8px label on a 44px plate wrapped every word, so COMBAT read
       "COMBA / T" and MARKET read "MARKE / T".
       Container units measure the plate itself, which is the box the text has
       to fit, so the relationship holds at every size.

       19cqw is the largest that still fits: at 21 the labels wrap again -
       measured, not guessed, and mobile-check.mjs asserts it, because a label
       too big for its disc does not overflow the PAGE and so nothing else
       would ever catch it. */
    font-size: clamp(0.42rem, 19cqw, 0.92rem);
    line-height: 1.05;
    text-align: center;
    letter-spacing: 0.01em;
    text-transform: uppercase;
    /* A little more of the disc, now that the wood is smaller. */
    max-width: 90%;
    /* Modul: NEVER BREAK INSIDE A WORD. Since task 73 the map is a strip capped
       at 40rem, so the plates (10.5% of it) are smaller than the label was
       sized for, and break-word split COMBAT into "COMBA / T" and VILLAGE into
       "VILLAG / E". A word wider than its plate now overhangs the wood by a
       pixel or two (the span's overflow is visible) instead; only the space in
       a multi-word label ("World Boss") is a place to wrap. */
    overflow-wrap: normal;
    word-break: keep-all;
    hyphens: none;
    /* The label carries its own line breaks - "World\nBoss" is two lines by
       authorship, not by the box happening to be narrow. */
    white-space: pre-line;
  }

  @media (prefers-reduced-motion: reduce) {
    .place {
      transition: none;
    }
  }
</style>
