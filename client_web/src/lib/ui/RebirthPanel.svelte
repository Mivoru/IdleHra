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
  //
  // Modul: AND THE PLAYER TYPES THE WORD (2026-10-08). Two players rebirthed
  // by accident in one evening: "Yes, rebirth now" appeared exactly where
  // "Rebirth..." had been, so a double tap went straight through. Step two now
  // states plainly that it cannot be undone, Cancel comes first, and the
  // confirm button stays disabled until REBIRTH is typed. The server refuses
  // a request without the word (ConfirmationRequired), so an old bundle
  // cannot skip it.
  //
  // Modul: A REBIRTH WORTH TAKING (task 114, owner 2026-10-11). The terms now
  // say what a rebirth PAYS - skill points, faster re-levelling, the second
  // pet slot, the cap - and let the player keep pieces of gear for a gold
  // price by rarity. Every number is the preview's; the panel only adds up
  // the prices it was sent, and the server prices and checks the keep again.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchRebirthPreview,
    fetchInventory,
    requestRebirth,
    REBIRTH_CONFIRMATION_WORD,
    type RebirthOutcome,
  } from '../net/rest';
  import { invalidateOwnedItems } from '../net/queryClient';
  import { prettifyBaseId } from '../net/content';
  import { pushLocalNotice } from '../stores/game';
  import { formatNumber } from './format';
  import { rarityColor, rarityName } from './rarity';

  const client = useQueryClient();
  const preview = createQuery(() => ({ queryKey: queryKeys.rebirthPreview, queryFn: fetchRebirthPreview }));
  const p = $derived(preview.data);

  let confirming = $state(false);

  // The keep list: the gear the player wears first, then the rarest of the
  // rest. Loaded only once the terms are open - the inventory is big.
  const KEEP_ROWS = 40;
  const inventory = createQuery(() => ({
    queryKey: queryKeys.inventory,
    queryFn: fetchInventory,
    enabled: confirming,
  }));
  const keepCandidates = $derived(
    [...(inventory.data?.Equipment ?? [])]
      .sort((a, b) => Number(b.IsEquipped) - Number(a.IsEquipped) || b.QualityTier - a.QualityTier)
      .slice(0, KEEP_ROWS),
  );
  let keep = $state<number[]>([]);
  function priceOf(tier: number): number {
    const prices = p?.KeepPriceByTier ?? [];
    if (prices.length === 0) return 0;
    return prices[Math.max(0, Math.min(tier, prices.length - 1))];
  }
  const keepTotal = $derived(
    keepCandidates.filter((e) => keep.includes(e.Id)).reduce((sum, e) => sum + priceOf(e.QualityTier), 0),
  );
  const keepAffordable = $derived(!p || keepTotal <= p.Gold);
  function toggleKeep(id: number) {
    keep = keep.includes(id) ? keep.filter((k) => k !== id) : [...keep, id];
  }
  let typed = $state('');
  const wordTyped = $derived(typed.trim().toUpperCase() === REBIRTH_CONFIRMATION_WORD);
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
    if (!p || working || !wordTyped) return;
    working = true;
    try {
      const outcome = await requestRebirth(p.RebirthCount, typed, keep);
      last = outcome;
      if (outcome.Result === 'Ok') {
        pushLocalNotice(
          outcome.Renowned
            ? `Reborn. Renown ${outcome.RenownedRebirths}: +${outcome.DamageBonusPct}% damage for good. +${formatNumber(outcome.ShardsEarned)} shards.`
            : `Reborn, below level ${p.RenownLevel}: no Renown this time. +${formatNumber(outcome.ShardsEarned)} shards.`,
          'info',
        );
      } else if (outcome.Result === 'ConfirmationRequired') {
        pushLocalNotice(`Type ${REBIRTH_CONFIRMATION_WORD} to confirm. Nothing was reset.`, 'error');
      } else if (outcome.Result === 'Capped') {
        pushLocalNotice(`You have taken all ${p.MaxRebirths} rebirths. Nothing was reset.`, 'error');
      } else if (outcome.Result === 'NotEnoughGold') {
        pushLocalNotice('Your gold does not cover the gear you chose to keep. Nothing was reset.', 'error');
      } else if (outcome.Result === 'KeepNotOwned') {
        pushLocalNotice('A piece you chose to keep is no longer yours. Nothing was reset.', 'error');
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
      typed = '';
      keep = [];
      refreshAll();
    }
  }
</script>

<section class="panel rebirth" data-testid="rebirth-panel" data-guide="rebirth">
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

    <p class="pays" data-testid="rebirth-pays">
      <strong>Each rebirth pays:</strong> +{p.SkillPointsFromRebirthsAfter - p.SkillPointsFromRebirthsNow} skill points for good
      (now {p.SkillPointsFromRebirthsNow}), and +{p.ReLevelXpBonusPct}% combat XP on every level below your best
      (level {p.PeakLevelAfter} after this one). {p.SecondPetSlotRebirths} rebirths open a second pet slot on every character.
      <span class="dim">{p.RebirthCount} of {p.MaxRebirths} taken.</span>
    </p>

    {#if p.Capped}
      <p class="dim" data-testid="rebirth-capped">You have taken all {p.MaxRebirths} rebirths.</p>
    {:else if !confirming}
      <button class="rebirth-open" data-guide="rebirth-start" disabled={working} onclick={() => (confirming = true)}>
        Rebirth&hellip;
      </button>
    {:else}
      <div class="terms" data-testid="rebirth-terms">
        <p class="danger-note" data-testid="rebirth-warning">
          <strong>This cannot be undone.</strong> Your character goes back to
          level 1 and loses the gear, gold and materials listed below, for good.
          Read it through before you confirm.
        </p>
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
          <strong>You gain:</strong> {formatNumber(p.ShardsEarned)} shards,
          {p.SkillPointsFromRebirthsAfter} skill points from rebirths, +{p.ReLevelXpBonusPct}% XP up to level
          {p.PeakLevelAfter}{#if p.Renowned}, and Renown {p.RenownedRebirths + 1} (+{p.DamageBonusPctAfter}% damage){/if}.
          {#if p.RebirthCount + 1 === p.SecondPetSlotRebirths}The second pet slot opens on every character.{/if}
        </p>

        <div class="keep" data-testid="rebirth-keep">
          <p>
            <strong>Keep gear (optional):</strong> each piece you tick stays in your chest, unworn, and costs
            gold by rarity, paid from the gold this rebirth takes. Worn gear is listed first.
          </p>
          {#if inventory.isPending}
            <p class="dim tiny">Reading your gear&hellip;</p>
          {:else if keepCandidates.length === 0}
            <p class="dim tiny">No gear to keep.</p>
          {:else}
            <div class="keep-list">
              {#each keepCandidates as e (e.Id)}
                <label class="keep-row">
                  <input type="checkbox" checked={keep.includes(e.Id)} disabled={working} onchange={() => toggleKeep(e.Id)} />
                  <span class="name" style="color: {rarityColor(e.QualityTier)}">{prettifyBaseId(e.BaseItemId)}</span>
                  <span class="dim tiny">[{rarityName(e.QualityTier)}]{#if e.IsEquipped} &middot; worn{/if}</span>
                  <span class="price">{formatNumber(priceOf(e.QualityTier))}</span>
                </label>
              {/each}
            </div>
          {/if}
          <p class:warn={!keepAffordable} data-testid="rebirth-keep-total">
            Keeping {keep.length} {keep.length === 1 ? 'piece' : 'pieces'}: {formatNumber(keepTotal)} of
            {formatNumber(p.Gold)} gold.
          </p>
        </div>
        <label class="typeword">
          <span>Type <strong>{REBIRTH_CONFIRMATION_WORD}</strong> to confirm</span>
          <input
            data-testid="rebirth-confirm-word"
            bind:value={typed}
            autocomplete="off"
            autocapitalize="characters"
            spellcheck="false"
            placeholder={REBIRTH_CONFIRMATION_WORD}
            disabled={working}
          />
        </label>
        <div class="confirm-row">
          <button class="rebirth-cancel" disabled={working} onclick={() => { confirming = false; typed = ''; }}>
            Cancel
          </button>
          <button class="rebirth-confirm danger" disabled={working || !wordTyped || !keepAffordable} onclick={rebirth}>
            {working ? 'Rebirthing…' : 'Yes, rebirth now'}
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

  .danger-note {
    padding: 0.45rem 0.6rem;
    border: 1px solid var(--danger);
    border-radius: var(--radius);
    color: var(--danger);
  }

  .keep {
    display: grid;
    gap: 0.35rem;
  }

  .keep-list {
    display: grid;
    gap: 0.15rem;
    max-height: 16rem;
    overflow-y: auto;
  }

  .keep-row {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    min-height: 2.75rem;
    font-size: 0.85rem;
  }

  .keep-row input {
    flex-shrink: 0;
    width: 1.4rem;
    height: 1.4rem;
  }

  .keep-row .name {
    flex: 1 1 auto;
    min-width: 6rem;
    overflow-wrap: anywhere;
  }

  .keep-row .price {
    flex-shrink: 0;
    margin-left: auto;
  }

  .typeword {
    display: grid;
    gap: 0.25rem;
    font-size: 0.85rem;
  }

  .typeword input {
    max-width: 14rem;
    text-transform: uppercase;
  }
</style>
