<script lang="ts">
  import { formatNumber, numberTitle } from './format';
  // Modul: what this session actually produced, in TWO lists.
  //
  // Reported as "in all that time nothing better than Rare dropped from the Ice
  // Bat, and I have 23,804 kills". The database disagreed - that account holds
  // 144 Legendary, 53 Mythic, 13 Relic and 9 Ancient, and had taken a Relic
  // recently. The drops were real. This panel had thrown them away.
  //
  // One shared ring buffer held both kinds. With two characters gathering, a
  // material drop lands every few seconds and the whole buffer turned over in
  // about four minutes, evicting every piece of equipment older than that
  // whatever its rarity. The player's own diagnosis was exactly right: "with 2
  // characters on gathering the equipment probably gets overwritten straight
  // away".
  //
  // Two stores now (lootLogEquipment / lootLogMaterials), so material volume
  // cannot reach the gear, and two lists here so it cannot crowd it out
  // visually either.
  //
  // Within each list, sorted by RARITY descending rather than by time. A
  // chronological feed of an idle session is a wall of Normal-tier scrap with
  // the one Legendary buried four hundred lines up - the exact thing a player
  // came back to check for. Aggregated by item and tier, because forty Iron Ore
  // is one fact.

  import { lootLogEquipment, lootLogMaterials, type LootEntry } from '../stores/game';
  import { itemName, type ContentRegistry } from '../net/content';
  import { rarityColor, shouldGlow, rarityName, rarityTitle, killsPerRarity } from './rarity';
  import Burst from './Burst.svelte';
  import DisabledReason from './DisabledReason.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState } from '../stores/game';
  import { lootFlash } from '../stores/game';
  import { queryKeys, fetchWorn } from '../net/rest';
  import { wearOnMain } from '../net/commands';
  import { play } from './audio';
  import { compareDrop, comparisonLine, dropRequirement, isUpgrade } from './lootCompare';
  import { requestScreen } from '../stores/navigation';

  interface Props {
    registry: ContentRegistry | null;
    /**
     * Modul: GATHERING HAS NO EQUIPMENT SECTION.
     *
     * A node drops materials and nothing else, so on that screen the equipment
     * list is permanently "Nothing yet." under a line quoting the odds of a
     * Legendary per KILL - a panel telling a fisherman his fishing is failing
     * at something fishing does not do. Combat shows both.
     */
    showEquipment?: boolean;
    /**
     * Modul: TASK 98 - ONE LINE ON A PHONE. On Combat this panel came before
     * the first Fight, and empty it was a heading, an odds line and two
     * "Nothing yet." - about 250px of nothing above the action. Compact, it is
     * a single line (how many pieces, how much material, the best piece) that
     * opens in place, plus a way to the Chest. Open by default on a wide
     * screen, where it has a column of its own.
     */
    compact?: boolean;
  }

  const { registry, showEquipment = true, compact = false }: Props = $props();

  const WIDE = '(min-width: 64rem)';
  // The starting state only - after that the player's tap decides. Read once,
  // deliberately: a fold that re-opened itself on a rotate would fight them.
  function initiallyOpen(): boolean {
    if (!compact) return true;
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return true;
    return window.matchMedia(WIDE).matches;
  }
  let expanded = $state(initiallyOpen());

  interface Row {
    key: string;
    itemId: number;
    qualityTier: number;
    dropKind: number;
    quantity: number;
    count: number;
    newest: number;
    /** Equipment rows only: the instances this row stands for, newest first. */
    instanceIds: number[];
  }

  function aggregate(entries: LootEntry[]): Row[] {
    const byKey = new Map<string, Row>();

    for (const entry of entries) {
      // Kind is part of the key so a material and an equipment piece that
      // happen to share an id never merge into one row.
      const key = `${entry.dropKind}:${entry.itemId}:${entry.qualityTier}`;
      const existing = byKey.get(key);
      if (existing) {
        existing.quantity += entry.quantity;
        existing.count += 1;
        existing.newest = Math.max(existing.newest, entry.atMs);
        if (entry.instanceId > 0) existing.instanceIds.push(entry.instanceId);
      } else {
        byKey.set(key, {
          key,
          itemId: entry.itemId,
          qualityTier: entry.qualityTier,
          dropKind: entry.dropKind,
          quantity: entry.quantity,
          count: 1,
          newest: entry.atMs,
          instanceIds: entry.instanceId > 0 ? [entry.instanceId] : [],
        });
      }
    }

    return [...byKey.values()].sort(
      (a, b) => b.qualityTier - a.qualityTier || b.newest - a.newest,
    );
  }

  const equipmentRows = $derived(aggregate($lootLogEquipment as LootEntry[]));
  const materialRows = $derived(aggregate($lootLogMaterials as LootEntry[]));

  // Modul: TASK 49 - WEAR IT FROM HERE. The game's main decision used to be
  // four taps away (Character -> slot -> picker -> Wear) with nothing saying
  // whether the drop beat what was worn. The row now says so and wears it.
  //
  // Wear sends EquipItem with NO TargetGuid, which the server resolves to the
  // MAIN character - so the comparison is against /player/worn, which is that
  // same character's gear. The instance id is the exact row that dropped
  // (ResponseLootDropPacket.InstanceId), never a guess among identical pieces.
  const worn = createQuery(() => ({
    queryKey: queryKeys.worn,
    queryFn: fetchWorn,
    enabled: showEquipment && equipmentRows.some((r) => r.instanceIds.length > 0),
  }));
  const wornPieces = $derived(worn.data?.Pieces ?? []);
  const wornIds = $derived(new Set(wornPieces.map((p) => p.InstanceId)));

  // Pressed but not yet confirmed. Cleared whenever the worn list refetches
  // (every command result invalidates it), so a refusal - the piece was swept,
  // or sold - brings the button back beside the toast that explains it, rather
  // than leaving a row that silently lost its button.
  let pending = $state<number[]>([]);
  $effect(() => {
    void worn.dataUpdatedAt;
    pending = [];
  });

  function requirementFor(itemId: number) {
    const def = registry?.items.get(itemId);
    return def ? dropRequirement(registry, def.BaseId, $playerState ?? {}) : null;
  }

  function comparisonFor(row: Row) {
    const def = registry?.items.get(row.itemId);
    if (!def || !worn.data) return null;
    return compareDrop(registry, def.BaseId, row.qualityTier, wornPieces);
  }

  function wear(instanceId: number) {
    pending = [...pending, instanceId];
    wearOnMain(instanceId);
    // On send, like every command: a refusal answers with the error tone.
    play('itemEquipped');
  }

  const equipmentCount = $derived(equipmentRows.reduce((n, r) => n + r.count, 0));
  const materialCount = $derived(materialRows.reduce((n, r) => n + r.quantity, 0));
  // Rows are sorted by rarity, so the first is the best piece this session.
  const bestRow = $derived(showEquipment ? (equipmentRows[0] ?? null) : null);

  // Modul: SAY HOW RARE RARE ACTUALLY IS.
  //
  // Asked, in effect, by "it's strange that nothing better than Rare dropped
  // and I have 23,804 kills". Nothing was wrong - gear drops on 15% of kills
  // and Legendary-or-better is 0.85% of those, so it is one kill in about
  // thirteen hundred. Twenty-four thousand kills is a couple of dozen, and the
  // chest sweep deletes everything up to Epic, so what a player SEES at the top
  // is whatever they last fused.
  //
  // The odds were computable all along - rarityOdds() was written, exported and
  // imported by nothing. A player counting kills against a number nobody showed
  // them will conclude the game is broken, and be reasonable about it.
  const odds = [
    { tier: 7, kills: killsPerRarity(7) },
    { tier: 10, kills: killsPerRarity(10) },
  ];
</script>

<div class="loot" class:compact>
  {#if compact}
    <div class="summary">
      <button
        class="toggle"
        aria-expanded={expanded}
        data-testid="loot-toggle"
        onclick={() => (expanded = !expanded)}
      >
        <span class="label">Loot</span>
        <span class="counts dim">
          {#if showEquipment}{formatNumber(equipmentCount)} piece{equipmentCount === 1 ? '' : 's'}{' · '}{/if}{formatNumber(materialCount)} material{materialCount === 1 ? '' : 's'}
        </span>
        {#if bestRow}
          <span class="best" style="color: {rarityColor(bestRow.qualityTier)}" title={rarityTitle(bestRow.qualityTier)}>
            {itemName(registry, bestRow.itemId)}
          </span>
        {/if}
        <span class="chev" aria-hidden="true">{expanded ? '▾' : '▸'}</span>
      </button>
      <button class="chestlink" onclick={() => requestScreen('chest')}>Chest</button>
    </div>
  {:else}
    <h2>Loot drops</h2>
  {/if}

  {#if expanded}
  {#if showEquipment}
  <section class="lootsection">
    <div class="head">
      <h3>Equipment</h3>
      {#if equipmentCount > 0}
        <span class="dim tiny">{equipmentCount} piece{equipmentCount === 1 ? '' : 's'}</span>
      {/if}
    </div>

    <p class="dim tiny odds">
      {#each odds as row, i}{i > 0 ? ' · ' : ''}{rarityName(row.tier)}+ about 1 in {formatNumber(row.kills)} kills{/each}
    </p>

    {#if equipmentRows.length === 0}
      <p class="dim tiny">Nothing yet.</p>
    {:else}
      <ul>
        {#each equipmentRows as row (row.key)}
          {@const isRare = shouldGlow(row.qualityTier)}
          {@const isWorn = row.instanceIds.some((id) => wornIds.has(id))}
          {@const target = row.instanceIds.find((id) => !wornIds.has(id) && !pending.includes(id))}
          {@const cmp = comparisonFor(row)}
          {@const req = requirementFor(row.itemId)}
          <li class="eq" class:rare={isRare} class:folk-sweep={isRare} data-loot-row={row.instanceIds[0] ?? 0}>
            <div class="line">
            <!-- Modul: A TOP-TIER DROP LOOKED LIKE EVERY OTHER LINE OF TEXT.
                 Gated on shouldGlow - tier 10 and up - for the reason that
                 function exists: an effect on every drop is an effect on none. -->
            {#if isRare}
              <span class="burstwrap">
                <Burst color={rarityColor(row.qualityTier)} reach={2.4} count={10} />
              </span>
            {/if}
            <!-- Modul: TASK 50 - a Rare+ drop bursts on its own row, at most one
                 every 5 s (lootFeel.ts). Keyed on the flash id so a second drop
                 of the same piece bursts again rather than reusing the spent
                 sparks. Hidden under reduced motion by app.css's rule. -->
            {#if $lootFlash && $lootFlash.itemId === row.itemId && $lootFlash.qualityTier === row.qualityTier}
              {#key $lootFlash.id}
                <span class="burstwrap flash">
                  <Burst color={rarityColor(row.qualityTier)} reach={1.8} count={8} />
                </span>
              {/key}
            {/if}

            <!-- Modul: NAME THE RARITY, do not only colour it.
                 The same base item at three different qualities renders as
                 three rows, and without the tier they read as duplicates of
                 one thing - on a panel whose entire purpose is telling the
                 player how good a drop was. Colour alone also fails anyone who
                 cannot separate the fourteen hues. -->
            <span class="name" style="color: {rarityColor(row.qualityTier)}" class:rarity-glow={isRare}>
              {itemName(registry, row.itemId)}
            </span>
            <span class="tier" style="color: {rarityColor(row.qualityTier)}" title={rarityTitle(row.qualityTier)}>{rarityName(row.qualityTier)}</span>
            <span class="qty">x{formatNumber(row.count)}</span>
            {#if isWorn}
              <span class="worntag">Worn</span>
            {:else if target !== undefined}
              <button
                class="wear"
                data-loot-wear={target}
                disabled={req !== null && !req.met}
                title={req && !req.met ? `This piece ${req.text}.` : 'Wear it on your main character'}
                onclick={() => wear(target)}>Wear</button>
            {/if}
            </div>
            {#if cmp && !isWorn}
              <div class="cmp" class:up={isUpgrade(cmp)} class:down={!isUpgrade(cmp)}>
                {comparisonLine(cmp)}{#if req && !req.met}<span class="unmet"> · {req.text}</span>{/if}
              </div>
            {:else if !isWorn && target !== undefined && req && !req.met}
              <!-- Modul: with no comparison line the unmet requirement was only
                   the disabled Wear button's title - invisible on a phone. -->
              <DisabledReason text="This piece {req.text}." />
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>
  {/if}

  <section class="lootsection">
    <div class="head">
      <h3>Materials</h3>
      {#if materialCount > 0}
        <span class="dim tiny" title={numberTitle(materialCount)}>{formatNumber(materialCount)}</span>
      {/if}
    </div>

    {#if materialRows.length === 0}
      <p class="dim tiny">Nothing yet.</p>
    {:else}
      <ul>
        {#each materialRows as row (row.key)}
          <li>
            <span class="name">{itemName(registry, row.itemId)}</span>
            <span class="qty" title={numberTitle(row.quantity)}>{formatNumber(row.quantity)}</span>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
  {/if}
</div>

<style>
  .loot {
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
  }

  /* The screen's own h2 is 1.05rem; this one was the browser's 1.5em and
     out-shouted the screen it sits on. */
  h2 {
    margin: 0;
    font-size: 1.05rem;
  }

  .summary {
    display: flex;
    align-items: stretch;
    gap: 0.4rem;
  }

  .toggle {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    text-align: left;
    font-size: 0.85rem;
  }

  .toggle .label {
    flex: none;
    font-weight: 600;
  }

  .toggle .counts {
    flex: none;
    font-size: 0.78rem;
    white-space: nowrap;
  }

  .toggle .best {
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 0.78rem;
  }

  .toggle .chev {
    margin-left: auto;
    flex: none;
  }

  .chestlink {
    flex-shrink: 0;
    font-size: 0.82rem;
  }

  /* Modul: NO SCROLLER INSIDE A SCROLLER ON A PHONE. Two nested 16rem boxes
     inside a page that already scrolls meant a thumb dragging the list moved
     the page or the box depending on where it landed. Opened on a phone, the
     compact panel's lists are simply as long as they are; equipment comes
     first, so material volume still cannot push it out of sight. */
  @media (max-width: 40rem) {
    .compact ul {
      max-height: none;
      overflow: visible;
    }
  }

  .lootsection {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
  }

  h3 {
    margin: 0;
    font-size: 0.9rem;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    /* Modul: each list scrolls on its OWN, so a long material run cannot push
       the equipment list off the screen - which is the display half of the
       defect the split buffers fixed. */
    max-height: 16rem;
    overflow-y: auto;

    /* Modul: SCROLL ANCHORING HID THE BEST DROP, and nudged the page on every
       kill. Both reported from a phone, and both are this one line.

       Rows are sorted by RARITY DESCENDING, so a better piece is INSERTED AT
       THE TOP. Scroll anchoring exists to stop content shifting under your
       eyes: the browser picks a node near the top of the view and, when
       something is inserted above it, raises scrollTop by exactly that height
       so the node does not move. That is the right instinct and precisely the
       wrong outcome here - the thing being pushed out of sight is the new best
       item, which is the one thing worth looking at. Screenshotted: an Ancient
       pendant sliced in half at the top edge of the list.

       The same mechanism explains the page nudging "a little with every action
       in combat". These lists grow a row at a time while they fill, and if the
       document's anchor happens to sit inside one, the page's own scroll is
       adjusted to compensate.

       `overflow-anchor: none` takes this subtree out of anchor selection, so
       nothing is "kept in place" here - the list simply shows its top, which
       is its best row. The browser then anchors the page to something that is
       not moving. */
    overflow-anchor: none;
  }

  li {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.82rem;
    padding: 0.1rem 0;
    border-bottom: 1px solid var(--border);
    /* Modul: A ROW MUST NOT SHRINK - and the BEST row was the one that did.
       The <ul> is a flex column with max-height, so once the rows outgrow it
       flex takes the difference out of any item allowed to shrink. A normal
       row is not (its minimum height is its content), but a rare row carries
       .folk-sweep, whose `overflow: hidden` makes it a scroll container, and
       a scroll container's automatic minimum is ZERO. So every glowing row -
       sorted to the TOP - was crushed to its padding (measured: 4.2px against
       19.2px for a plain row): "the top item is cut off", task 27. The list
       scrolls; its rows keep their height. Do not "fix" this by removing the
       overflow from .folk-sweep - the sweep and the sparks overshoot the row
       on purpose and must be clipped. */
    flex: none;
  }

  li.eq {
    flex-direction: column;
    align-items: stretch;
    gap: 0.1rem;
  }

  .line {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
  }

  .wear {
    flex-shrink: 0;
    font-size: 0.75rem;
    padding: 0.15rem 0.6rem;
  }

  .worntag {
    flex-shrink: 0;
    font-size: 0.72rem;
    opacity: 0.7;
  }

  .cmp {
    font-size: 0.72rem;
    opacity: 0.85;
    overflow-wrap: anywhere;
  }

  .cmp.up {
    color: var(--good);
  }

  .cmp.down {
    color: var(--text-dim);
  }

  .unmet {
    color: var(--danger);
  }

  .name {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .tier {
    font-size: 0.72rem;
    opacity: 0.85;
    flex: 0 0 auto;
    white-space: nowrap;
  }

  .qty {
    font-variant-numeric: tabular-nums;
    opacity: 0.85;
    flex: 0 0 auto;
  }

  .burstwrap {
    position: absolute;
    inset: 0;
    pointer-events: none;
  }

  .odds {
    margin: 0 0 0.15rem;
    opacity: 0.55;
  }

  .dim {
    opacity: 0.7;
  }

  .tiny {
    font-size: 0.75rem;
  }
</style>
