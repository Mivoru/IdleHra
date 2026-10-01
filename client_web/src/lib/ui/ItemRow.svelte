<script lang="ts">
  // Modul: ONE ROW FOR AN ITEM, two lines: the name, then what makes this
  // piece this piece - region tier, rarity and its strongest affixes (task 99,
  // reused by 106). A row that showed only a name and a rarity made two Relic
  // amulets, one of them worn, indistinguishable, which is no help with five
  // thousand of them.
  //
  // Modul: ALWAYS TWO LINES, NEVER A WRAP. The Chest renders this inside a
  // VirtualList, whose rowHeight is a contract: a row taller than the number it
  // was given overlaps the next one instead of pushing it down. Both lines are
  // single-line with an ellipsis, so the height is fixed by the font and the
  // caller's buttons, and `height: 100%` takes whatever the list hands it.
  //
  // The caller owns the controls (`actions`) and any badges (`chips`, drawn at
  // the start of the second line). Nothing here knows what a Chest is.
  import type { Snippet } from 'svelte';
  import type { AffixMap } from '../net/rest';
  import ItemIcon from './ItemIcon.svelte';
  import { rarityColor, shouldGlow } from './rarity';
  import { itemMetaLine } from './itemRow';
  import { formatNumber, numberTitle } from './format';
  import { contentRegistry } from '../net/registry.svelte';

  interface Props {
    baseItemId: string;
    name: string;
    /** 0 (the default) for things without a rarity, such as materials. */
    qualityTier?: number;
    affixes?: AffixMap | null;
    /** Extra words appended to the meta line ("Food", "Material"). */
    extra?: string;
    /** A stack count, shown right-aligned before the actions. */
    quantity?: number;
    chips?: Snippet;
    actions?: Snippet;
    selected?: boolean;
    /**
     * Clicking the row body. Pointer only: a caller that passes this must
     * also offer the same action from a real control (the Chest's "Inspect"
     * menu item), because a click handler on a div is not a keyboard path.
     */
    onSelect?: () => void;
  }

  const {
    baseItemId,
    name,
    qualityTier = 0,
    affixes = null,
    extra,
    quantity,
    chips,
    actions,
    selected = false,
    onSelect,
  }: Props = $props();

  const regionTier = $derived(contentRegistry.current?.itemsByBaseId.get(baseItemId)?.RegionTier ?? 0);
  const meta = $derived(itemMetaLine({ regionTier, qualityTier, affixes, extra }));
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="item-row" class:selected class:selectable={!!onSelect} onclick={onSelect}>
  <ItemIcon {baseItemId} {name} {qualityTier} size="sm" />
  <span class="label">
    <span
      class="name"
      style={qualityTier > 0 ? `color: ${rarityColor(qualityTier)}` : undefined}
      class:rarity-glow={shouldGlow(qualityTier)}
    >{name}</span>
    <span class="meta">
      {#if chips}<span class="chips">{@render chips()}</span>{/if}
      <span class="metatext">{meta}</span>
    </span>
  </span>
  {#if quantity !== undefined}
    <span class="qty" data-exact={quantity} title={numberTitle(quantity)}>{formatNumber(quantity)}</span>
  {/if}
  {#if actions}
    <div class="actions">{@render actions()}</div>
  {/if}
</div>

<style>
  .item-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    box-sizing: border-box;
    height: 100%;
    padding: 0.3rem 0.45rem;
    background: var(--bg-raised);
    border: 1px solid transparent;
    border-radius: var(--radius);
    font-size: 0.85rem;
  }

  .item-row.selectable {
    cursor: pointer;
  }

  .item-row.selected {
    border-color: var(--accent);
  }

  /* The only child allowed to shrink. It ellipsises rather than collapsing:
     a bare `min-width: 0` on a flex child is what once measured a Chest name
     0 wide (client_web/CLAUDE.md). */
  .label {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 0.1rem;
    line-height: 1.25;
  }

  .name,
  .metatext {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .meta {
    display: flex;
    align-items: baseline;
    gap: 0.35rem;
    min-width: 0;
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  .chips {
    display: inline-flex;
    gap: 0.3rem;
    flex-shrink: 0;
    white-space: nowrap;
  }

  .qty {
    flex-shrink: 0;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    flex-shrink: 0;
  }
</style>
