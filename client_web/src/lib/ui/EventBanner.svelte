<script lang="ts">
  // Modul: the active global event.
  //
  // LiveOpsTickEngine rotates one of four events on a fixed weekly schedule, so
  // there is ALWAYS one running - and until now this client showed none of
  // them. A player wondering why their gathering suddenly yields more had
  // nothing to look at.
  //
  // The names come from the shared localisation table rather than being
  // written here, because those five keys (EventNone plus the four events) are
  // most of what that table is for. The EFFECTS are not in the table and are
  // transcribed from the server below, each one from the line that applies it.

  import { playerState } from '../stores/game';
  import { t } from './i18n';
  import Hint from './Hint.svelte';

  /** ContentRegistry.GlobalEventType. */
  const EVENTS: Record<number, { key: string; effect: string; tone: string }> = {
    1: {
      key: 'EventGoldenHarvest',
      // SimulationEngine: `multiplier += 20.0` on the gathering yield.
      effect: '+20% gathering yield',
      tone: 'good',
    },
    2: {
      key: 'EventBloodMoon',
      // ProgressionEngine.ProcessMonsterDeath: `xpMultiplier += 15`.
      effect: '+15% combat XP',
      tone: 'danger',
    },
    3: {
      key: 'EventMasterArtisan',
      // CraftingEngine.GrantCraftedOutputAsync: `quantityProduced++` on a
      // 25% roll. This banner used to read "no effect on the server yet",
      // because for a quarter of every rotation the game really did announce
      // an event no code read.
      effect: '25% chance of an extra item from every craft',
      tone: 'accent',
    },
    4: {
      key: 'EventDiamondStar',
      // ForgeSplicingEngine: `feeDiscount + 0.05`. Fusion has had no roll
      // since 2026-09-06, so the old "+5 percentage points forge success"
      // promised a chance that does not exist; the event is a fee discount.
      effect: '5% off fusion fees',
      tone: 'accent',
    },
  };

  const eventId = $derived($playerState?.ActiveEventType ?? 0);
  const event = $derived(EVENTS[eventId] ?? null);
</script>

{#if event}
  <span class="event" data-tone={event.tone} title={event.effect}>
    <span class="label">{$t('ActiveEventPrefix')}</span>
    <!-- Modul: the name is a Hint. A phone hides the effect (below) and the
         title tooltip never shows on touch, so "Diamond Star" was an
         unexplained pill; a tap now says what it does. -->
    <Hint class="event-hint" text="{$t('ActiveEventPrefix')} {$t(event.key)}: {event.effect}."><strong>{$t(event.key)}</strong></Hint>
    <span class="effect">{event.effect}</span>
  </span>
{/if}

<style>
  .event {
    display: inline-flex;
    align-items: baseline;
    gap: 0.35rem;
    font-size: 0.78rem;
    padding: 0.15rem 0.55rem;
    border-radius: 999px;
    border: 1px solid var(--border);
    color: var(--text-dim);
  }

  .label {
    opacity: 0.75;
  }

  .effect {
    font-size: 0.7rem;
    opacity: 0.8;
  }

  strong {
    font-size: inherit;
  }

  /* Modul: on a phone the chip is the event's NAME. Prefix and effect made it
     a two-line banner on a row of its own; the effect is a tap on the name
     (the Hint), and the colour still carries the flavour. */
  /* A laptop-width header is full: with a seasonal event's "Event shop" chip
     beside the purse, the effect line pushed Sign out onto a second row at
     1366px. The name is a Hint that still says what the event does. */
  @media (max-width: 90rem) {
    .effect {
      display: none;
    }
  }

  /* ...and at 1280px the "Active Event:" prefix as well; the pill's colour and
     the Hint still say what it is. */
  @media (max-width: 85rem) {
    .label {
      display: none;
    }
  }

  @media (max-width: 40rem) {
    .event {
      order: 1;
    }

    .label,
    .effect {
      display: none;
    }

    /* Modul: on a phone the name IS the control, so it gets the same 44px as
       the Chat and Menu buttons beside it. Hint's ::after only grows the hit
       area by 20px, which left this pill at 39px - check:touch flagged it on
       every screen. */
    .event {
      align-items: center;
      padding-block: 0;
    }

    .event :global(.event-hint) {
      min-height: 44px;
    }
  }

  /* Colour carries the flavour; the effect text carries the meaning, so this
     is never colour-only. */
  .event[data-tone='good'] {
    color: var(--good);
    border-color: var(--good);
  }
  .event[data-tone='danger'] {
    color: var(--rarity-10);
    border-color: var(--rarity-10);
  }
  .event[data-tone='accent'] {
    color: var(--accent);
    border-color: var(--accent);
  }
</style>
