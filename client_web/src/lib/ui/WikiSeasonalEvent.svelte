<script lang="ts">
  // Modul: THE WIKI READS THE EVENT, IT DOES NOT COPY IT. Rates, prices, the
  // rare pet's odds and the boss ladder were each retuned more than once in the
  // event's first two days (pumpkins were cut 25x on 2026-10-10). A wiki that
  // restated them would have been wrong by the evening, so this renders the
  // same two answers the event screen does - /api/v1/event and
  // /api/v1/seasonal-boss - and only the rules that do not move are prose.
  import { createQuery } from '@tanstack/svelte-query';
  import { chancePct, EVENT_PHASE, EVENT_SHOP_KIND, fetchSeasonalEvent, oneIn, seasonalEventKeys } from '../net/seasonalEvent';
  import { fetchSeasonalBoss, seasonalBossKeys, ROMAN } from '../net/seasonalBoss';
  import { formatExact, formatNumber } from './format';
  import Skeleton from './Skeleton.svelte';
  import QueryError from './QueryError.svelte';

  const event = createQuery(() => ({ queryKey: seasonalEventKeys.all, queryFn: fetchSeasonalEvent, staleTime: 60_000 }));
  const boss = createQuery(() => ({ queryKey: seasonalBossKeys.all, queryFn: fetchSeasonalBoss, staleTime: 60_000 }));

  const ev = $derived(event.data ?? null);
  const tiers = $derived(boss.data?.Tiers ?? []);

  function date(utcSeconds: number): string {
    return new Date(utcSeconds * 1000).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', timeZone: 'UTC' });
  }
</script>

<h3 id="seasonal">Seasonal events</h3>
<p class="dim small">
  A few weeks a year the valley keeps a festival. While one runs, every kill and every harvest has a small chance
  of paying the event's own currency - online and offline alike - and the event screen (the chip in the header)
  sells avatars and pets for it. The currency belongs to that one event: when the shop closes, what is left is gone.
  What you bought stays yours for good, through every rebirth.
</p>

{#if event.isError}
  <QueryError query={event} what="the event" />
{:else if event.isPending}
  <Skeleton rows={3} />
{:else if ev}
  {@const unit = ev.CurrencyName.toLowerCase().replace(/s$/, '')}
  <div class="card" data-testid="wiki-seasonal-event">
    <strong>{ev.Name}</strong>
    <span class="dim small">
      - {ev.Phase === EVENT_PHASE.Live ? 'running now' : 'ended, shop still open'}: {date(ev.StartUtc)} to {date(ev.EndUtc)},
      shop open until {date(ev.ShopCloseUtc)} (UTC).
    </span>
    <ul class="styled-list">
      <li>
        <strong>{ev.CurrencyName}:</strong> {chancePct(ev.KillChance)} per kill, {chancePct(ev.GatherChance)} per harvest{#if ev.OfflineFactor === 1}, the same while you are away{:else}, {Math.round(ev.OfflineFactor * 100)}% of that while away{/if}.
        Each character rolls for its own work - the fighter on its kills, a gatherer on its harvests.
      </li>
      {#each ev.Shop as item (item.Id)}
        <li>
          <strong>{item.Name}</strong> ({item.Kind === EVENT_SHOP_KIND.Pet ? 'pet' : 'avatar'}) - {formatExact(item.Price)} {ev.CurrencyName.toLowerCase()}{#if item.Bonuses.length > 0}: {item.Bonuses.join(', ')}{/if}
        </li>
      {/each}
      {#if ev.RarePet}
        <li data-testid="wiki-rare-pet">
          <strong>{ev.RarePet.Name}</strong> (rare pet, never sold) - every {unit} you earn has a
          {oneIn(ev.RarePetChancePerCurrency)} chance to bring her, about {oneIn(ev.RarePetChancePerCurrency * ev.KillChance)} per kill:
          {ev.RarePet.Bonuses.join(', ')}.
        </li>
      {/if}
      {#if ev.BossPet}
        <li>
          <strong>{ev.BossPet.Name}</strong> (boss pet, never sold) - the first clear of the boss's tier {ev.BossPetTier}:
          {ev.BossPet.Bonuses.join(', ')}.
        </li>
      {/if}
    </ul>
  </div>
{:else}
  <p class="dim small">No event is running right now.</p>
{/if}

{#if boss.isError}
  <QueryError query={boss} what="the seasonal boss" />
{:else if tiers.length > 0}
  <h3 id="cailleach">The seasonal boss</h3>
  <p class="dim small">
    Samhain's boss is <strong>The Cailleach</strong>, fought from the event screen's Boss tab or the World Boss screen.
    She has six tiers: tier N is as strong as region N's boss was the first time you met it, so a tier opens once you
    have beaten that region's boss, and tier VI is harder still. Fight opens her own window, and one fight is one
    attempt - win or die, your character then goes back to the work it was doing. Attempts are unlimited, but only the
    <strong>first</strong> clear of a tier pays; diamonds, gold and titles arrive by mail.
  </p>
  <div class="scroll">
    <table>
      <thead>
        <tr><th>Tier</th><th>Strength</th><th class="num">Diamonds</th><th class="num">Gold</th><th class="num">{ev?.CurrencyName ?? 'Currency'}</th><th>Also</th></tr>
      </thead>
      <tbody>
        {#each tiers as t (t.Tier)}
          <tr>
            <td>{ROMAN[t.Tier] ?? t.Tier}</td>
            <td class="dim">{t.BossName}{#if t.HpPct > 0 || t.AttackPct > 0} +{t.HpPct}% health, +{t.AttackPct}% attack{/if}</td>
            <td class="num">{t.Diamonds}</td>
            <td class="num">{formatNumber(t.Gold)}</td>
            <td class="num">{formatExact(t.Currency)}</td>
            <td class="dim">{[t.Title ? `title “${t.Title}”` : '', t.Pet ?? ''].filter(Boolean).join(', ') || '-'}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}

<h3 id="pets">Pets</h3>
<p class="dim small">
  A pet follows one character and adds its bonus to that character's stats. Give it one from the pet tile, the
  twelfth tile of the Gear grid on the Character screen. Each character carries one pet, and each pet can be on one
  character at a time. You own each pet once per account. Pets are permanent: they outlive their event and every
  rebirth. Today pets come from seasonal events - bought in the event shop, found as a rare drop, or won from the
  seasonal boss.
</p>

<style>
  /* Scoped copies of the Wiki's own rules - a child component does not
     inherit them (WikiVillage does the same). */
  h3 {
    margin: 2rem 0 0.75rem;
    color: var(--text);
    font-size: 1.1rem;
    scroll-margin-top: 1rem;
  }

  p {
    margin: 0.6rem 0;
    line-height: 1.5;
    max-width: 70ch;
  }

  .card {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.75rem;
    background: rgba(0, 0, 0, 0.12);
    min-width: 0;
  }

  .styled-list {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    padding-left: 1.2rem;
    margin: 0.5rem 0 0;
    color: var(--text-dim);
    font-size: 0.88rem;
    line-height: 1.45;
    max-width: 70ch;
  }

  .styled-list strong {
    color: var(--text);
  }

  .scroll {
    overflow-x: auto;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: rgba(0, 0, 0, 0.12);
    margin: 0.5rem 0;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
    min-width: 30rem;
  }

  th {
    text-align: left;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--border);
    color: var(--text-dim);
    font-weight: 600;
    white-space: nowrap;
  }

  td {
    padding: 0.45rem 0.6rem;
    border-bottom: 1px solid rgba(128, 128, 128, 0.12);
    vertical-align: top;
    line-height: 1.4;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .dim {
    color: var(--text-dim);
  }

  .small {
    font-size: 0.85rem;
  }
</style>
