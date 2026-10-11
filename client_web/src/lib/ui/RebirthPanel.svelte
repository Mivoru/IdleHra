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
  import RebirthRoadmap from './RebirthRoadmap.svelte';

  const client = useQueryClient();
  const preview = createQuery(() => ({ queryKey: queryKeys.rebirthPreview, queryFn: fetchRebirthPreview }));
  const p = $derived(preview.data);

  let confirming = $state(false);

  // THE VAULT (owner, 2026-10-11: "where is the save you can save your
  // equipment from rebirth"). Its own section, open before the terms, so the
  // choice is visible and made calmly rather than inside the confirmation.
  // The gear the player wears first, then the rarest of the rest. Loaded only
  // once the vault is opened - the inventory is big.
  const KEEP_PAGE = 40;
  let vaultOpen = $state(false);
  let keepRows = $state(KEEP_PAGE);
  const inventory = createQuery(() => ({
    queryKey: queryKeys.inventory,
    queryFn: fetchInventory,
    enabled: vaultOpen || confirming,
  }));
  const sortedGear = $derived(
    [...(inventory.data?.Equipment ?? [])].sort(
      (a, b) => Number(b.IsEquipped) - Number(a.IsEquipped) || b.QualityTier - a.QualityTier,
    ),
  );
  const keepCandidates = $derived(sortedGear.slice(0, keepRows));
  /** Each rarity once, rarest first, for the price list. */
  const priceRows = $derived(
    (p?.KeepPriceByTier ?? []).map((price, tier) => ({ tier, price })).filter((r) => r.tier > 0).reverse(),
  );
  let keep = $state<number[]>([]);
  function priceOf(tier: number): number {
    const prices = p?.KeepPriceByTier ?? [];
    if (prices.length === 0) return 0;
    return prices[Math.max(0, Math.min(tier, prices.length - 1))];
  }
  const keepTotal = $derived(
    sortedGear.filter((e) => keep.includes(e.Id)).reduce((sum, e) => sum + priceOf(e.QualityTier), 0),
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
  {#if preview.isPending}
    <p class="dim tiny">Reading what a rebirth would do&hellip;</p>
  {:else if preview.isError || !p}
    <p class="warn">What a rebirth would do could not be loaded.</p>
  {:else}
    <!-- Where you stand. -->
    <div class="hero" data-testid="rebirth-hero">
      <div class="count">
        <span class="shield" class:max={p.Capped}>{p.RebirthCount}</span>
        <span><strong>{p.RebirthCount} of {p.MaxRebirths}</strong> rebirths taken</span>
      </div>
      <div class="stats">
        <span class="stat"><strong>{p.SkillPointsFromRebirthsNow}</strong> skill points from rebirths</span>
        <span class="stat"><strong>+{p.DamageBonusPctNow}%</strong> damage (Renown {p.RenownedRebirths})</span>
        <span class="stat"><strong>+{p.ReLevelXpBonusPct}%</strong> XP below your best level ({p.PeakLevelAfter} after the next rebirth)</span>
      </div>
    </div>

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
      Rebirths 3, 5, 7 and 10 each bring an avatar, and the tenth the Phoenix pet.
      <span class="dim">{p.RebirthCount} of {p.MaxRebirths} taken.</span>
    </p>

    <h3>The road to ten</h3>
    <RebirthRoadmap steps={p.Roadmap} taken={p.RebirthCount} />

    {#if !p.Capped}
      <!-- The Vault. -->
      <div class="vault" data-testid="rebirth-vault">
        <h3>The Vault &mdash; gear that survives</h3>
        <p class="dim small">
          A rebirth takes every piece of gear, except what you put in the Vault. Each piece costs gold by
          its rarity, paid from the gold the rebirth takes anyway, and waits in your chest, unworn, for the
          new run. You can wear it again once your attributes meet it.
        </p>
        <div class="prices" data-testid="rebirth-vault-prices">
          {#each priceRows as row (row.tier)}
            <span class="price-chip" style:--rc={rarityColor(row.tier)}>
              <span class="r">{rarityName(row.tier)}</span>
              <span>{formatNumber(row.price)}</span>
            </span>
          {/each}
        </div>
        {#if !vaultOpen && !confirming}
          <button class="vault-open" onclick={() => (vaultOpen = true)}>Choose gear to keep&hellip;</button>
        {:else}
          {#if inventory.isPending}
            <p class="dim tiny">Reading your gear&hellip;</p>
          {:else if keepCandidates.length === 0}
            <p class="dim tiny">No gear to keep.</p>
          {:else}
            <div class="keep-list" data-testid="rebirth-keep">
              {#each keepCandidates as e (e.Id)}
                <label class="keep-row">
                  <input type="checkbox" checked={keep.includes(e.Id)} disabled={working} onchange={() => toggleKeep(e.Id)} />
                  <span class="name" style="color: {rarityColor(e.QualityTier)}">{prettifyBaseId(e.BaseItemId)}</span>
                  <span class="dim tiny">[{rarityName(e.QualityTier)}]{#if e.IsEquipped} &middot; worn{/if}</span>
                  <span class="price">{formatNumber(priceOf(e.QualityTier))}</span>
                </label>
              {/each}
            </div>
            {#if sortedGear.length > keepRows}
              <button class="tiny-btn" onclick={() => (keepRows += KEEP_PAGE)}>
                Show {Math.min(KEEP_PAGE, sortedGear.length - keepRows)} more of {sortedGear.length}
              </button>
            {/if}
          {/if}
        {/if}
        <p class:warn={!keepAffordable} data-testid="rebirth-keep-total">
          Keeping {keep.length} {keep.length === 1 ? 'piece' : 'pieces'}: {formatNumber(keepTotal)} of
          {formatNumber(p.Gold)} gold.
        </p>
      </div>
    {/if}

    <h3>Rebirth now</h3>
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
        <div class="columns">
          <div class="col lose">
            <strong>You lose</strong>
            <p>
              Level {p.Level} (back to 1), {formatNumber(p.Gold)} gold,
              {p.MaterialStacks} material {p.MaterialStacks === 1 ? 'stack' : 'stacks'},
              {Math.max(0, p.EquipmentPieces - keep.length)} {p.EquipmentPieces - keep.length === 1 ? 'piece' : 'pieces'} of gear{#if p.MarketListings > 0}
                and {p.MarketListings} market {p.MarketListings === 1 ? 'listing' : 'listings'}{/if},
              {p.SkillTreeLevels} skill tree {p.SkillTreeLevels === 1 ? 'level' : 'levels'},
              {p.AttributePoints} attribute points, your potions, the chronicle pass and
              the village gene pool ({p.VillageNewcomers} {p.VillageNewcomers === 1 ? 'newcomer' : 'newcomers'}).
              Everybody goes idle.
            </p>
          </div>
          <div class="col keep">
            <strong>You keep</strong>
            <p>
              {p.HallMembersKept} ancestors and their aptitudes,
              {p.Seals} {p.Seals === 1 ? 'Seal' : 'Seals'} ({p.SkillPointsFromSeals} skill points back at once),
              {p.InheritanceLevels} Inheritance levels, {formatNumber(p.ShardBalance)} shards,
              {formatNumber(p.Diamonds)} diamonds, {p.VillageBuildingLevels} village building levels,
              every pet, avatar, frame and title, race and gathering masteries, the codex and your deeds{#if keep.length > 0},
                and the {keep.length} {keep.length === 1 ? 'piece' : 'pieces'} in the Vault{/if}.
            </p>
          </div>
          <div class="col gain">
            <strong>You gain</strong>
            <p>
              {formatNumber(p.ShardsEarned)} shards,
              {p.SkillPointsFromRebirthsAfter} skill points from rebirths, +{p.ReLevelXpBonusPct}% XP up to level
              {p.PeakLevelAfter}{#if p.Renowned}, and Renown {p.RenownedRebirths + 1} (+{p.DamageBonusPctAfter}% damage){/if}.
              {#each p.Roadmap.find((st) => st.Rebirth === p.RebirthCount + 1)?.Rewards.filter((r) => r.Kind !== 'skill_points' && r.Kind !== 'xp') ?? [] as r (r.Kind + r.Name)}
                <span class="gain-item">{r.Name}{r.Kind === 'avatar' ? ' (avatar)' : ''}.</span>
              {/each}
            </p>
          </div>
        </div>
        {#if p.HallLetGo.length > 0}
          <p class="warn">
            <strong>The Hall lets go, permanently:</strong> {p.HallLetGo.join(', ')}.
            Mark someone <em>Keep</em> in the Hall of Ancestors to change who.
          </p>
        {/if}
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

  h2,
  h3 {
    margin: 0;
    font-size: 1.05rem;
  }

  h3 {
    margin-top: 0.6rem;
    font-size: 0.95rem;
  }

  .hero {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.6rem 1.2rem;
    padding: 0.7rem;
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--accent) 10%, var(--bg));
    border: 1px solid color-mix(in srgb, var(--accent) 40%, var(--border));
  }

  .count {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .shield {
    display: inline-grid;
    place-items: center;
    /* width, not min-width: app.css's phone rule `.panel * { min-width: 0 !important }` flattens a min-width to a sliver. */
    flex: 0 0 auto;
    width: 2.8rem;
    height: 2.8rem;
    padding-bottom: 0.4rem;
    clip-path: polygon(0 0, 100% 0, 100% 62%, 50% 100%, 0 62%);
    background: color-mix(in srgb, var(--accent) 40%, var(--bg));
    font-size: 1.2rem;
    font-weight: 800;
  }

  .shield.max {
    background: linear-gradient(160deg, #fff4b0, #ffd700 45%, #b8860b);
    color: #3a2a00;
  }

  .stats {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem 0.9rem;
    font-size: 0.85rem;
  }

  .vault {
    display: grid;
    gap: 0.45rem;
    padding: 0.6rem;
    border-radius: var(--radius);
    border: 1px dashed color-mix(in srgb, var(--accent) 55%, var(--border));
  }

  .vault h3 {
    margin-top: 0;
  }

  .prices {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  .price-chip {
    display: inline-flex;
    gap: 0.35rem;
    padding: 0.1rem 0.45rem;
    border-radius: 4px;
    border: 1px solid var(--rc);
    font-size: 0.75rem;
  }

  .price-chip .r {
    color: var(--rc);
    font-weight: 700;
  }

  .vault-open {
    justify-self: start;
    flex-shrink: 0;
  }

  .columns {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(100%, 14rem), 1fr));
    gap: 0.5rem;
  }

  .col {
    display: grid;
    align-content: start;
    gap: 0.25rem;
    padding: 0.5rem;
    border-radius: var(--radius);
    border: 1px solid var(--border);
  }

  .col.lose strong {
    color: var(--danger);
  }

  .col.keep strong {
    color: var(--accent);
  }

  .col.gain strong {
    color: #7ed957;
  }

  .gain-item {
    font-weight: 700;
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
