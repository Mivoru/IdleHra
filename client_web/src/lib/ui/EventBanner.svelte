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
      // ForgeSplicingEngine: `baseProbability += 0.05`.
      effect: '+5 percentage points forge success',
      tone: 'accent',
    },
  };

  const eventId = $derived($playerState?.ActiveEventType ?? 0);
  const event = $derived(EVENTS[eventId] ?? null);
</script>

{#if event}
  <span class="event" data-tone={event.tone} title={event.effect}>
    <span class="label">{$t('ActiveEventPrefix')}</span>
    <strong>{$t(event.key)}</strong>
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
