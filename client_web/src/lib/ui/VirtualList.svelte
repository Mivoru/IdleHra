<script lang="ts" generics="T">
  /**
   * A scrolling list that only builds the rows you can actually see.
   *
   * Modul: THIS IS WHAT MADE THE GAME LAG.
   *
   * The chest rendered `{#each equipment as item (item.Id)}` with no window at
   * all, inside a box 26rem tall. On the worst-affected live account that is
   * 17,836 keyed components - each an ItemIcon plus six buttons, so roughly
   * 180,000 DOM nodes - of which about twenty are on screen. The other 99.8%
   * were built, laid out, styled and kept in memory so they could be scrolled
   * past. Every filter keystroke rebuilt the lot.
   *
   * The rows here are UNIFORM HEIGHT and that is load-bearing: it is what lets
   * a scroll position be turned into a row index by division rather than by
   * measuring, which would mean laying the rows out to find out how tall they
   * are, which is the thing being avoided. Callers must therefore give every
   * row the same height - `rowHeight` is the contract, not a hint. A row that
   * grows taller than it (a wrapped name, say) will overlap its neighbour
   * rather than push it down, so keep row content on one line; both current
   * callers already do, with `white-space: nowrap` and an ellipsis.
   *
   * Deliberately NOT a dependency. The two libraries that do this well both
   * want to own the scroll container and the markup inside it, and this list
   * has to stay a <ul> of <li> for the screen-reader semantics the chest and
   * the item browser already had. Ninety lines is cheaper than that argument.
   */
  import type { Snippet } from 'svelte';

  let {
    items,
    rowHeight,
    gap = 4,
    maxHeight = '26rem',
    overscan = 6,
    label,
    row,
  }: {
    items: T[];
    /** Row height in CSS pixels, EXCLUDING gap. Every row must match it. */
    rowHeight: number;
    /** Vertical space between rows, in CSS pixels. */
    gap?: number;
    /** Any CSS length. The viewport's height, not the content's. */
    maxHeight?: string;
    /**
     * Rows built above and below the visible band. Without a few, a fast
     * scroll shows blank space for one frame while the new rows are created.
     */
    overscan?: number;
    label?: string;
    row: Snippet<[T, number]>;
  } = $props();

  let scrollTop = $state(0);
  let viewportHeight = $state(0);

  const stride = $derived(rowHeight + gap);

  // The scroll runway. One gap short of `count * stride`, because the last row
  // has no gap under it - otherwise the list can always be scrolled a few
  // pixels past its own end, which reads as a rendering bug.
  const totalHeight = $derived(items.length === 0 ? 0 : items.length * stride - gap);

  const firstIndex = $derived(
    Math.max(0, Math.floor(scrollTop / stride) - overscan),
  );

  // +1 for the row straddling the top edge, +1 for the one straddling the
  // bottom. Both are half-visible and both have to exist.
  const visibleCount = $derived(
    Math.ceil(viewportHeight / stride) + 2 * overscan + 2,
  );

  const lastIndex = $derived(Math.min(items.length, firstIndex + visibleCount));

  // Sliced with its absolute index carried alongside, because the caller's
  // snippet needs the real position (for a key, a number, an aria-posinset)
  // and the slice has renumbered everything from zero.
  const window = $derived(
    items.slice(firstIndex, lastIndex).map((item, offset) => ({
      item,
      index: firstIndex + offset,
    })),
  );

  function onscroll(event: Event) {
    scrollTop = (event.currentTarget as HTMLElement).scrollTop;
  }

  // Modul: measured, not assumed. `maxHeight` can be a rem value, a
  // percentage, or clamped by a flex parent, so the only honest source for how
  // many rows fit is the element itself - and it changes when the window is
  // resized or a phone is rotated, which a one-shot read at mount would miss.
  function measure(node: HTMLElement) {
    viewportHeight = node.clientHeight;

    const observer = new ResizeObserver(() => {
      viewportHeight = node.clientHeight;
    });
    observer.observe(node);

    return { destroy: () => observer.disconnect() };
  }
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_to_interactive_role -->
<ul
  class="viewport"
  style="max-height: {maxHeight}"
  aria-label={label}
  {onscroll}
  use:measure
>
  <!-- The runway. Holds the scrollbar honest at the full list's height while
       only the windowed rows exist inside it. -->
  <li class="runway" style="height: {totalHeight}px" aria-hidden="true"></li>

  {#each window as entry (entry.index)}
    <li class="slot" style="top: {entry.index * stride}px; height: {rowHeight}px">
      {@render row(entry.item, entry.index)}
    </li>
  {/each}
</ul>

<style>
  .viewport {
    list-style: none;
    margin: 0;
    padding: 0;
    position: relative;
    overflow-y: auto;
    /* Without this the browser recalculates scroll anchoring against rows that
       are being created and destroyed under it, and the list drifts while you
       scroll. */
    overflow-anchor: none;
  }

  .runway {
    /* Not display:none - it has to occupy the height. */
    width: 100%;
    pointer-events: none;
  }

  .slot {
    position: absolute;
    left: 0;
    right: 0;
    /* The scrollbar sits inside the padding box, so absolutely positioned rows
       would run under it without this. */
    box-sizing: border-box;
  }
</style>
