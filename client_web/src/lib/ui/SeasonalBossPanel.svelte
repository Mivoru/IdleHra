<script lang="ts">
  // The Cailleach's six winters: the seasonal boss ladder. Shown on the World
  // Boss screen (owner: "in the window with the world boss") and on the event
  // screen's Boss tab. Each tier is a region boss at its first-clear strength,
  // the sixth a step past region 5; only a first clear pays, and attempts are
  // unlimited. The ladder, bosses and rewards are the server's answer; cleared
  // tiers and beaten region bosses come off the wire so the next tier unlocks
  // the moment one falls.
  import { createQuery } from '@tanstack/svelte-query';
  import { startSeasonalBoss } from '../net/commands';
  import { fetchSeasonalBoss, seasonalBossKeys, tierCleared, ROMAN, type SeasonalBossTier } from '../net/seasonalBoss';
  import { connectionStatus, playerState, pushLocalNotice } from '../stores/game';
  import { requestScreen } from '../stores/navigation';
  import { formatExact, formatNumber } from './format';
  import { spriteUrl } from './spriteUrl';
  import QueryError from './QueryError.svelte';
  import DisabledReason from './DisabledReason.svelte';

  interface Props {
    /** The compact form for the World Boss screen: no lore paragraph. */
    compact?: boolean;
  }

  const { compact = false }: Props = $props();

  const view = createQuery(() => ({ queryKey: seasonalBossKeys.all, queryFn: fetchSeasonalBoss, refetchInterval: 60_000 }));

  const live = $derived($connectionStatus.phase === 'live');
  const clearedMask = $derived(Number($playerState?.SeasonalBossClearedMask ?? 0));
  const bossMask = $derived(Number($playerState?.DefeatedRegionBossMask ?? 0));
  const armed = $derived(Number($playerState?.SeasonalBossTier ?? 0));
  const eventId = $derived(view.data?.EventId ?? 0);
  const tiers = $derived(view.data?.Tiers ?? []);

  function isCleared(t: SeasonalBossTier): boolean {
    return t.Cleared || tierCleared(clearedMask, t.Tier);
  }

  /** Why Fight is off, in words. */
  function blocked(t: SeasonalBossTier): string | null {
    if ((bossMask & (1 << (t.Region - 1))) === 0) return `Beat region ${t.Region}'s boss first.`;
    if (t.Tier > 1 && !tierCleared(clearedMask, t.Tier - 1) && !tiers[t.Tier - 2]?.Cleared) return `Break winter ${ROMAN[t.Tier - 1]} first.`;
    if (!live) return 'Waiting for the connection.';
    return null;
  }

  function fight(t: SeasonalBossTier) {
    const outcome = startSeasonalBoss(t.Tier, eventId);
    if (!outcome.ok) {
      pushLocalNotice(outcome.reason, 'error');
      return;
    }
    requestScreen('combat');
  }

  function reward(t: SeasonalBossTier): string {
    const parts = [`${t.Diamonds} diamonds`, `${formatNumber(t.Gold)} gold`, `${formatExact(t.Currency)} pumpkins`];
    if (t.Title) parts.push(`the title “${t.Title}”`);
    if (t.Pet) parts.push(`the ${t.Pet}`);
    return parts.join(' · ');
  }
</script>

{#if view.isError}
  <QueryError query={view} what="The Cailleach" />
{:else if view.data && eventId > 0 && tiers.length > 0}
  <section class="panel cailleach" data-testid="seasonal-boss">
    <div class="head">
      <img src={spriteUrl('Events/samhain/boss/cailleach.webp')} alt="" decoding="async" />
      <div>
        <h2>The Cailleach</h2>
        {#if !compact}
          <p class="dim tiny">
            Six winters, each as hard as a region's boss the first time you met it - and the sixth harder than anything
            in the valley. Her first fall at each winter pays; you may try as often as you like.
          </p>
        {:else}
          <p class="dim tiny">Samhain's boss: six winters, the first fall of each pays.</p>
        {/if}
      </div>
    </div>

    <ol class="tiers">
      {#each tiers as t (t.Tier)}
        {@const done = isCleared(t)}
        {@const why = done ? null : blocked(t)}
        <li class="tier" class:done class:armed={armed === t.Tier} data-testid="seasonal-boss-tier" data-tier={t.Tier}>
          <span class="roman">{ROMAN[t.Tier]}</span>
          <div class="what">
            <strong>{t.BossName}{t.HpPct > 0 || t.AttackPct > 0 ? ` +${t.HpPct}% health, +${t.AttackPct}% attack` : ''}</strong>
            <span class="tiny dim">{done ? 'Broken.' : reward(t)}</span>
          </div>
          {#if done}
            <button type="button" class="tiny-btn" disabled={!live} onclick={() => fight(t)} data-testid="seasonal-boss-again">Again</button>
          {:else}
            <div class="go">
              <button type="button" class="primary" disabled={why !== null} onclick={() => fight(t)} data-testid="seasonal-boss-fight">Fight</button>
              <DisabledReason text={why} />
            </div>
          {/if}
        </li>
      {/each}
    </ol>
  </section>
{/if}

<style>
  .cailleach {
    display: grid;
    gap: 0.7rem;
    border-color: var(--event-accent, var(--border));
  }

  .head {
    display: flex;
    gap: 0.8rem;
    align-items: center;
  }

  .head img {
    width: 4.5rem;
    height: 4.5rem;
    object-fit: contain;
    flex: none;
  }

  .head h2 {
    margin: 0;
  }

  .head p {
    margin: 0.2rem 0 0;
  }

  .tiers {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }

  .tier {
    display: flex;
    align-items: center;
    gap: 0.7rem;
    padding: 0.45rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 0.5rem;
  }

  .tier.done {
    opacity: 0.75;
  }

  .tier.armed {
    border-color: var(--event-accent, var(--accent));
  }

  .roman {
    width: 2rem;
    font-family: Georgia, serif;
    font-size: 1.2rem;
    text-align: center;
    color: var(--event-accent, var(--accent));
    flex: none;
  }

  .what {
    display: grid;
    flex: 1;
    min-width: 0;
  }

  .go {
    display: grid;
    justify-items: end;
    flex-shrink: 0;
  }

  .tier > button {
    flex-shrink: 0;
  }
</style>
