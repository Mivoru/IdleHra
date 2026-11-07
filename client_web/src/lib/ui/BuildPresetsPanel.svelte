<script lang="ts">
  /*
    Build presets (owner, 2026-11-07): up to three saved builds - the four
    attributes and the War half of the skill tree - so a player can keep a
    damage build and a gathering build and switch between them.

    The server stores them and applies the TREE (respec + commit, the same
    rules as the skill tree screen, checked before anything is spent). The
    ATTRIBUTES are applied here with the ordinary respec and spend commands,
    because the tick owns them; their respec is free, as it is on the panel
    above.
  */
  import {
    fetchBuildPresets,
    saveBuildPreset,
    deleteBuildPreset,
    applyBuildPresetTree,
    fetchSkillTreeState,
    type BuildPreset,
    type SkillTreeState,
  } from '../net/rest';
  import { ATTRIBUTES, respecAttributes, respecBlockedReason, spendAttributePoint } from '../net/commands';
  import { formatNumber } from './format';
  import ConfirmButton from './ConfirmButton.svelte';

  type Key = 'STR' | 'DEX' | 'CON' | 'LCK';

  let {
    values,
    unspent,
    onnotice,
  }: {
    values: Record<Key, number>;
    unspent: number;
    onnotice: (message: string, kind?: 'info' | 'error') => void;
  } = $props();

  let maxSlots = $state(3);
  let presets = $state<BuildPreset[]>([]);
  let tree = $state<SkillTreeState | null>(null);
  let loaded = $state(false);
  let busy = $state(false);
  let names = $state<Record<number, string>>({});

  async function load() {
    try {
      const [list, st] = await Promise.all([fetchBuildPresets(), fetchSkillTreeState()]);
      maxSlots = list.MaxSlots;
      presets = list.Presets;
      tree = st;
      for (const p of list.Presets) if (names[p.Slot] === undefined) names[p.Slot] = p.Name;
    } catch {
      // The panel keeps what it showed; the next action asks again.
    } finally {
      loaded = true;
    }
  }
  $effect(() => {
    void load();
  });

  const slots = $derived(Array.from({ length: maxSlots }, (_, i) => i + 1));
  const bySlot = $derived(new Map(presets.map((p) => [p.Slot, p])));

  /** What a respec of the tree costs right now: free, a grant, or gold. */
  const respecCost = $derived.by(() => {
    if (!tree) return null;
    const blocked = respecBlockedReason(tree.FreeRespecUsed, tree.PaidRespecGrants);
    if (blocked === null) return { gold: false, label: tree.FreeRespecUsed ? 'uses a respec grant' : 'uses your free respec' };
    return { gold: true, label: `costs ${formatNumber(tree.RespecGoldPrice)} gold for the respec` };
  });

  function sameAttributes(p: BuildPreset): boolean {
    return ATTRIBUTES.every((a) => (values[a.key] ?? 0) === (p[a.key] ?? 0));
  }

  function summary(p: BuildPreset): string {
    return ATTRIBUTES.map((a) => `${a.label} ${formatNumber(p[a.key] ?? 0)}`).join(' · ') + ` · tree ${formatNumber(p.PointsSpent)} pts`;
  }

  async function save(slot: number) {
    if (busy) return;
    busy = true;
    try {
      const saved = await saveBuildPreset(slot, names[slot] ?? '', { ...values });
      if (saved) {
        names[slot] = saved.Name;
        onnotice(`Saved "${saved.Name}".`, 'info');
      } else onnotice('The build could not be saved.', 'error');
      await load();
    } catch {
      onnotice('The server did not answer. Nothing was saved.', 'error');
    } finally {
      busy = false;
    }
  }

  async function remove(slot: number) {
    if (busy) return;
    busy = true;
    try {
      await deleteBuildPreset(slot);
      delete names[slot];
      await load();
    } catch {
      onnotice('The server did not answer.', 'error');
    } finally {
      busy = false;
    }
  }

  /**
   * The attributes: return every placed point (free), then place the preset's
   * numbers. A preset that wants more points than the house has (fewer levels
   * after a rebirth) is scaled down evenly; anything left stays unspent.
   */
  function applyAttributes(p: BuildPreset): string | null {
    if (sameAttributes(p)) return null;
    const placed = ATTRIBUTES.reduce((sum, a) => sum + Math.max(0, (values[a.key] ?? 0) - a.start), 0);
    const pool = unspent + placed;
    const wanted = ATTRIBUTES.map((a) => Math.max(0, (p[a.key] ?? 0) - a.start));
    const total = wanted.reduce((x, y) => x + y, 0);
    const scale = total > pool && total > 0 ? pool / total : 1;
    const amounts = wanted.map((w) => Math.floor(w * scale));

    if (placed > 0) {
      const r = respecAttributes();
      if (!r.ok) return r.reason;
    }
    let left = pool;
    for (let i = 0; i < ATTRIBUTES.length; i++) {
      if (amounts[i] <= 0) continue;
      const r = spendAttributePoint(ATTRIBUTES[i].id, amounts[i], left);
      if (!r.ok) return r.reason;
      left -= amounts[i];
    }
    return scale < 1 ? `Not enough attribute points for all of it; placed what fits.` : null;
  }

  async function apply(p: BuildPreset) {
    if (busy) return;
    busy = true;
    try {
      if (!p.TreeMatches) {
        const res = await applyBuildPresetTree(p.Slot, p.NeedsRespec && respecCost?.gold === true);
        if (res.Reason) {
          onnotice(res.Reason, 'error');
          // A respec that went through still changed the tree; the attributes
          // stay as they are so nothing else moves unasked.
          await load();
          return;
        }
      }
      const attrNote = applyAttributes(p);
      onnotice(attrNote ?? `Switched to "${p.Name}".`, attrNote ? 'error' : 'info');
      await load();
    } catch {
      onnotice('The server did not answer. Nothing was changed.', 'error');
    } finally {
      busy = false;
    }
  }
</script>

<section class="presets" data-testid="build-presets">
  <header>
    <h3>Builds</h3>
    <p class="dim tiny">
      Save your attributes and skill tree as a build and switch back to it later. The attributes switch for free; the
      skill tree switches for free when the build only adds to it, and otherwise needs a respec.
    </p>
  </header>

  {#if !loaded}
    <p class="dim tiny">Loading…</p>
  {:else}
    <div class="slots">
      {#each slots as slot (slot)}
        {@const p = bySlot.get(slot)}
        <article class="slot" class:empty={!p}>
          <input
            class="name"
            maxlength="24"
            placeholder={`Build ${slot}`}
            aria-label={`Name of build ${slot}`}
            bind:value={names[slot]}
          />
          {#if p}
            <p class="dim tiny">{summary(p)}</p>
            {#if p.TreeMatches && sameAttributes(p)}
              <p class="tiny active">This is your current build.</p>
            {:else if p.NeedsRespec && respecCost}
              <p class="tiny">Switching the tree {respecCost.label}.</p>
            {/if}
            <div class="row">
              {#if p.NeedsRespec}
                <ConfirmButton
                  small
                  danger={false}
                  label="Use"
                  confirmLabel={respecCost?.gold ? 'Pay and switch?' : 'Respec and switch?'}
                  disabled={busy || (p.TreeMatches && sameAttributes(p))}
                  onConfirm={() => apply(p)}
                />
              {:else}
                <button disabled={busy || (p.TreeMatches && sameAttributes(p))} onclick={() => apply(p)}>Use</button>
              {/if}
              <button disabled={busy} onclick={() => save(slot)}>Save current here</button>
              <button class="ghost" disabled={busy} onclick={() => remove(slot)}>Clear</button>
            </div>
          {:else}
            <p class="dim tiny">Empty.</p>
            <div class="row">
              <button disabled={busy} onclick={() => save(slot)}>Save current build here</button>
            </div>
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>

<style>
  .presets {
    margin-top: 1.25rem;
  }

  header h3 {
    margin: 0 0 0.25rem;
  }

  .slots {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 0.75rem;
    margin-top: 0.5rem;
  }

  .slot {
    border: 1px solid var(--line, rgba(255, 255, 255, 0.12));
    border-radius: 10px;
    padding: 0.75rem;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    min-width: 0;
  }

  .slot.empty {
    opacity: 0.85;
  }

  .name {
    font: inherit;
    font-weight: 600;
    width: 100%;
    min-width: 0;
    box-sizing: border-box;
  }

  .row {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .row button {
    min-height: 44px;
  }

  .active {
    color: var(--good, #7fc97f);
  }

  .ghost {
    background: transparent;
  }
</style>
