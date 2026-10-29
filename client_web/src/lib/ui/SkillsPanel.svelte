<script lang="ts">
  // Modul: THE SKILL TREE SCREEN, phase 1 of docs/design/SKILL_TREE_REWORK.md.
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
  import { onDestroy } from 'svelte';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { backgroundUrl } from './sprites';
  import DisabledReason from './DisabledReason.svelte';
  import { respecSkillTree, respecBlockedReason } from '../net/commands';
  import {
    fetchSkillTreeDefinition,
    previewSkillTree,
    commitSkillTree,
    type SkillTreeDefinition,
    type SkillTreeNodeDef,
    type SkillTreePreview,
    type StatSheetRow,
  } from '../net/rest';

  const snap = $derived($playerState);
  const points = $derived(snap?.AvailableSkillPoints ?? 0);

  // The wire still carries one byte per node in SkillTreeRegistry id order.
  const levels = $derived.by((): number[] => {
    const s = snap;
    if (!s) return new Array(20).fill(0);
    return [
      s.SkillTree_LootRarity, s.SkillTree_WorldBossDamage, s.SkillTree_CritChance,
      s.SkillTree_CritDamage, s.SkillTree_XpGain,
      s.SkillTree_Plenty, s.SkillTree_Rarity, s.SkillTree_FirstBlood,
      s.SkillTree_TrophyHunter, s.SkillTree_Guile, s.SkillTree_Relentless,
      s.SkillTree_Bloodthirst, s.SkillTree_Fortitude, s.SkillTree_Craft,
      s.SkillTree_Harvest,
      s.SkillTree_GoldenFleece, s.SkillTree_Thunderer, s.SkillTree_DoubleStrike,
      s.SkillTree_LastStand, s.SkillTree_Scholar,
    ].map((v) => Number(v) || 0);
  });

  let def = $state<SkillTreeDefinition | null>(null);
  let loadError = $state<string | null>(null);
  fetchSkillTreeDefinition()
    .then((d) => (def = d))
    .catch(() => (loadError = 'The skill tree could not be loaded. Try again in a moment.'));

  // Modul: THE DRAFT IS STORED AS ADDITIONS, not as a whole tree. Levels keep
  // arriving on the snapshot while the panel is open (a respec, or the commit
  // itself), and a draft kept as absolute levels would silently revert them.
  let extra = $state<number[]>(new Array(20).fill(0));
  const draft = $derived(levels.map((l, i) => l + (extra[i] ?? 0)));
  const draftCount = $derived(extra.reduce((a, b) => a + b, 0));

  let preview = $state<SkillTreePreview | null>(null);
  let previewing = $state(false);
  let previewTimer: ReturnType<typeof setTimeout> | null = null;
  let previewSeq = 0;

  $effect(() => {
    const target = draft;
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

  /** One limb per row: root, the two sides of its fork, its crown. */
  const rows = $derived.by(() => {
    if (!def) return [];
    return def.Columns.map((col) => {
      const of = def!.Nodes.filter((n) => n.Column === col.Id);
      return {
        col,
        root: of.find((n) => n.Kind === 'Root')!,
        boughs: of.filter((n) => n.Kind === 'Bough').sort((a, b) => a.Lane - b.Lane),
        crown: of.find((n) => n.Kind === 'Crown')!,
      };
    });
  });

  const LIMB_HUE = ['#e8c15a', '#e2674a', '#57c7e3', '#d94a6a', '#a98cf0'];
  const RUNE = ['ᚠ', 'ᛏ', 'ᛉ', 'ᚦ', 'ᚨ'];

  // ---- selection, hold and hover --------------------------------------------

  let selected = $state<number | null>(null);
  let tip = $state<{ id: number; x: number; y: number } | null>(null);
  let holdTimer: ReturnType<typeof setTimeout> | null = null;
  let held = false;

  const selectedNode = $derived(def && selected !== null ? def.Nodes[selected] : null);

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

  function onClick(id: number) {
    if (held) {
      held = false;
      return;
    }
    tip = null;
    selected = selected === id ? null : id;
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
  }

  function remove(node: SkillTreeNodeDef) {
    if ((extra[node.Id] ?? 0) <= 0) return;
    extra = extra.map((v, i) => (i === node.Id ? v - 1 : v));
  }

  function discard() {
    extra = new Array(20).fill(0);
  }

  let committing = $state(false);

  async function confirm() {
    if (draftCount === 0 || committing) return;
    committing = true;
    try {
      const res = await commitSkillTree(draft);
      if (res.Reason) {
        pushLocalNotice(res.Reason, 'error');
      } else {
        pushLocalNotice(`Learned - ${res.Spent} point${res.Spent === 1 ? '' : 's'} spent.`, 'info');
        discard();
      }
    } catch {
      pushLocalNotice('The server did not answer. Nothing was spent.', 'error');
    } finally {
      committing = false;
    }
  }

  // ---- respec (unchanged rules, see SkillTreeEngine.RespecAsync) ------------

  const freeUsed = $derived(Number(snap?.FreeRespecUsed ?? 0) > 0);
  const grants = $derived(Number(snap?.PaidRespecGrants ?? 0));
  const respecBlocked = $derived(respecBlockedReason(freeUsed, grants));
  const spentTotal = $derived(levels.reduce((a, b) => a + b, 0));
  let confirmingRespec = $state(false);

  function doRespec() {
    const outcome = respecSkillTree(freeUsed, grants);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
    confirmingRespec = false;
    discard();
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
        Tap a rune to see what it does, hold it for a quick look. Roots are cheap; each root
        <strong>forks into two and only one can be taken</strong>; a crown waits above the fork you
        chose. Nothing is spent until you confirm.
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
    <div class="grove" role="list" aria-label="Skill tree limbs">
      {#each rows as row, i (row.col.Id)}
        <div class="limb" role="listitem" style={`--hue: ${LIMB_HUE[i % LIMB_HUE.length]}`}>
          <span class="limb-name">{row.col.Name}</span>
          <div class="path">
            {#snippet rune(node: SkillTreeNodeDef, size: 'big' | 'small')}
              {@const st = statusOf(node)}
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
                onclick={() => onClick(node.Id)}
              >
                <span class="glyph" aria-hidden="true">{node.Kind === 'Crown' ? '✦' : RUNE[i % RUNE.length]}</span>
                <span class="badge">{draft[node.Id] ?? 0}/{node.MaxLevel}</span>
                {#if st === 'locked'}<span class="pad" aria-hidden="true">🔒</span>{/if}
              </button>
            {/snippet}

            {@render rune(row.root, 'big')}
            <span class="wire" class:lit={(draft[row.root.Id] ?? 0) >= 5}></span>
            <div class="fork">
              {#each row.boughs as b (b.Id)}
                <div class="fork-cell">
                  {@render rune(b, 'small')}
                  <span class="cap tiny">{b.Name}</span>
                </div>
              {/each}
            </div>
            <span class="wire" class:lit={(draft[row.crown.Id] ?? 0) > 0}></span>
            {@render rune(row.crown, 'big')}
          </div>
        </div>
      {/each}
    </div>

    {#if selectedNode}
      {@const n = selectedNode}
      {@const st = statusOf(n)}
      {@const why = canAdd(n)}
      <div class="sheet" role="dialog" aria-label={n.Name} data-testid="skill-detail">
        <div class="sheet-head">
          <strong>{n.Name}</strong>
          <span class="dim tiny">{n.Kind === 'Root' ? 'Root' : n.Kind === 'Bough' ? 'Branch (one per fork)' : 'Crown'} · {draft[n.Id] ?? 0}/{n.MaxLevel}</span>
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
          <button class="primary" disabled={why !== null} onclick={() => add(n)} data-testid="skill-add">+1</button>
          <button disabled={why !== null} onclick={() => add(n, true)}>Max</button>
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
            onclick={confirm}
            data-testid="skill-confirm"
          >Confirm{preview && preview.Reason === null ? ` (${preview.Cost} pt)` : ''}</button>
        </div>
      </div>
    {/if}

    <div class="respec-row">
      {#if spentTotal === 0 && points === 0}
        <p class="dim small" data-testid="skills-earn">
          You earn a skill point every level, and two more for each chapter you seal in the Book of
          Deeds.
        </p>
      {:else if confirmingRespec}
        <span class="confirm">
          <span class="dim tiny">Refund every point and unlock both forks again?</span>
          <button onclick={doRespec}>Yes, respec</button>
          <button onclick={() => (confirmingRespec = false)}>Cancel</button>
        </span>
      {:else if spentTotal > 0}
        <button
          class="respec"
          disabled={respecBlocked !== null}
          title={respecBlocked ?? (freeUsed ? `${grants} paid respec left` : 'Your free respec for this run')}
          onclick={() => (confirmingRespec = true)}
        >
          Respec{' '}{#if !freeUsed}(free){:else}({grants} left){/if}
        </button>
        <DisabledReason text={respecBlocked} />
      {/if}
    </div>
  {/if}

  {#if tip && def}
    {@const t = def.Nodes[tip.id]}
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
    border-radius: 8px;
    padding: 1rem 1.15rem 1.25rem;
    overflow: hidden;
    isolation: isolate;
  }
  /* The painted Yggdrasil, kept from the old panel as atmosphere - darkened so
     the runes on top of it always win the contrast. */
  .panel::before {
    content: '';
    position: absolute;
    inset: 0;
    z-index: -1;
    background:
      linear-gradient(180deg, rgb(8 10 14 / 0.72), rgb(8 10 14 / 0.9)),
      var(--art) center 30% / cover no-repeat;
  }

  .head { display: flex; align-items: baseline; gap: 0.75rem; flex-wrap: wrap; }
  .head h2 { margin: 0 0 0.2rem; }
  .points {
    margin-left: auto;
    font-size: 1.4rem;
    font-weight: 700;
    color: #f3d27a;
    text-shadow: 0 0 10px rgb(243 210 122 / 0.45);
  }

  .grove { display: grid; gap: 0.6rem; margin-top: 0.75rem; }

  .limb {
    display: grid;
    grid-template-columns: 6.2rem 1fr;
    align-items: center;
    gap: 0.5rem;
    padding: 0.45rem 0.6rem;
    border-radius: 10px;
    background: linear-gradient(90deg, color-mix(in srgb, var(--hue) 14%, transparent), transparent 70%);
    border: 1px solid color-mix(in srgb, var(--hue) 28%, transparent);
  }
  .limb-name {
    font-weight: 700;
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-size: 0.8rem;
    color: var(--hue);
  }
  .path { display: flex; align-items: center; gap: 0.35rem; min-width: 0; }
  .fork { display: flex; flex-direction: column; gap: 0.3rem; }
  .fork-cell { display: flex; align-items: center; gap: 0.35rem; min-width: 0; }
  .cap { opacity: 0.8; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 6.5rem; }

  .wire {
    flex: 0 1 1.4rem;
    min-width: 0.6rem;
    height: 3px;
    border-radius: 2px;
    background: rgb(255 255 255 / 0.14);
  }
  .wire.lit {
    background: var(--hue);
    box-shadow: 0 0 8px var(--hue);
  }

  /* ---- a rune ---- */
  .rune {
    position: relative;
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
  .rune.kind-crown { border-radius: 50%; }
  .rune:active { transform: scale(0.94); }
  .glyph { font-size: 1.5rem; line-height: 1; }
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
  .rune.draft { border-style: dashed; border-color: #ffe27a; color: #ffe27a; animation: draft 1.6s ease-in-out infinite; }
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
    50% { box-shadow: 0 0 14px rgb(255 226 122 / 0.6); }
  }

  /* ---- detail sheet ---- */
  .sheet {
    margin-top: 0.8rem;
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
    position: sticky;
    bottom: 0;
    margin-top: 0.8rem;
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
  .dim { opacity: 0.75; }

  @media (max-width: 26rem) {
    .limb { grid-template-columns: 1fr; }
    .rune.big { width: 3.2rem; height: 3.2rem; }
    .rune.small { width: 2.75rem; height: 2.75rem; }
  }

  @media (prefers-reduced-motion: reduce) {
    .rune { transition: none; }
    .rune.draft { animation: none; }
  }
</style>
