<script lang="ts">
  // Modul: THE SKILL TREE SCREEN, docs/design/SKILL_TREE_REWORK.md. Phase 1
  // built it on today's tree; slice 2.5 drew the War half on it: four columns
  // of five rows, forks inside a row, and bridges that join two columns.
  //
  // Square nodes, no button on any of them. Tap a node to open its detail;
  // hold it (or hover with a mouse) for a quick tooltip. Every change is a
  // DRAFT until "Confirm" sends the whole thing in one request, so a misclick
  // never spends a point - the old panel had a Learn button per card and
  // every press was final.
  //
  // THIS FILE HOLDS NO RULE. The shape, prices and text come from
  // GET /api/v1/skilltree; which nodes a draft opens, what it costs and what
  // it does to the stats come from POST /api/v1/skilltree/preview, which runs
  // the same check the commit runs. A client that judged gates or stats itself
  // would be a second copy of the tree and a second damage model - the
  // dominant defect class of this codebase.
  //
  // The old panel's painted Yggdrasil stays, as the backdrop.
  import { onDestroy, tick } from 'svelte';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { backgroundUrl, skillIcon } from './sprites';
  import DisabledReason from './DisabledReason.svelte';
  import { respecBlockedReason } from '../net/commands';
  import { play } from './audio';
  import {
    fetchSkillTreeDefinition,
    previewSkillTree,
    commitSkillTree,
    fetchSkillTreeState,
    respecSkillTreeRest,
    type SkillTreeDefinition,
    type SkillTreeState,
    type SkillTreeNodeDef,
    type SkillTreePreview,
    type StatSheetRow,
  } from '../net/rest';

  const snap = $derived($playerState);
  const points = $derived(snap?.AvailableSkillPoints ?? 0);

  // Modul: THE RANKS COME FROM GET /api/v1/skilltree/state, not the snapshot
  // (rework slice 2.1b took twenty bytes off every frame). They are asked
  // again whenever the points or the respec bytes on the snapshot move - a
  // purchase, a commit and a respec all move at least one - and a commit's own
  // answer carries the new levels at once.
  // 64 slots, the server's rank buffer; War node ids index it (20-54).
  const SLOTS = 64;
  let levels = $state<number[]>(new Array(SLOTS).fill(0));
  let treeState = $state<SkillTreeState | null>(null);
  let stateSeq = 0;
  async function loadState() {
    const seq = ++stateSeq;
    try {
      const st = await fetchSkillTreeState();
      if (seq === stateSeq) {
        levels = st.Levels;
        treeState = st;
      }
    } catch {
      // The tree keeps its last known ranks; the next change asks again.
    }
  }
  const stamp = $derived(`${points}|${snap?.FreeRespecUsed ?? 0}|${snap?.PaidRespecGrants ?? 0}`);
  $effect(() => {
    void stamp;
    void loadState();
  });

  let def = $state<SkillTreeDefinition | null>(null);
  let loadError = $state<string | null>(null);
  fetchSkillTreeDefinition()
    .then((d) => (def = d))
    .catch(() => (loadError = 'The skill tree could not be loaded. Try again in a moment.'));

  // Modul: THE DRAFT IS STORED AS ADDITIONS, not as a whole tree. Levels keep
  // arriving on the snapshot while the panel is open (a respec, or the commit
  // itself), and a draft kept as absolute levels would silently revert them.
  let extra = $state<number[]>(new Array(SLOTS).fill(0));
  const draft = $derived(levels.map((l, i) => l + (extra[i] ?? 0)));
  const draftCount = $derived(extra.reduce((a, b) => a + b, 0));

  let preview = $state<SkillTreePreview | null>(null);
  let previewing = $state(false);
  let previewTimer: ReturnType<typeof setTimeout> | null = null;
  let previewSeq = 0;

  // Modul: KEYED ON THE CONTENT. `levels` is a fresh array on every snapshot
  // (about once a second), so an effect on `draft` itself re-asked the server
  // every second and Confirm flickered disabled while each answer was out.
  const draftKey = $derived(draft.join(','));

  $effect(() => {
    const target = draftKey.split(',').map(Number);
    if (!def) return;
    if (previewTimer) clearTimeout(previewTimer);
    previewTimer = setTimeout(async () => {
      const seq = ++previewSeq;
      previewing = true;
      try {
        const res = await previewSkillTree(target);
        if (seq === previewSeq) preview = res;
      } catch {
        if (seq === previewSeq) preview = null;
      } finally {
        if (seq === previewSeq) previewing = false;
      }
    }, 220);
  });

  onDestroy(() => {
    if (previewTimer) clearTimeout(previewTimer);
    if (holdTimer) clearTimeout(holdTimer);
  });

  const nodeState = $derived.by(() => {
    const out = new Map<number, { blocked: string | null; nextCost: number }>();
    for (const n of preview?.Nodes ?? []) out.set(n.Id, { blocked: n.Blocked, nextCost: n.NextCost });
    return out;
  });

  type Status = 'locked' | 'open' | 'owned' | 'draft' | 'maxed' | 'closed';

  function statusOf(node: SkillTreeNodeDef): Status {
    const have = draft[node.Id] ?? 0;
    if (node.Fork >= 0 && (draft[node.Fork] ?? 0) > 0 && have === 0) return 'closed';
    if (have >= node.MaxLevel) return 'maxed';
    if ((extra[node.Id] ?? 0) > 0) return 'draft';
    if (have > 0) return 'owned';
    const st = nodeState.get(node.Id);
    if (st && st.blocked === null) return 'open';
    return 'locked';
  }

  /** One card per column, its five rows in order; bridges are drawn apart. */
  const columns = $derived.by(() => {
    if (!def) return [];
    return def.Columns.map((col) => {
      const of = def!.Nodes.filter((n) => n.Column === col.Id && n.Kind !== 'Bridge');
      const tiers = def!.RowGates.map((gate, row) => ({
        row,
        gate,
        nodes: of.filter((n) => n.Row === row).sort((a, b) => a.Lane - b.Lane),
      }));
      return { col, tiers };
    });
  });
  const bridges = $derived(def ? def.Nodes.filter((n) => n.Kind === 'Bridge') : []);
  const byId = $derived(new Map((def?.Nodes ?? []).map((n) => [n.Id, n])));

  /** Points in each column on the preview's basis - the server's count. */
  const columnPoints = $derived(preview?.ColumnPoints ?? []);

  const LIMB_HUE = ['#e8c15a', '#e2674a', '#57c7e3', '#7fb069'];
  const RUNE = ['ᚠ', 'ᛏ', 'ᛉ', 'ᚦ'];
  const KIND_LABEL: Record<string, string> = {
    Minor: 'Minor',
    Notable: 'Notable',
    Fork: 'Fork (one side only)',
    Bridge: 'Bridge (two columns)',
    Keystone: 'Keystone',
  };

  // ---- selection, hold and hover --------------------------------------------

  let selected = $state<number | null>(null);
  let tip = $state<{ id: number; x: number; y: number } | null>(null);
  let holdTimer: ReturnType<typeof setTimeout> | null = null;
  let held = false;

  const selectedNode = $derived(selected !== null ? (byId.get(selected) ?? null) : null);

  // Modul: 350 ms is long enough that a scroll or a tap never trips it and
  // short enough that a player who means it does not wonder whether it works.
  // A hold only SHOWS text; tapping opens the same text and more, so nothing
  // is reachable by long-press alone.
  function onDown(e: PointerEvent, id: number) {
    held = false;
    if (e.pointerType === 'mouse') return;
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    holdTimer = setTimeout(() => {
      held = true;
      tip = { id, x: rect.left + rect.width / 2, y: rect.top };
    }, 350);
  }

  function onUp() {
    if (holdTimer) clearTimeout(holdTimer);
    holdTimer = null;
    if (held) tip = null;
  }

  let dockEl = $state<HTMLDivElement | null>(null);
  let dockHeight = $state(0);

  /** The element that actually scrolls this panel: an inner region, or the page. */
  function scrollerOf(el: HTMLElement): HTMLElement {
    for (let p = el.parentElement; p; p = p.parentElement) {
      const y = getComputedStyle(p).overflowY;
      if ((y === 'auto' || y === 'scroll') && p.scrollHeight > p.clientHeight) return p;
    }
    return (document.scrollingElement as HTMLElement) ?? document.documentElement;
  }

  // Modul: THE TAPPED LIMB STAYS ABOVE THE DOCK (owner, 2026-10-29: "the
  // panel must not reach into the last branch"). The detail sheet and the
  // draft bar stick to the bottom of the view, so tapping a rune in the lowest
  // limbs opened them on top of that very limb. After the sheet renders, the
  // view scrolls just far enough that the whole limb sits clear of the dock.
  let clearOf: HTMLElement | null = null;

  async function keepClear(button: HTMLElement) {
    clearOf = button;
    await tick();
    const limb = button.closest('.tier, .bridge') as HTMLElement | null;
    if (!limb || !dockEl) return;
    const overlap = limb.getBoundingClientRect().bottom - dockEl.getBoundingClientRect().top + 12;
    if (overlap > 0) scrollerOf(limb).scrollBy({ top: overlap, behavior: 'smooth' });
  }

  // The dock grows when the draft bar arrives under the sheet, which can
  // cover the limb again - so the same check runs whenever its height moves.
  $effect(() => {
    void dockHeight;
    if (clearOf && selected !== null) void keepClear(clearOf);
  });

  function onClick(id: number, e?: MouseEvent) {
    if (held) {
      held = false;
      return;
    }
    tip = null;
    selected = selected === id ? null : id;
    if (selected !== null && e?.currentTarget) void keepClear(e.currentTarget as HTMLElement);
    if (selected === null) clearOf = null;
    // Its own voice rather than the app-wide button click (data-no-click-sound
    // on the grove): a rune taps like wood, and a shut one knocks dull.
    const picked = selected !== null ? byId.get(selected) : undefined;
    if (picked) {
      const st = statusOf(picked);
      play(st === 'locked' || st === 'closed' ? 'runeLocked' : 'runeSelect');
    }
  }

  function onEnter(e: PointerEvent, id: number) {
    if (e.pointerType !== 'mouse') return;
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    tip = { id, x: rect.left + rect.width / 2, y: rect.top };
  }

  // ---- draft editing ----------------------------------------------------------

  function canAdd(node: SkillTreeNodeDef): string | null {
    const st = nodeState.get(node.Id);
    if (!st) return previewing ? 'Checking…' : 'Not loaded yet.';
    return st.blocked;
  }

  function add(node: SkillTreeNodeDef, many = false) {
    if (canAdd(node) !== null) return;
    const room = node.MaxLevel - (draft[node.Id] ?? 0);
    const step = many ? room : 1;
    if (step <= 0) return;
    extra = extra.map((v, i) => (i === node.Id ? v + step : v));
    play('runeAdd', many ? 0.9 : 1);
  }

  function remove(node: SkillTreeNodeDef) {
    if ((extra[node.Id] ?? 0) <= 0) return;
    extra = extra.map((v, i) => (i === node.Id ? v - 1 : v));
  }

  function discard() {
    extra = new Array(SLOTS).fill(0);
  }

  let committing = $state(false);

  async function confirmDraft() {
    if (draftCount === 0 || committing) return;
    committing = true;
    try {
      const res = await commitSkillTree(draft);
      if (res.Reason) {
        pushLocalNotice(res.Reason, 'error');
      } else {
        play('runeConfirm');
        levels = res.Levels;
        pushLocalNotice(`Learned - ${res.Spent} point${res.Spent === 1 ? '' : 's'} spent.`, 'info');
        discard();
      }
    } catch {
      pushLocalNotice('The server did not answer. Nothing was spent.', 'error');
    } finally {
      committing = false;
    }
  }

  // ---- respec (SkillTreeEngine.RespecAsync) ---------------------------------
  //
  // The free one, then a paid grant, then gold (decision D3) - the server
  // prices the gold and refuses what the player cannot pay.

  const freeUsed = $derived(Number(snap?.FreeRespecUsed ?? 0) > 0);
  const grants = $derived(Number(snap?.PaidRespecGrants ?? 0));
  const respecBlocked = $derived(respecBlockedReason(freeUsed, grants));
  const spentTotal = $derived(levels.reduce((a, b) => a + b, 0));
  const goldPrice = $derived(treeState?.RespecGoldPrice ?? 0);
  const goldHeld = $derived(Number(snap?.Gold ?? treeState?.Gold ?? 0));
  const withGold = $derived(respecBlocked !== null);
  const goldShort = $derived(withGold && goldHeld < goldPrice ? `A respec costs ${goldPrice.toLocaleString()} gold; you have ${goldHeld.toLocaleString()}.` : null);
  let confirmingRespec = $state(false);
  let respecing = $state(false);

  async function doRespec() {
    if (respecing) return;
    respecing = true;
    try {
      const res = await respecSkillTreeRest(withGold);
      if (res.Reason) pushLocalNotice(res.Reason, 'error');
      else {
        pushLocalNotice(
          `Respec done - ${res.Refunded} point${res.Refunded === 1 ? '' : 's'} back${res.GoldSpent > 0 ? ` for ${res.GoldSpent.toLocaleString()} gold` : ''}.`,
          'info',
        );
        void loadState();
      }
    } catch {
      pushLocalNotice('The server did not answer. Nothing was refunded.', 'error');
    } finally {
      respecing = false;
      confirmingRespec = false;
      discard();
    }
  }

  // ---- the stat preview ----------------------------------------------------

  /** Rows whose value the draft moves, from the server's two sheets. */
  const changes = $derived.by(() => {
    const before = preview?.Before;
    const after = preview?.After;
    if (!before || !after || draftCount === 0) return [];
    const old = new Map<string, StatSheetRow>();
    for (const s of before.Sections) for (const r of s.Rows) old.set(r.Key, r);
    const out: { label: string; from: string; to: string; up: boolean }[] = [];
    for (const s of after.Sections) {
      for (const r of s.Rows) {
        const o = old.get(r.Key);
        if (!o || Math.abs(o.Value - r.Value) < 1e-6) continue;
        out.push({ label: r.Label, from: fmt(o), to: fmt(r), up: r.Value > o.Value });
      }
    }
    return out;
  });

  function fmt(r: StatSheetRow): string {
    const v = r.Value;
    switch (r.Unit) {
      case 'pct': return `${v.toFixed(1)}%`;
      case 'mult': return `×${v.toFixed(2)}`;
      case 's': return `${v.toFixed(2)} s`;
      case 'per_s': return `${v.toFixed(1)}/s`;
      default: return Math.round(v).toLocaleString();
    }
  }

  function effectNow(node: SkillTreeNodeDef): string {
    const l = draft[node.Id] ?? 0;
    return l > 0 ? node.Effects[l - 1] : '—';
  }

  function effectNext(node: SkillTreeNodeDef): string | null {
    const l = draft[node.Id] ?? 0;
    return l < node.MaxLevel ? node.Effects[l] : null;
  }
</script>

<section class="panel skills" style={`--art: url(${backgroundUrl('yggdrasil')})`}>
  <header class="head">
    <div>
      <h2>Skill tree</h2>
      <p class="dim small">
        Tap a rune to see what it does, hold it for a quick look. Four columns of five rows: a row
        opens once its column holds enough points, a <strong>fork</strong> lets you take only one
        side, and a <strong>bridge</strong> joins two columns. Nothing is spent until you confirm.
      </p>
    </div>
    <span class="points" data-testid="skills-points">
      {preview && draftCount > 0 && preview.Reason === null ? preview.PointsLeft : points}
      <span class="dim tiny">to spend</span>
    </span>
  </header>

  {#if loadError}
    <p class="error">{loadError}</p>
  {:else if !def}
    <p class="dim small">Reading the runes…</p>
  {:else}
    {#snippet rune(node: SkillTreeNodeDef, size: 'big' | 'small', ci: number)}
      {@const st = statusOf(node)}
      {@const art = skillIcon(node.Icon)}
      <div class="cell">
        <button
          class="rune {st} {size} kind-{node.Kind.toLowerCase()}"
          class:selected={selected === node.Id}
          aria-label={`${node.Name}, ${draft[node.Id] ?? 0} of ${node.MaxLevel}`}
          aria-pressed={selected === node.Id}
          data-testid={`skill-node-${node.Id}`}
          onpointerdown={(e) => onDown(e, node.Id)}
          onpointerup={onUp}
          onpointercancel={onUp}
          onpointerleave={() => { onUp(); if (tip?.id === node.Id) tip = null; }}
          onpointerenter={(e) => onEnter(e, node.Id)}
          oncontextmenu={(e) => e.preventDefault()}
          onclick={(e) => onClick(node.Id, e)}
        >
          {#if art}
            <img class="art" src={art} alt="" draggable="false" loading="lazy" />
          {:else}
            <span class="glyph" aria-hidden="true">{node.Kind === 'Keystone' ? '✦' : RUNE[ci % RUNE.length]}</span>
          {/if}
          <span class="badge">{draft[node.Id] ?? 0}/{node.MaxLevel}</span>
          {#if st === 'locked'}<span class="pad" aria-hidden="true">🔒</span>{/if}
        </button>
        <span class="cap tiny">{node.Name}</span>
      </div>
    {/snippet}

    <div class="grove" role="list" aria-label="Skill tree columns" data-no-click-sound>
      {#each columns as column, ci (column.col.Id)}
        <div class="column" role="listitem" style={`--hue: ${LIMB_HUE[ci % LIMB_HUE.length]}`} data-testid={`skill-column-${column.col.Id}`}>
          <span class="limb-name">
            {#if skillIcon(column.col.Icon)}<img class="emblem" src={skillIcon(column.col.Icon)} alt="" draggable="false" />{/if}
            {column.col.Name}
            <span class="col-points tiny">{columnPoints[column.col.Id] ?? 0} pts</span>
          </span>
          <div class="tiers">
            {#each column.tiers as tier (tier.row)}
              {@const open = (columnPoints[column.col.Id] ?? 0) >= tier.gate}
              <div class="tier" class:shut={!open}>
                {#if tier.gate > 0}
                  <span class="gate tiny" class:met={open}>{open ? '✓' : '🔒'} {tier.gate}</span>
                {/if}
                <div class="tier-nodes">
                  {#each tier.nodes as node, k (node.Id)}
                    {#if k > 0 && node.Kind === 'Fork'}<span class="or tiny" aria-hidden="true">or</span>{/if}
                    {@render rune(node, node.Lane < 0 ? 'big' : 'small', ci)}
                  {/each}
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/each}
    </div>

    {#if bridges.length > 0}
      <div class="bridges" role="list" aria-label="Bridges between columns" data-no-click-sound>
        <span class="limb-name bridges-name">Bridges <span class="dim tiny">· {def.BridgeGate} points in both columns</span></span>
        {#each bridges as b (b.Id)}
          <div class="bridge" role="listitem" style={`--hue: ${LIMB_HUE[b.Column % LIMB_HUE.length]}; --hue2: ${LIMB_HUE[b.BridgeColumn % LIMB_HUE.length]}`}>
            <span class="tiny bridge-cols">{def.Columns[b.Column]?.Name} + {def.Columns[b.BridgeColumn]?.Name}</span>
            {@render rune(b, 'small', b.Column)}
          </div>
        {/each}
      </div>
    {/if}

    <div class="dock" bind:this={dockEl} bind:clientHeight={dockHeight}>
    {#if selectedNode}
      {@const n = selectedNode}
      {@const st = statusOf(n)}
      {@const why = canAdd(n)}
      <div class="sheet" role="region" aria-label={n.Name} data-testid="skill-detail">
        <div class="sheet-head">
          <strong>{n.Name}</strong>
          <span class="dim tiny">{KIND_LABEL[n.Kind] ?? n.Kind} · {draft[n.Id] ?? 0}/{n.MaxLevel}</span>
          <button class="x" aria-label="Close" onclick={() => (selected = null)}>✕</button>
        </div>
        <p class="dim small">{n.Blurb}</p>
        <dl>
          <dt>Now</dt><dd>{effectNow(n)}</dd>
          {#if effectNext(n)}
            <dt>Next</dt><dd>{effectNext(n)} <span class="dim tiny">· {nodeState.get(n.Id)?.nextCost ?? n.Costs[draft[n.Id] ?? 0]} pt</span></dd>
          {/if}
        </dl>
        {#if st === 'closed'}
          <p class="dim tiny">The other side of this fork was taken. A respec opens it again.</p>
        {/if}
        <div class="sheet-actions">
          <button disabled={(extra[n.Id] ?? 0) <= 0} onclick={() => remove(n)} aria-label="Remove one drafted level">−1</button>
          <button class="primary" disabled={why !== null} onclick={() => add(n)} data-testid="skill-add" data-no-click-sound>+1</button>
          <button disabled={why !== null} onclick={() => add(n, true)} data-no-click-sound>Max</button>
        </div>
        <DisabledReason text={st === 'maxed' || st === 'closed' ? null : why} />
      </div>
    {/if}

    {#if draftCount > 0}
      <div class="draftbar" data-testid="skill-draftbar">
        <div class="draft-sum">
          <strong>{draftCount} level{draftCount === 1 ? '' : 's'} drafted</strong>
          {#if preview?.Reason}
            <span class="bad tiny">{preview.Reason}</span>
          {:else if preview}
            <span class="dim tiny">costs {preview.Cost} · {preview.PointsLeft} left</span>
          {/if}
        </div>
        {#if changes.length > 0}
          <ul class="changes" data-testid="skill-preview">
            {#each changes as c (c.label)}
              <li><span>{c.label}</span> <span class="dim">{c.from}</span> → <span class:up={c.up} class:down={!c.up}>{c.to}</span></li>
            {/each}
          </ul>
        {/if}
        <div class="draft-actions">
          <button onclick={discard}>Discard</button>
          <button
            class="primary"
            disabled={committing || previewing || !preview || preview.Reason !== null}
            onclick={confirmDraft}
            data-no-click-sound
            data-testid="skill-confirm"
          >Confirm{preview && preview.Reason === null ? ` (${preview.Cost} pt)` : ''}</button>
        </div>
      </div>
    {/if}
    </div>

    <div class="respec-row">
      {#if spentTotal === 0 && points === 0}
        <p class="dim small" data-testid="skills-earn">
          You earn a skill point every level, and two more for each chapter you seal in the Book of
          Deeds.
        </p>
      {:else if confirmingRespec}
        <span class="confirm">
          <span class="dim tiny">
            Refund every point and open every fork again{withGold ? ` for ${goldPrice.toLocaleString()} gold` : ''}?
          </span>
          <button onclick={doRespec} disabled={respecing} data-testid="skill-respec-yes">Yes, respec</button>
          <button onclick={() => (confirmingRespec = false)}>Cancel</button>
        </span>
      {:else if spentTotal > 0}
        <button
          class="respec"
          disabled={goldShort !== null}
          title={withGold ? `${goldPrice.toLocaleString()} gold` : freeUsed ? `${grants} paid respec left` : 'Your free respec for this run'}
          onclick={() => (confirmingRespec = true)}
          data-testid="skill-respec"
        >
          Respec{' '}{#if !freeUsed}(free){:else if grants > 0}({grants} left){:else}({goldPrice.toLocaleString()} gold){/if}
        </button>
        <DisabledReason text={goldShort} />
      {/if}
    </div>
  {/if}

  <!-- Room to scroll the last limb above the dock: the dock sticks to the
       bottom of the view, so without this the lowest limbs could never be
       brought clear of it. -->
  {#if selectedNode || draftCount > 0}
    <div class="dock-room" style={`height: ${dockHeight}px`} aria-hidden="true"></div>
  {/if}

  {#if tip && byId.get(tip.id)}
    {@const t = byId.get(tip.id)!}
    <div class="tip" role="tooltip" style={`left: ${tip.x}px; top: ${tip.y}px`}>
      <strong>{t.Name}</strong>
      <span class="tiny">{effectNow(t)}{#if effectNext(t)} → {effectNext(t)}{/if}</span>
      {#if nodeState.get(t.Id)?.blocked && statusOf(t) === 'locked'}
        <span class="tiny bad">{nodeState.get(t.Id)?.blocked}</span>
      {/if}
    </div>
  {/if}
</section>

<style>
  .panel {
    position: relative;
    /* Modul: an explicit ink. The page theme is parchment with dark text, and
       inherited dark text on this darkened painting read as nothing at all. */
    color: #ece6d8;
    border-radius: 8px;
    padding: 1rem 1.15rem 1.25rem;
    overflow: hidden;
    isolation: isolate;
  }
  /* The painted Yggdrasil, kept from the old panel as atmosphere - darkened so
     the runes on top of it always win the contrast. */
  /* Modul: A FIXED-HEIGHT LAYER, NOT `cover` OVER THE WHOLE PANEL (owner,
     2026-10-29: "the tree moves a little every time I tap a skill"). Sized to
     the panel, the painting re-scaled whenever the detail sheet or the draft
     bar changed the panel's height. Pinned to the top at its own height, it
     never moves; below it the panel is the painting's own dark. */
  .panel {
    background: #0b0d12;
  }
  .panel::before {
    content: '';
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    height: 46rem;
    z-index: -1;
    pointer-events: none;
    background:
      linear-gradient(180deg, rgb(8 10 14 / 0.72), rgb(8 10 14 / 0.86) 70%, #0b0d12),
      var(--art) center top / cover no-repeat;
  }

  .head { display: flex; align-items: baseline; gap: 0.75rem; flex-wrap: wrap; }
  .head h2 { margin: 0 0 0.2rem; color: #f6efe0; }
  .points {
    margin-left: auto;
    font-size: 1.4rem;
    font-weight: 700;
    color: #f3d27a;
    text-shadow: 0 0 10px rgb(243 210 122 / 0.45);
  }

  .grove { display: grid; gap: 0.6rem; margin-top: 0.75rem; }

  .column {
    display: grid;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem 0.6rem;
    border-radius: 10px;
    background: linear-gradient(180deg, color-mix(in srgb, var(--hue) 14%, transparent), transparent 80%);
    border: 1px solid color-mix(in srgb, var(--hue) 28%, transparent);
  }
  .col-points { margin-left: auto; color: #f3ecdc; letter-spacing: 0; text-transform: none; font-weight: 600; }
  .tiers { display: grid; gap: 0.35rem; }
  .tier {
    display: grid;
    grid-template-columns: 2.6rem 1fr;
    align-items: center;
    gap: 0.4rem;
    padding: 0.25rem 0;
    border-top: 1px solid rgb(255 255 255 / 0.07);
  }
  .tier:first-child { border-top: none; }
  .tier:first-child .tier-nodes { grid-column: 1 / -1; }
  .gate { color: #c9c0ad; white-space: nowrap; }
  .gate.met { color: var(--hue); }
  .tier-nodes { display: flex; align-items: flex-start; gap: 0.5rem; flex-wrap: wrap; min-width: 0; }
  .cell { display: grid; justify-items: center; gap: 0.2rem; min-width: 0; }
  .or { align-self: center; opacity: 0.7; }

  .bridges {
    display: flex;
    flex-wrap: wrap;
    gap: 0.6rem;
    align-items: flex-end;
    margin-top: 0.6rem;
    padding: 0.5rem 0.6rem;
    border-radius: 10px;
    border: 1px dashed rgb(255 255 255 / 0.22);
  }
  .bridges-name { flex-basis: 100%; color: #f3ecdc; }
  .bridge {
    display: grid;
    justify-items: center;
    gap: 0.25rem;
    padding: 0.35rem 0.5rem;
    border-radius: 8px;
    background: linear-gradient(90deg, color-mix(in srgb, var(--hue) 18%, transparent), color-mix(in srgb, var(--hue2) 18%, transparent));
  }
  .bridge-cols { color: #ece6d8; white-space: nowrap; }
  .limb-name {
    display: flex;
    align-items: center;
    min-height: 1.9rem;
    font-weight: 700;
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 0.8rem;
    color: var(--hue);
  }
  .cap { opacity: 0.8; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 5.5rem; text-align: center; }

  /* ---- a rune ---- */
  .rune {
    position: relative;
    overflow: visible;
    flex: none;
    display: grid;
    place-items: center;
    padding: 0;
    border-radius: 10px;
    border: 2px solid rgb(255 255 255 / 0.18);
    background: radial-gradient(circle at 50% 35%, rgb(255 255 255 / 0.08), rgb(0 0 0 / 0.55));
    color: rgb(255 255 255 / 0.55);
    cursor: pointer;
    touch-action: manipulation;
    -webkit-touch-callout: none;
    user-select: none;
    transition: transform 0.12s ease, box-shadow 0.2s ease, border-color 0.2s ease;
  }
  .rune.big { width: 3.6rem; height: 3.6rem; }
  .rune.small { width: 3rem; height: 3rem; }
  .rune.kind-keystone { border-radius: 50%; }
  .rune.kind-bridge { border-radius: 50% 10px; }
  .rune:active { transform: scale(0.94); }
  .glyph { font-size: 1.5rem; line-height: 1; }
  /* Painted icons fill the tile; the border, badge and glow stay the client's
     so every state reads the same with or without art. */
  .art {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: cover;
    border-radius: inherit;
    pointer-events: none;
  }
  .rune.locked .art { filter: grayscale(0.85) brightness(0.6); }
  .rune.closed .art { filter: grayscale(1) brightness(0.45); }
  .rune.open .art { filter: saturate(0.75) brightness(0.85); }
  .rune .badge, .rune .pad { z-index: 1; }
  .emblem {
    width: 1.9rem;
    height: 1.9rem;
    border-radius: 50%;
    object-fit: cover;
    vertical-align: middle;
    margin-right: 0.35rem;
    box-shadow: 0 0 8px color-mix(in srgb, var(--hue) 60%, transparent);
  }
  .rune.small .glyph { font-size: 1.2rem; }
  .badge {
    position: absolute;
    right: -0.35rem;
    bottom: -0.35rem;
    min-width: 1.6rem;
    padding: 0 0.25rem;
    border-radius: 999px;
    font-size: 0.68rem;
    font-weight: 700;
    line-height: 1.15rem;
    background: #11141a;
    border: 1px solid rgb(255 255 255 / 0.25);
    color: #fff;
  }
  .pad { position: absolute; top: -0.4rem; left: -0.4rem; font-size: 0.7rem; }

  .rune.open { border-color: #e7b84f; color: #f3d27a; box-shadow: 0 0 0 1px rgb(231 184 79 / 0.3); }
  .rune.owned { border-color: var(--hue); color: #fff; box-shadow: 0 0 10px color-mix(in srgb, var(--hue) 55%, transparent); }
  .rune.draft { border-style: dashed; border-color: #ffe27a; color: #ffe27a; }
  /* The pulse is a fixed glow whose OPACITY moves - the compositor animates
     that alone; a pulsing box-shadow repainted the rune every frame. */
  .rune.draft::after {
    content: '';
    position: absolute;
    inset: -2px;
    border-radius: inherit;
    box-shadow: 0 0 14px rgb(255 226 122 / 0.6);
    opacity: 0;
    pointer-events: none;
    animation: draft 1.6s ease-in-out infinite;
  }
  .rune.maxed {
    border-color: var(--hue);
    color: #fff;
    background: radial-gradient(circle at 50% 35%, color-mix(in srgb, var(--hue) 45%, transparent), rgb(0 0 0 / 0.5));
    box-shadow: 0 0 16px var(--hue), inset 0 0 10px color-mix(in srgb, var(--hue) 50%, transparent);
  }
  .rune.closed { opacity: 0.38; }
  .rune.closed .glyph { text-decoration: line-through; }
  .rune.locked { opacity: 0.72; }
  .rune.selected { outline: 2px solid #fff; outline-offset: 2px; }

  @keyframes draft {
    50% { opacity: 1; }
  }

  /* ---- detail sheet ---- */
  /* Modul: THE DETAIL AND THE DRAFT RIDE THE BOTTOM OF THE VIEW. On a phone the
     tree is taller than the screen, so a detail drawn after it opened below the
     fold of the rune that was tapped and looked like nothing happened. */
  /* Phones: FIXED to the view, not sticky. Sticky is relative to whichever
     element scrolls, and inside the game's own scroll region it let the dock
     drift under the onboarding coach. Fixed is relative to the screen, which
     is what "above the nav and the coach" means; .dock-room keeps the last
     limb reachable above it. */
  .dock {
    position: fixed;
    left: 1rem;
    right: 1rem;
    max-height: 60vh;
    overflow-y: auto;
    /* Clear of the bottom nav and of whatever floats over the page (the
       onboarding coach, the update prompt - stores/bottomInset.ts). */
    bottom: max(calc(4.5rem + env(safe-area-inset-bottom, 0px)), var(--floating-bottom, 0px));
    z-index: 5;
    display: grid;
    gap: 0.5rem;
    margin-top: 0.8rem;
  }
  @media (min-width: 60rem) {
    .dock {
      position: sticky;
      left: auto;
      right: auto;
      max-height: none;
      overflow: visible;
      bottom: max(0.5rem, var(--floating-bottom, 0px));
    }
    /* Wide screens hold the whole tree above the dock already. */
    .dock-room { display: none; }
  }

  .sheet {
    padding: 0.75rem 0.85rem;
    border-radius: 10px;
    background: rgb(12 14 20 / 0.92);
    border: 1px solid rgb(255 255 255 / 0.16);
  }
  .sheet-head { display: flex; align-items: baseline; gap: 0.5rem; flex-wrap: wrap; }
  .sheet-head .x { margin-left: auto; min-width: 2.75rem; min-height: 2.75rem; }
  .sheet p { margin: 0.3rem 0 0.4rem; }
  dl { display: grid; grid-template-columns: auto 1fr; gap: 0.15rem 0.6rem; margin: 0.2rem 0 0.5rem; }
  dt { opacity: 0.7; font-size: 0.85rem; }
  dd { margin: 0; font-size: 0.9rem; }
  .sheet-actions, .draft-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
  .sheet-actions button, .draft-actions button { min-height: 2.75rem; min-width: 3.5rem; }
  .primary { font-weight: 700; }

  /* ---- draft bar ---- */
  .draftbar {
    padding: 0.7rem 0.85rem;
    border-radius: 10px;
    background: rgb(30 26 12 / 0.95);
    border: 1px dashed #ffe27a;
    display: grid;
    gap: 0.5rem;
  }
  .draft-sum { display: flex; gap: 0.6rem; flex-wrap: wrap; align-items: baseline; }
  .changes { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.15rem; font-size: 0.85rem; }
  .up { color: #7fe08a; font-weight: 700; }
  .down { color: #ff8a7a; font-weight: 700; }
  .bad { color: #ff8a7a; }
  .error { color: #ff8a7a; }

  .respec-row { display: flex; gap: 0.6rem; flex-wrap: wrap; align-items: center; margin-top: 0.8rem; }
  .confirm { display: flex; gap: 0.5rem; flex-wrap: wrap; align-items: center; }

  .tip {
    position: fixed;
    z-index: 50;
    transform: translate(-50%, calc(-100% - 8px));
    max-width: min(16rem, calc(100vw - 2rem));
    display: grid;
    gap: 0.15rem;
    padding: 0.45rem 0.6rem;
    border-radius: 8px;
    background: #0d1016;
    border: 1px solid rgb(255 255 255 / 0.22);
    box-shadow: 0 6px 18px rgb(0 0 0 / 0.5);
    pointer-events: none;
  }

  .small { font-size: 0.9rem; max-width: 46rem; }
  .tiny { font-size: 0.8rem; }
  /* The app's global .dim is a dark ink for parchment; on this panel it has to
     be a light one. */
  .panel .dim { color: #c9c0ad; opacity: 1; }

  /* Modul: THE PANEL'S BUTTONS ARE ITS OWN, not the theme's. The panel is dark
     in either Appearance scheme, and the theme's buttons are not: forcing a
     dark ink made them readable on the light scheme and blank on the dark one
     (owner, 2026-10-29: "the buttons at the bottom have no labels"). So they
     carry their own surface and ink, in both schemes alike. */
  .panel .sheet button,
  .panel .draft-actions button,
  .panel .respec-row button {
    color: #f3ecdc;
    background: rgb(255 255 255 / 0.08);
    border: 1px solid rgb(255 255 255 / 0.28);
    border-radius: 8px;
    font-weight: 600;
  }
  .panel .sheet button:hover:not(:disabled),
  .panel .draft-actions button:hover:not(:disabled),
  .panel .respec-row button:hover:not(:disabled) {
    background: rgb(255 255 255 / 0.16);
  }
  .panel .sheet button.primary,
  .panel .draft-actions button.primary {
    color: #fff;
    background: #6d45c4;
    border-color: #9a78ea;
  }
  .panel .sheet button.primary:hover:not(:disabled),
  .panel .draft-actions button.primary:hover:not(:disabled) {
    background: #7d55d6;
  }
  .panel .sheet button:disabled,
  .panel .draft-actions button:disabled,
  .panel .respec-row button:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  /* Wide screens: four columns standing side by side, the first row at the
     foot and the keystone at the top, so the screen reads as the tree it is
     named after. */
  @media (min-width: 60rem) {
    .grove { grid-template-columns: repeat(4, minmax(0, 1fr)); align-items: stretch; }
    .column { background: linear-gradient(0deg, color-mix(in srgb, var(--hue) 16%, transparent), transparent 80%); }
    .tiers { display: flex; flex-direction: column-reverse; gap: 0.35rem; }
    .tier { grid-template-columns: 1fr; justify-items: center; border-top: none; border-bottom: 1px solid rgb(255 255 255 / 0.07); }
    .tier:first-child { border-bottom: none; }
    .tier-nodes { justify-content: center; }
    .rune.big { width: 4rem; height: 4rem; }
    .rune.small { width: 3.3rem; height: 3.3rem; }
    .bridges { justify-content: center; }
  }

  @media (max-width: 26rem) {
    .tier { grid-template-columns: 2.3rem 1fr; }
    .rune.big { width: 3.2rem; height: 3.2rem; }
    .rune.small { width: 2.9rem; height: 2.9rem; }
  }

  @media (prefers-reduced-motion: reduce) {
    .rune { transition: none; }
    .rune.draft::after { animation: none; }
  }
</style>
