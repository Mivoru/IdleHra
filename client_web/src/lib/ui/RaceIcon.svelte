<script lang="ts">
  // Modul: a race, as a picture and a name.
  //
  // This is deliberately a RACE emblem, not a portrait of the player's own
  // character. The art ships a male and a female sheet per race, but nothing
  // on the wire or in any REST payload says which sex a character is - the
  // breeding roster carries level, age phase, generation and both race loci,
  // and stops there. Picking one sheet and presenting it as "your character"
  // would be a coin flip dressed as information.
  //
  // So it shows one consistent image per race and captions it with the race
  // NAME. That is true, useful (the race is otherwise a bare number nobody can
  // read), and does not claim anything the data cannot support.

  import { raceIcon } from './sprites';
  import { raceName } from './races';

  interface Props {
    raceId: number;
    size?: 'sm' | 'md' | 'xl';
    /** Show the race name beside the image. */
    label?: boolean;
  }

  const { raceId, size = 'sm', label = false }: Props = $props();

  const name = $derived(raceName(raceId));
  const url = $derived(raceIcon(raceId, false));
</script>

<span class="race" data-size={size} title={name}>
  {#if url}
    <img src={url} alt="" loading="lazy" decoding="async" />
  {:else}
    <span class="fallback" aria-hidden="true">{name.slice(0, 2)}</span>
  {/if}
  {#if label}<span class="name">{name}</span>{/if}
</span>

<style>
  .race {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.8rem;
  }

  img,
  .fallback {
    flex: none;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg);
    object-fit: contain;
    /* Modul: CENTRED, because the premise of the old rule was not true.
       It said `object-position: left center` with "the race sheets are wide
       (two figures side by side), so they are shown in a landscape frame" -
       which described the MASTER sheets, not what is actually served. The
       cleaned per-sex WebP files are single portrait figures: measured,
       Human_Male 451x512, Bes_Female 475x512, and every one of the twelve is
       taller than it is wide.
       So `contain` letterboxes them horizontally inside these landscape
       frames, and `left` then jammed every character against the left edge
       with the empty space piled on the right. Reported as exactly that, from
       a phone, about every race. `center` is also the CSS default, so this
       line now only exists to stop somebody re-deriving the old reasoning from
       the old comment. */
    object-position: center;
  }

  .race[data-size='sm'] img,
  .race[data-size='sm'] .fallback {
    width: 2.6rem;
    height: 1.8rem;
  }

  .race[data-size='md'] img,
  .race[data-size='md'] .fallback {
    width: 4.4rem;
    height: 3rem;
  }

  /* The paper doll's centrepiece: the character stands between two columns of
     equipment slots, so it has to be big enough to read as a figure rather
     than an icon. */
  .race[data-size='xl'] img,
  .race[data-size='xl'] .fallback {
    width: 100%;
    max-width: 13rem;
    height: auto;
    aspect-ratio: 3 / 2;
    object-fit: contain;
  }

  .fallback {
    display: grid;
    place-items: center;
    font-size: 0.62rem;
    font-weight: 700;
    color: var(--text-dim);
  }

  .name {
    white-space: nowrap;
  }
</style>
