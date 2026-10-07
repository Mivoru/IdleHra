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
  import QuestPanel from '../lib/ui/QuestPanel.svelte';
  import MonumentGlyph from '../lib/ui/MonumentGlyph.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchGreatWorks, greatWorksKeys } from '../lib/net/greatWorks';
  import { screenLocks } from '../lib/stores/tutorial';

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
    // One line now: the label is a ribbon UNDER the disc (task 109), so it no
    // longer has to fit inside the wood.
    { key: 'worldboss', label: 'World Boss', x: 83.0, y: 81.5 },
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

  <!-- Modul: THE QUEST LINE (owner, 2026-10-07) sits between the answers and
       the map: it is the one list that makes a player TRY the features. -->
  <QuestPanel />

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
      <!-- Task 109: a LOCKED place is shown, greyed, with its condition on
           the ribbon - the same rule the menu follows (ui/unlocks.ts) - rather
           than offered to a level-1 player as a place to go. -->
      {@const locked = $screenLocks(place.key)}
      <button
        class="place"
        class:locked={locked !== null}
        disabled={locked !== null}
        data-locked={locked ?? undefined}
        title={locked ? `Opens at: ${locked}` : undefined}
        style="left: {place.x}%; top: {place.y}%; background-image: url('{plate}')"
        onclick={() => onNavigate(place.key)}
      >
        <span>{place.label}{#if locked}<small> · {locked}</small>{/if}</span>
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
    padding: 0;
    border: none;
    background-color: transparent;
    background-size: contain;
    background-repeat: no-repeat;
    background-position: center;
    /* The plate reads better with the valley showing through it than as a
       solid disc. */
    opacity: 0.85;
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
    .place:not(:disabled):hover {
      opacity: 1;
      filter: brightness(1.08);
    }
  }

  /* Task 109: a place that is not open yet. Still on the map, so the player
     learns it exists; greyed, untappable, its condition on the ribbon. */
  .place.locked,
  .place.locked:disabled {
    opacity: 0.6;
    filter: grayscale(0.85);
    cursor: default;
  }

  /* Modul: TASK 109 - THE LABEL IS A RIBBON UNDER THE DISC, NOT TEXT IN IT.
     Sized against the plate (19cqw), the label came out at about 6.7px on a
     phone: a 44px disc cannot hold "MARKET" at a readable size. Under the
     disc the label has the whole width of the painting to use, so it is a
     fixed 11.2px and never has to shrink. mobile-check.mjs still asserts it
     does not wrap - nowrap is what holds that now. */
  .place span {
    position: absolute;
    top: calc(100% - 0.15rem);
    left: 50%;
    translate: -50% 0;
    padding: 0.05rem 0.45rem;
    border-radius: 3px;
    background: rgba(23, 17, 10, 0.82);
    border: 1px solid rgba(201, 162, 39, 0.55);
    color: #f4e6c4;
    font-size: 0.7rem;
    font-weight: 700;
    line-height: 1.3;
    letter-spacing: 0.03em;
    text-transform: uppercase;
    white-space: nowrap;
    pointer-events: none;
  }

  .place span small {
    font-size: inherit;
    font-weight: 600;
    text-transform: none;
    color: #d8c79f;
  }

  @media (prefers-reduced-motion: reduce) {
    .place {
      transition: none;
    }
  }
</style>
