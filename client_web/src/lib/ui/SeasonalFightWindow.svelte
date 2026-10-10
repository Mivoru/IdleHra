<script lang="ts">
  // Modul: THE CAILLEACH'S FIGHT, IN ITS OWN WINDOW (owner, 2026-10-10): "after
  // Fight a window pops up with the fight against the Cailleach; when the
  // player beats her - congratulations, what they got, and Close or the next
  // winter; when they fail - Close or Try again". It replaced a jump to the
  // Combat screen, which drew the region boss she borrows her strength from.
  //
  // Mounted with the other session overlays, so the window outlives a change
  // of screen. What happened is read from the server's statements, never from
  // a health difference - see stores/seasonalFight.ts.
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchSeasonalBoss, seasonalBossKeys, seasonalTierBlocked, ROMAN, type SeasonalBossTier } from '../net/seasonalBoss';
  import { connectionStatus, playerState, visualState } from '../stores/game';
  import { combatLog, describeCombatLine, CombatEventKind } from '../stores/combatLog';
  import { closeSeasonalFight, seasonalFight, setSeasonalFightHidden } from '../stores/seasonalFight';
  import { fightSeasonalTier } from './seasonalFightStart';
  import { formatExact, formatNumber } from './format';
  import { spriteUrl } from './spriteUrl';
  import Modal from './Modal.svelte';
  import Bar from './Bar.svelte';
  import DisabledReason from './DisabledReason.svelte';

  const NAME = 'The Cailleach';

  const fight = $derived($seasonalFight);
  const open = $derived(fight !== null && !fight.hidden);

  const view = createQuery(() => ({
    queryKey: seasonalBossKeys.all,
    queryFn: fetchSeasonalBoss,
    enabled: fight !== null,
  }));
  const tiers = $derived(view.data?.Tiers ?? []);
  const tier = $derived(fight ? (tiers.find((t) => t.Tier === fight.tier) ?? null) : null);
  const next = $derived(fight ? (tiers.find((t) => t.Tier === fight.tier + 1) ?? null) : null);

  const snap = $derived($playerState);
  const live = $derived($connectionStatus.phase === 'live');
  const clearedMask = $derived(Number(snap?.SeasonalBossClearedMask ?? 0));
  const bossMask = $derived(Number(snap?.DefeatedRegionBossMask ?? 0));
  const armed = $derived(Number(snap?.SeasonalBossTier ?? 0));

  // Her bars are slot 1's: the attempt is always the main character's
  // (SeasonalBossTickCoordinator). Shown only while slot 1 faces her boss, so
  // the bar never draws whatever the character returns to afterwards.
  const facingHer = $derived(fight !== null && Number(snap?.CurrentMonsterId ?? 0) === fight.bossId && armed === fight.tier);
  const herHp = $derived($visualState?.CurrentMonsterHp ?? Number(snap?.CurrentMonsterHp ?? 0));
  const herMax = $derived(Number(snap?.CurrentMonsterMaxHp ?? 0));
  const myHp = $derived($visualState?.PlayerHp ?? Number(snap?.PlayerHp ?? 0));
  const myMax = $derived(Number(snap?.PlayerMaxHp ?? 0));

  // The last few blows of THIS fight, newest first.
  const lines = $derived(
    fight
      ? $combatLog
          .filter((l) => l.monsterId === fight.bossId && l.id >= fight.sinceSequence && l.kind !== CombatEventKind.Kill)
          .slice(0, 5)
      : [],
  );

  // Modul: a fight the server ended without a kill or a death the window saw -
  // the character was sent elsewhere from another screen, or a reconnect
  // swallowed the moment. The armed tier reads 0; give the kill event (its own
  // packet, which can trail the snapshot) a moment, then let the window go.
  $effect(() => {
    if (!fight || fight.phase !== 'fighting' || armed === fight.tier) return;
    const handle = setTimeout(() => {
      if ($seasonalFight?.phase === 'fighting') closeSeasonalFight();
    }, 3000);
    return () => clearTimeout(handle);
  });

  const nextBlocked = $derived(next && fight ? seasonalTierBlocked(next, tiers, clearedMask, bossMask, live) : null);

  function rewardParts(t: SeasonalBossTier): string[] {
    const parts = [`${t.Diamonds} diamonds`, `${formatNumber(t.Gold)} gold`, `${formatExact(t.Currency)} pumpkins`];
    if (t.Title) parts.push(`the title “${t.Title}”`);
    if (t.Pet) parts.push(`the ${t.Pet}, a new pet`);
    return parts;
  }

  function again() {
    if (!fight || !tier) return;
    fightSeasonalTier(tier, fight.eventId, clearedMask);
  }

  function goNext() {
    if (!fight || !next) return;
    fightSeasonalTier(next, fight.eventId, clearedMask);
  }

  /** While she still stands, closing only hides the window: the fight runs on. */
  function close() {
    if (!fight) return;
    if (fight.phase === 'starting' || fight.phase === 'fighting') setSeasonalFightHidden(true);
    else closeSeasonalFight();
  }
</script>

{#if fight && open}
  <Modal
    label="The Cailleach, winter {ROMAN[fight.tier]}"
    tone={fight.phase === 'lost' ? 'danger' : 'brass'}
    width="28rem"
    onClose={close}
    dismissOnScrim={false}
    testid="seasonal-fight"
  >
    <div class="head">
      <img src={spriteUrl('Events/samhain/boss/cailleach.webp')} alt="" decoding="async" class:fallen={fight.phase === 'won'} />
      <div>
        <p class="kicker">Winter {ROMAN[fight.tier]}</p>
        <h2>
          {#if fight.phase === 'won'}
            {NAME} falls back
          {:else if fight.phase === 'lost'}
            {NAME} has beaten you
          {:else}
            {NAME}
          {/if}
        </h2>
      </div>
    </div>

    {#if fight.phase === 'starting' || fight.phase === 'fighting'}
      <div class="bars" data-testid="seasonal-fight-bars">
        {#if facingHer}
          <span class="tiny dim">{NAME}</span>
          <Bar value={herHp} max={herMax} color="var(--danger)" label={`${formatNumber(Math.round(herHp))} / ${formatNumber(herMax)}`} ariaLabel="{NAME}'s health" />
          <span class="tiny dim">You</span>
          <Bar value={myHp} max={myMax} tone="good" label={`${formatNumber(Math.round(myHp))} / ${formatNumber(myMax)}`} ariaLabel="Your health" />
        {:else}
          <p class="dim small">She comes down from the mountain…</p>
        {/if}
      </div>
      <ol class="log" aria-live="polite">
        {#each lines as line (line.id)}
          <li class="tiny">{describeCombatLine(line, NAME)}</li>
        {/each}
      </ol>
      <div class="row">
        <button type="button" onclick={close} data-testid="seasonal-fight-hide">Hide - the fight goes on</button>
      </div>
    {:else if fight.phase === 'won'}
      <p class="congrats" data-testid="seasonal-fight-won">Congratulations - you broke winter {ROMAN[fight.tier]}!</p>
      {#if fight.firstClear && view.isError}
        <!-- The ladder failed to load: the reward is still paid, only the list is missing. -->
        <p class="small">Her first fall here is paid - the pumpkins are yours and the rest is in your mail.</p>
      {:else if fight.firstClear && tier}
        <div class="reward">
          <p class="small"><strong>You got:</strong></p>
          <ul>
            {#each rewardParts(tier) as part (part)}
              <li>{part}</li>
            {/each}
            {#if fight.xp > 0}<li>{formatNumber(fight.xp)} xp</li>{/if}
          </ul>
          <p class="tiny dim">
            {fight.rewardSealed
              ? 'The pumpkins are yours already; the rest is waiting in your mail.'
              : 'Sealing your reward…'}
          </p>
        </div>
      {:else}
        <p class="small dim">
          This winter was already broken, so its first-fall reward is not paid again{fight.xp > 0
            ? ` - you earned ${formatNumber(fight.xp)} xp`
            : ''}.
        </p>
      {/if}
      <p class="tiny dim">Your character went back to what it was doing before.</p>
      <div class="row">
        {#if next}
          <div class="go">
            <button type="button" class="primary" disabled={nextBlocked !== null} onclick={goNext} data-testid="seasonal-fight-next">
              Next: winter {ROMAN[next.Tier]}
            </button>
            <DisabledReason text={nextBlocked} />
          </div>
        {/if}
        <button type="button" onclick={closeSeasonalFight} data-testid="seasonal-fight-close">Close</button>
      </div>
    {:else}
      <p class="small" data-testid="seasonal-fight-lost">
        Her winter was too cold this time. You revived at full health; your character went back to what it was doing
        before.
      </p>
      <p class="tiny dim">Better gear and a stocked larder help - you may try as often as you like.</p>
      <div class="row">
        <button type="button" class="primary" disabled={!live || !tier} onclick={again} data-testid="seasonal-fight-again">Try again</button>
        <button type="button" onclick={closeSeasonalFight} data-testid="seasonal-fight-close">Close</button>
      </div>
    {/if}
  </Modal>
{/if}

<style>
  .head {
    display: flex;
    gap: 0.8rem;
    align-items: center;
  }

  .head img {
    width: 5rem;
    height: 5rem;
    object-fit: contain;
    flex: none;
    transition: filter 0.6s, opacity 0.6s;
  }

  .head img.fallen {
    filter: grayscale(0.8);
    opacity: 0.7;
  }

  .kicker {
    margin: 0;
    font-size: 0.7rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--event-accent, var(--brass));
  }

  h2 {
    margin: 0;
    font-family: Georgia, serif;
    font-size: 1.25rem;
  }

  .bars {
    display: grid;
    gap: 0.25rem;
  }

  .log {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.15rem;
    min-height: 5.5rem;
    overflow-anchor: none;
    color: var(--text-dim);
  }

  .congrats {
    margin: 0;
    font-weight: 600;
    color: var(--event-accent, var(--brass-lit));
  }

  .reward {
    padding: 0.5rem 0.7rem;
    border-left: 3px solid var(--event-accent, var(--brass));
    border-radius: 4px;
    background: color-mix(in srgb, var(--event-accent, var(--brass)) 10%, transparent);
  }

  .reward p {
    margin: 0;
  }

  .reward ul {
    margin: 0.25rem 0;
    padding-left: 1.1rem;
  }

  .row {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    align-items: start;
  }

  .go {
    display: grid;
  }

  .dim {
    color: var(--text-dim);
  }

  .small {
    font-size: 0.85rem;
    margin: 0;
  }

  .tiny {
    font-size: 0.75rem;
  }
</style>
