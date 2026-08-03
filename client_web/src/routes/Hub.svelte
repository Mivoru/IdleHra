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
    { key: 'worldboss', label: 'World Boss', x: 83.0, y: 81.5 },
  ];

  const scene = backgroundUrl('main_hub');
  const plate = backgroundUrl('button_round');
</script>

<div class="hub">
  <div class="scene" style="background-image: url('{scene}')">
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
    /* The painting's own proportions, so the plates stay on their landmarks. */
    aspect-ratio: 1920 / 1072;
    background-size: cover;
    background-position: center;
    border-radius: var(--radius, 8px);
    overflow: hidden;
  }

  .place {
    position: absolute;
    transform: translate(-50%, -50%);
    width: 12.5%;
    aspect-ratio: 512 / 356;
    min-width: 4.5rem;
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
    transition: opacity 120ms ease, transform 120ms ease;
  }

  .place:hover,
  .place:focus-visible {
    opacity: 1;
    transform: translate(-50%, -50%) scale(1.04);
  }

  .place span {
    /* Black on wood, per the mock-up - and the only text on this screen that
       does NOT follow the app's light-on-dark theme, because it sits on a
       painted plank rather than on the page. */
    color: #17110a;
    font-weight: 800;
    /* Sized so the longest label (WORLD BOSS) still fits the plate's inner
       disc rather than running off the wood. */
    font-size: clamp(0.55rem, 0.85vw, 0.92rem);
    line-height: 1.05;
    text-align: center;
    letter-spacing: 0.01em;
    text-transform: uppercase;
    max-width: 76%;
    overflow-wrap: break-word;
  }

  @media (prefers-reduced-motion: reduce) {
    .place {
      transition: none;
    }

    .place:hover,
    .place:focus-visible {
      transform: translate(-50%, -50%);
    }
  }
</style>
