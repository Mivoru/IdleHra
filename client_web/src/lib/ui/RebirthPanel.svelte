<script lang="ts">
  // Modul: REBIRTH ON DEMAND (task 88). The season no longer ends on a date;
  // the player ends their own run here, whenever they choose.
  //
  // Everything on this panel is the SERVER's answer (GET /api/v1/rebirth/
  // preview): what goes, what stays, who the Hall lets go, and whether this
  // rebirth raises Renown. The panel decides nothing - it would be a second
  // copy of the rollover's wipe list, and two copies of one truth is this
  // codebase's dominant bug class.
  //
  // TWO STEPS, and nothing in either that ticks. Step one only OPENS the
  // terms; step two is a plain pair of buttons. No <select> (an Android
  // WebView redraws it as a system dialog that a re-render closes), and no
  // countdown inside a control - see CLAUDE.md. The POST carries the count the
  // preview showed, so a double tap or a stale page is refused by the server
  // (409 AlreadyReborn) rather than rebirthing twice.
  //
  // Prose and <p>, never <li>: exercise.mjs counts `.panel li` on this screen
  // to find the Hall's roster rows.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchRebirthPreview,
    requestRebirth,
    type RebirthOutcome,
  } from '../net/rest';
  import { invalidateOwnedItems } from '../net/queryClient';
  import { pushLocalNotice } from '../stores/game';
  import { formatNumber } from './format';

  const client = useQueryClient();
  const preview = createQuery(() => ({ queryKey: queryKeys.rebirthPreview, queryFn: fetchRebirthPreview }));
  const p = $derived(preview.data);

  let confirming = $state(false);
  let working = $state(false);
  let last = $state<RebirthOutcome | null>(null);

  function refreshAll() {
    client.invalidateQueries({ queryKey: queryKeys.rebirthPreview });
    client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    client.invalidateQueries({ queryKey: queryKeys.breedingRoster });
    client.invalidateQueries({ queryKey: queryKeys.deeds });
    invalidateOwnedItems(client);
  }

  async function rebirth() {
    if (!p || working) return;
    working = true;
    try {
      const outcome = await requestRebirth(p.RebirthCount);
      last = outcome;
      if (outcome.Result === 'Ok') {
        pushLocalNotice(
          outcome.Renowned
            ? `Reborn. Renown ${outcome.RenownedRebirths}: +${outcome.DamageBonusPct}% damage for good. +${formatNumber(outcome.ShardsEarned)} shards.`
            : `Reborn, below level ${p.RenownLevel}: no Renown this time. +${formatNumber(outcome.ShardsEarned)} shards.`,
          'info',
        );
      } else if (outcome.Result === 'AlreadyReborn' || outcome.Result === 'InFlight') {
        pushLocalNotice('That rebirth has already happened - the page was out of date.', 'error');
      } else {
        pushLocalNotice('The rebirth did not go through. Nothing was reset - try again.', 'error');
      }
    } catch {
      pushLocalNotice('The rebirth did not go through. Nothing was reset - try again.', 'error');
    } finally {
      working = false;
      confirming = false;
      refreshAll();
    }
  }
</script>

<section class="panel rebirth" data-testid="rebirth-panel">
  <h2>Rebirth</h2>
  <p class="dim small">
    End this run whenever you choose and start again at level 1 with the
    bloodline, the Seals and everything diamonds bought. No date ends it for
    you.
  </p>

  {#if preview.isPending}
    <p class="dim tiny">Reading what a rebirth would do&hellip;</p>
  {:else if preview.isError || !p}
    <p class="warn">What a rebirth would do could not be loaded.</p>
  {:else}
    <p class="renown" data-testid="rebirth-renown">
      <strong>Renown {p.RenownedRebirths}</strong> &middot; +{p.DamageBonusPctNow}% damage, for good.
      {#if p.Renowned}
        Reborn now (level {p.Level}): <strong>+{p.DamageBonusPctAfter}%</strong>.
      {:else}
        Only a rebirth at level {p.RenownLevel} or above raises it &mdash; you are {p.Level}.
      {/if}
      <span class="dim">Each step is smaller; it never reaches {p.DamageBonusPctCap}%.</span>
    </p>

    {#if !confirming}
      <button class="rebirth-open" disabled={working} onclick={() => (confirming = true)}>
        Rebirth&hellip;
      </button>
      {#if p.RebirthCount > 0}
        <p class="dim tiny">Reborn {p.RebirthCount} {p.RebirthCount === 1 ? 'time' : 'times'} so far.</p>
      {/if}
    {:else}
      <div class="terms" data-testid="rebirth-terms">
        <p>
          <strong>You lose:</strong> level {p.Level} (back to 1), {formatNumber(p.Gold)} gold,
          {p.MaterialStacks} material {p.MaterialStacks === 1 ? 'stack' : 'stacks'},
          {p.EquipmentPieces} {p.EquipmentPieces === 1 ? 'piece' : 'pieces'} of gear{#if p.MarketListings > 0}
            and {p.MarketListings} market {p.MarketListings === 1 ? 'listing' : 'listings'}{/if},
          {p.SkillTreeLevels} skill tree {p.SkillTreeLevels === 1 ? 'level' : 'levels'},
          {p.AttributePoints} attribute points, your potions, the chronicle pass and
          the village gene pool ({p.VillageNewcomers} {p.VillageNewcomers === 1 ? 'newcomer' : 'newcomers'}).
          Everybody goes idle.
        </p>
        {#if p.HallLetGo.length > 0}
          <p class="warn">
            <strong>The Hall lets go, permanently:</strong> {p.HallLetGo.join(', ')}.
            Mark someone <em>Keep</em> above to change who.
          </p>
        {/if}
        <p>
          <strong>You keep:</strong> {p.HallMembersKept} ancestors and their aptitudes,
          {p.Seals} {p.Seals === 1 ? 'Seal' : 'Seals'} ({p.SkillPointsFromSeals} skill points back at once),
          {p.InheritanceLevels} Inheritance levels, {formatNumber(p.ShardBalance)} shards,
          {formatNumber(p.Diamonds)} diamonds, {p.VillageBuildingLevels} village building levels,
          race and gathering masteries, the codex and your deeds.
        </p>
        <p>
          <strong>You gain:</strong> {formatNumber(p.ShardsEarned)} shards{#if p.Renowned}, and Renown
            {p.RenownedRebirths + 1} (+{p.DamageBonusPctAfter}% damage){/if}.
        </p>
        <div class="confirm-row">
          <button class="rebirth-confirm danger" disabled={working} onclick={rebirth}>
            {working ? 'Rebirthing…' : 'Yes, rebirth now'}
          </button>
          <button class="rebirth-cancel" disabled={working} onclick={() => (confirming = false)}>
            Cancel
          </button>
        </div>
      </div>
    {/if}

    {#if last && last.Result === 'Ok'}
      <p class="dim tiny" data-testid="rebirth-last">
        Last rebirth: Renown {last.RenownedRebirths}, +{last.DamageBonusPct}% damage, +{formatNumber(last.ShardsEarned)} shards.
      </p>
    {/if}
  {/if}
</section>

<style>
  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    display: grid;
    gap: 0.5rem;
  }

  h2 {
    margin: 0;
    font-size: 1.05rem;
  }

  p {
    margin: 0;
    line-height: 1.4;
    max-width: 78ch;
    overflow-wrap: anywhere;
  }

  .terms {
    display: grid;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem;
    background: var(--bg);
    border-radius: var(--radius);
  }

  .confirm-row {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin-top: 0.2rem;
  }

  .confirm-row button,
  .rebirth-open {
    flex-shrink: 0;
    justify-self: start;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .warn {
    color: var(--warn);
  }
</style>
