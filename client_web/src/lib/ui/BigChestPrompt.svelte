<script lang="ts">
  // Modul: THE CHEST SAYS WHEN IT IS TOO BIG (owner, 2026-10-20). One live
  // account held 243,096 pieces; every inventory download for it was tens of
  // megabytes that phones gave up on half way, and equipping on top of that was
  // "too slow and laggy, I had to restart". Auto-salvage existed and was off,
  // and the Chest's sweep only knew a rarity - nothing told the player the two
  // were the cure, or which part of the pile was safe to let go.
  //
  // So past BIG_CHEST_PIECES this asks once (per device, again after
  // ASK_AGAIN_DAYS): regions x rarity, combined - "regions 1 to 3, Epic or
  // lower" - with the count that choice takes and what the download weighs.
  // Nothing is forced: a player keeping low pieces for the Vault closes it.
  // The numbers are GET /api/v1/chest/summary, a GROUP BY that never ships the
  // rows - the prompt must not cost what it warns about.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { pendingNotes } from '../stores/version';
  import { offlineSummary, playerState, pushLocalNotice } from '../stores/game';
  import { onboardingDismissed } from '../stores/tutorial';
  import { guidedStage } from '../stores/guided';
  import {
    bulkClearChest,
    fetchChestSettings,
    fetchChestSummary,
    queryKeys,
    saveChestSettings,
  } from '../net/rest';
  import { invalidateOwnedItems } from '../net/queryClient';
  import { formatGold, formatNumber } from './format';
  import { rarityColor, rarityName } from './rarity';
  import Modal from './Modal.svelte';
  import { EVENT_PHASE, eventThemeKey } from '../net/seasonalEvent';
  import { eventIntroClosed, eventIntroSeen } from './eventIntroSeen';

  /** Past this many pieces the download is ~0.5 MB and the screens feel it. */
  const BIG_CHEST_PIECES = 3000;
  const ASK_AGAIN_DAYS = 3;
  const STORAGE_KEY = 'folkidle.bigChestPrompt.dismissedAt';
  const REGIONS = [1, 2, 3, 4, 5];

  function dismissedRecently(): boolean {
    try {
      const at = Number(localStorage.getItem(STORAGE_KEY) ?? 0);
      return at > 0 && Date.now() - at < ASK_AGAIN_DAYS * 86_400_000;
    } catch {
      return false;
    }
  }

  let closed = $state(false);
  // After the seasonal event's introduction, never on top of it.
  const introKey = $derived(eventThemeKey(Number($playerState?.SeasonalEventId ?? 0)));
  const introPending = $derived.by(() => {
    void $eventIntroClosed; // re-read the seen key when the intro closes
    return introKey !== null
      && Number($playerState?.SeasonalEventPhase ?? 0) === EVENT_PHASE.Live
      && !eventIntroSeen(introKey);
  });
  const eligible = $derived(
    !closed && !introPending && !dismissedRecently()
      && $pendingNotes.length === 0 && $playerState !== null && $offlineSummary === null
      && guidedStage($playerState, $onboardingDismissed) === null,
  );

  const client = useQueryClient();
  const summary = createQuery(() => ({
    queryKey: ['player', 'chestSummary'] as const,
    queryFn: fetchChestSummary,
    enabled: eligible,
    staleTime: Infinity,
  }));
  const settings = createQuery(() => ({
    queryKey: queryKeys.chestSettings,
    queryFn: fetchChestSettings,
    staleTime: Infinity,
    enabled: eligible,
  }));

  const data = $derived(summary.data ?? null);
  // A failed summary shows nothing: this is an offer, not a screen, and an
  // error box about a prompt nobody asked for would be noise.
  const wanted = $derived(eligible && !summary.isError && data !== null && data.Total >= BIG_CHEST_PIECES);
  const maxTier = $derived(data?.MaxSweepableQualityTier ?? 1);

  // Default: every region but the highest one the pile holds, Normal..Rare-ish
  // (tier 4) - old regions' low rolls, the part nobody misses.
  let regions = $state<number[]>([]);
  let tier = $state(4);
  let remember = $state(false);
  let busy = $state(false);
  let seeded = false;
  $effect(() => {
    if (seeded || !data) return;
    seeded = true;
    const present = [...new Set(data.Sweepable.map((r) => r.Region))].filter((r) => r >= 1).sort();
    const top = present.at(-1) ?? 5;
    regions = REGIONS.filter((r) => r < top);
    if (regions.length === 0) regions = [top];
    tier = Math.min(4, maxTier);
  });

  const matching = $derived(
    (data?.Sweepable ?? [])
      .filter((r) => regions.includes(r.Region) && r.QualityTier <= tier)
      .reduce((n, r) => n + r.Count, 0),
  );
  const mb = (bytes: number) => (bytes / 1_048_576).toFixed(bytes < 10 * 1_048_576 ? 1 : 0);
  const afterBytes = $derived(Math.max(0, (data?.ApproxInventoryBytes ?? 0) * (1 - matching / Math.max(1, data?.Total ?? 1))));

  function toggleRegion(r: number) {
    regions = regions.includes(r) ? regions.filter((x) => x !== r) : [...regions, r].sort();
  }

  function close() {
    closed = true;
    try {
      localStorage.setItem(STORAGE_KEY, String(Date.now()));
    } catch {
      // Storage blocked: it asks again next session, nothing worse.
    }
  }

  async function sell() {
    if (matching === 0 || busy) return;
    busy = true;
    try {
      const result = await bulkClearChest(tier, true, regions);
      if (!result || result.Success === false) {
        pushLocalNotice('Could not clear the chest.', 'error');
        return;
      }
      if (remember) {
        // Auto-sell the same choice from now on: each chosen region's floor
        // rises to this rarity (never lowered - the server takes the larger).
        const current = settings.data;
        const floors = [...(current?.AutoSalvageRegionTiers ?? [0, 0, 0, 0, 0])];
        while (floors.length < 5) floors.push(0);
        for (const r of regions) floors[r - 1] = Math.max(floors[r - 1] ?? 0, tier);
        const saved = await saveChestSettings(current?.AutoSalvageBelowTier ?? 0, floors);
        if (saved) client.setQueryData(queryKeys.chestSettings, saved);
      }
      pushLocalNotice(
        `Sold ${formatNumber(result.RemovedCount)} pieces for ${formatGold(result.GoldGained)}.` +
          (remember ? ' The same will sell itself from now on.' : ''),
        'info',
      );
      invalidateOwnedItems(client);
      closed = true;
    } catch {
      pushLocalNotice('Could not reach the server.', 'error');
    } finally {
      busy = false;
    }
  }
</script>

{#if wanted && data}
  <Modal label="Your chest is very full" width="32rem" layout="block" onClose={close} dismissOnScrim={false} testid="big-chest-prompt">
    {#snippet header()}
      <div class="head">
        <h2>Your chest is very full</h2>
        <button class="close" aria-label="Close" onclick={close}>&times;</button>
      </div>
    {/snippet}
    {#snippet footer()}
      <div class="foot">
        <button class="primary" disabled={matching === 0 || busy} onclick={sell} data-testid="big-chest-sell">
          Sell {formatNumber(matching)} pieces
        </button>
        <button onclick={close} data-testid="big-chest-close">Keep everything</button>
      </div>
    {/snippet}

    <div class="body">
      <p class="lead">
        You have <strong>{formatNumber(data.Total)}</strong> pieces of gear. Every time the game opens your chest it
        downloads all of them - about <strong>{mb(data.ApproxInventoryBytes)} MB</strong> - which is what makes equipping,
        the Chest and the Forge slow, especially on a phone.
      </p>

      <h3>Which regions</h3>
      <div class="chips" role="group" aria-label="Regions">
        {#each REGIONS as r (r)}
          <label class="chip" class:on={regions.includes(r)}>
            <input type="checkbox" checked={regions.includes(r)} onchange={() => toggleRegion(r)} />
            Region {r}
          </label>
        {/each}
      </div>

      <h3>Up to which rarity</h3>
      <select bind:value={tier} aria-label="Highest rarity to sell">
        {#each Array.from({ length: maxTier }, (_, i) => i + 1) as t (t)}
          <option value={t} style="color: {rarityColor(t)}">{rarityName(t)} or lower</option>
        {/each}
      </select>

      <p class="effect" data-testid="big-chest-effect">
        That sells <strong>{formatNumber(matching)}</strong> pieces and brings the download to about
        <strong>{mb(afterBytes)} MB</strong>. Worn, locked and Vault pieces are never sold.
      </p>

      <label class="remember">
        <input type="checkbox" bind:checked={remember} />
        Sell drops like these automatically from now on (you can change it in the Chest)
      </label>
    </div>
  </Modal>
{/if}

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .head h2 {
    margin: 0;
    flex: 1;
  }

  .close {
    min-width: 44px;
    min-height: 44px;
  }

  .body h3 {
    margin: 0.9rem 0 0.3rem;
  }

  .body p {
    margin: 0.2rem 0;
  }

  .lead {
    margin-top: 0;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    min-height: 44px;
    padding: 0 0.7rem;
    border: 1px solid var(--border);
    border-radius: 999px;
    cursor: pointer;
  }

  .chip.on {
    border-color: var(--accent);
  }

  select {
    min-height: 44px;
  }

  .effect {
    margin-top: 0.8rem !important;
  }

  .remember {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    margin-top: 0.6rem;
    min-height: 44px;
  }

  .foot {
    display: flex;
    gap: 0.5rem;
    justify-content: flex-end;
    flex-wrap: wrap;
  }
</style>
