<script lang="ts">
  // Modul: THE SKILL TREE, in three rings.
  //
  // It used to be five flat branches of twenty levels, and that looked like a
  // choice without being one: every branch was a pure bonus and the cost curve
  // was nearly flat, so the best play was always "pour into the strongest, then
  // the next". An ORDERING, not an identity. Two players ended a season the
  // same shape.
  //
  // Now each root forks into two boughs and ONLY ONE MAY BE TAKEN, with a crown
  // above whichever fork was chosen. The locked side stays drawn, greyed, with
  // its name still readable - a fork whose other half is invisible is a fork
  // nobody can plan against, and the plan is the point.
  //
  // What a node is worth is stated on its card, because a passive bonus the
  // player cannot see is indistinguishable from one that does not work - a
  // mistake this project has made more than once.
  import { playerState, pushLocalNotice } from '../stores/game';
  import {
    SKILL_TREE_NODES,
    SKILL_TREE_ROOT_MAX,
    skillNodeMaxLevel,
    skillTreeUpgradeCost,
    skillNodeBlockedReason,
    purchaseSkillTreeLevel,
    respecSkillTree,
    respecBlockedReason,
    siblingBoughOf,
    boughsOfRoot,
    crownOfRoot,
  } from '../net/commands';

  const snap = $derived($playerState);
  const points = $derived(snap?.AvailableSkillPoints ?? 0);

  // The wire carries one byte per node, indexed by the same ids the server's
  // SkillTreeRegistry uses, so the two cannot drift on ordering.
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

  type NodeRow = (typeof SKILL_TREE_NODES)[number] & {
    level: number;
    max: number;
    cost: number;
    blocked: string | null;
    /** Foreclosed for the season: the other side of the fork was taken. */
    lockedOut: boolean;
    label: string;
  };

  function rowFor(node: (typeof SKILL_TREE_NODES)[number]): NodeRow {
    const level = levels[node.id] ?? 0;
    const max = skillNodeMaxLevel(node.id);
    const sibling = siblingBoughOf(node.id);
    const lockedOut = sibling >= 0 && (levels[sibling] ?? 0) > 0 && level === 0;

    const total = level * node.perLevel;
    const label =
      node.unit === 'special'
        ? level > 0
          ? 'Taken'
          : ''
        : node.unit === 'points'
          ? `+${total.toFixed(1)} pts`
          : `+${total.toFixed(1)}%`;

    return {
      ...node,
      level,
      max,
      cost: skillTreeUpgradeCost(node.id, level),
      blocked: skillNodeBlockedReason(node.id, levels, points),
      lockedOut,
      label,
    };
  }

  /** One limb: its root, both boughs, and the crown above them. */
  const limbs = $derived(
    SKILL_TREE_NODES.filter((n) => n.ring === 'root').map((root) => {
      const [a, b] = boughsOfRoot(root.id);
      return {
        root: rowFor(root),
        boughs: [rowFor(SKILL_TREE_NODES[a]), rowFor(SKILL_TREE_NODES[b])],
        crown: rowFor(SKILL_TREE_NODES[crownOfRoot(root.id)]),
      };
    }),
  );

  const spent = $derived.by(() => {
    let total = 0;
    for (const node of SKILL_TREE_NODES) {
      for (let l = 0; l < (levels[node.id] ?? 0); l++) total += skillTreeUpgradeCost(node.id, l);
    }
    return total;
  });

  // Modul: the way back. Ring 2 locks a fork for a NINETY-DAY season, so a
  // misclick without a respec is three months of regret. Limited rather than
  // free, or the exclusivity that is the whole choice would be gone.
  const freeUsed = $derived(Number(snap?.FreeRespecUsed ?? 0) > 0);
  const grants = $derived(Number(snap?.PaidRespecGrants ?? 0));
  const respecBlocked = $derived(respecBlockedReason(freeUsed, grants));
  let confirmingRespec = $state(false);

  function doRespec() {
    const outcome = respecSkillTree(freeUsed, grants);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
    confirmingRespec = false;
  }

  function buy(nodeId: number) {
    const outcome = purchaseSkillTreeLevel(nodeId, levels, points);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  // ---- the drawing ---------------------------------------------------------
  //
  // Geometry rather than art: the branch paths are computed from the levels, so
  // the picture IS the data and cannot fall out of step with the numbers beside
  // it. No files either.
  const VIEW_W = 460;
  const VIEW_H = 320;
  const ROOT_X = VIEW_W / 2;
  const ROOT_Y = VIEW_H - 14;
  const TRUNK_H = 176;

  /** Where each limb leaves the trunk and which way it grows. */
  const LIMB_LAYOUT = [
    { dx: -1.0, dy: -0.3, startY: 0.3 },
    { dx: -0.8, dy: -0.64, startY: 0.58 },
    { dx: 0.0, dy: -1.0, startY: 0.88 },
    { dx: 0.8, dy: -0.64, startY: 0.58 },
    { dx: 1.0, dy: -0.3, startY: 0.3 },
  ] as const;

  let hovered = $state<number | null>(null);

  const drawn = $derived(
    limbs.map((limb, i) => {
      const layout = LIMB_LAYOUT[i % LIMB_LAYOUT.length];
      const startX = ROOT_X;
      const startY = ROOT_Y - TRUNK_H * layout.startY;

      // A limb still grows while untaken, as a bud - the tree's SHAPE must not
      // change as it fills in, or a player cannot see what they are choosing
      // between before they choose.
      const growth = 0.74 + 0.26 * (limb.root.level / SKILL_TREE_ROOT_MAX);
      const reach = 108 * growth;
      const forkX = startX + layout.dx * reach;
      const forkY = startY + layout.dy * reach;

      // The two twigs leave the fork at a spread, one up and one out.
      const twigs = limb.boughs.map((bough, side) => {
        const spread = side === 0 ? -0.55 : 0.55;
        const twigReach = 46 + 26 * (bough.level / bough.max);
        const tipX = forkX + (layout.dx * 0.6 + spread) * twigReach;
        const tipY = forkY + (layout.dy * 0.9 - 0.35) * twigReach;
        return { bough, tipX, tipY };
      });

      const taken = twigs.find((t) => t.bough.level > 0) ?? twigs[0];

      return {
        id: limb.root.id,
        name: limb.root.name,
        level: limb.root.level,
        limbPath: `M ${startX} ${startY} Q ${startX + layout.dx * reach * 0.55} ${startY + layout.dy * reach * 0.15} ${forkX} ${forkY}`,
        width: 2 + 5 * (limb.root.level / SKILL_TREE_ROOT_MAX),
        forkX,
        forkY,
        twigs,
        crown: limb.crown,
        crownX: taken.tipX,
        crownY: taken.tipY - 16,
        // Along the limb and clear of it. A near-horizontal limb needs the
        // label beside its tip; the vertical one needs it under the fork.
        labelDx: layout.dx * 26,
        labelDy: Math.abs(layout.dy) > 0.9 ? 20 : 16,
        labelAnchor: layout.dx < -0.5 ? 'end' : layout.dx > 0.5 ? 'start' : 'middle',
      };
    }),
  );
</script>

<section class="panel">
  <header class="head">
    <div>
      <h2>Skill tree</h2>
      <p class="dim small">
        Roots are cheap and you want some of each. Each root forks into two, and
        <strong>taking one locks the other</strong> for the season. A crown sits
        above whichever fork you chose.
      </p>
    </div>
    <span class="points">{points} <span class="dim tiny">points</span></span>
  </header>

  <svg
    class="tree"
    viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
    role="img"
    aria-label="Your skill tree: five limbs, each forking into two branches with a crown above"
  >
    <path
      d={`M ${ROOT_X} ${ROOT_Y} C ${ROOT_X - 12} ${ROOT_Y - 70}, ${ROOT_X + 12} ${ROOT_Y - 130}, ${ROOT_X} ${ROOT_Y - TRUNK_H}`}
      class="trunk"
    />
    <path
      d={`M ${ROOT_X} ${ROOT_Y} l -26 10 M ${ROOT_X} ${ROOT_Y} l 26 10 M ${ROOT_X} ${ROOT_Y} l -8 12 M ${ROOT_X} ${ROOT_Y} l 9 12`}
      class="roots"
    />

    {#each drawn as limb (limb.id)}
      <path
        d={limb.limbPath}
        class="limb"
        class:lit={limb.level > 0}
        class:hot={hovered === limb.id}
        style={`stroke-width: ${limb.width}`}
      />

      {#each limb.twigs as twig (twig.bough.id)}
        <path
          d={`M ${limb.forkX} ${limb.forkY} L ${twig.tipX} ${twig.tipY}`}
          class="twig"
          class:lit={twig.bough.level > 0}
          class:dead={twig.bough.lockedOut}
        />
        <circle
          cx={twig.tipX}
          cy={twig.tipY}
          r={twig.bough.level > 0 ? 3.5 + twig.bough.level / 4 : 2.5}
          class="bud"
          class:lit={twig.bough.level > 0}
          class:dead={twig.bough.lockedOut}
        />
      {/each}

      {#if limb.crown.level > 0}
        <circle cx={limb.crownX} cy={limb.crownY} r="6" class="crown-bud" />
      {/if}

      <!-- Pushed OUTWARD along the limb rather than straight down: a label 15px
           below a near-horizontal limb lands on the limb itself, which is what
           it did to Fortune and Insight. -->
      <text
        x={limb.forkX + limb.labelDx}
        y={limb.forkY + limb.labelDy}
        class="limb-label"
        text-anchor={limb.labelAnchor}
      >
        {limb.name}{#if limb.level > 0}&#160;{limb.level}{/if}
      </text>
    {/each}
  </svg>

  <div class="respec-row">
    <p class="dim tiny spent">{spent} points invested</p>

    {#if confirmingRespec}
      <!-- Confirmed, because a respec undoes a season of decisions and the
           free one does not come back until the rollover. -->
      <span class="confirm">
        <span class="dim tiny">Refund every point and unlock both forks again?</span>
        <button onclick={doRespec}>Yes, respec</button>
        <button onclick={() => (confirmingRespec = false)}>Cancel</button>
      </span>
    {:else}
      <button
        class="respec"
        disabled={respecBlocked !== null}
        title={respecBlocked ??
          (freeUsed ? `${grants} paid respec left` : 'Your free respec this season')}
        onclick={() => (confirmingRespec = true)}
      >
        Respec{#if !freeUsed} (free){:else} ({grants} left){/if}
      </button>
    {/if}
  </div>

  <div class="limbs">
    {#each limbs as limb (limb.root.id)}
      <div
        class="limb-card"
        onmouseenter={() => (hovered = limb.root.id)}
        onmouseleave={() => (hovered = null)}
        role="group"
      >
        <!-- The root -->
        <div class="node root" class:capped={limb.root.level >= limb.root.max}>
          <div class="node-text">
            <strong>{limb.root.name} <span class="lvl">{limb.root.level}/{limb.root.max}</span></strong>
            <p class="dim small">{limb.root.blurb}</p>
            {#if limb.root.label}<span class="worth">{limb.root.label}</span>{/if}
          </div>
          <button
            disabled={limb.root.blocked !== null}
            title={limb.root.blocked ?? ''}
            onclick={() => buy(limb.root.id)}
          >
            {limb.root.cost > 0 ? `${limb.root.cost} pt` : '—'}
          </button>
        </div>

        <!-- The fork: two boughs, one of which will be locked out -->
        <div class="fork">
          {#each limb.boughs as bough (bough.id)}
            <div class="node bough" class:taken={bough.level > 0} class:locked={bough.lockedOut}>
              <div class="node-text">
                <strong>
                  {bough.name}
                  <span class="lvl">{bough.level}/{bough.max}</span>
                </strong>
                <p class="dim tiny">{bough.blurb}</p>
                {#if bough.level > 0}<span class="worth">{bough.label}</span>{/if}
                {#if bough.lockedOut}<span class="dim tiny locked-note">Foreclosed this season</span>{/if}
              </div>
              <button
                disabled={bough.blocked !== null}
                title={bough.blocked ?? ''}
                onclick={() => buy(bough.id)}
              >
                {bough.cost > 0 ? `${bough.cost} pt` : '—'}
              </button>
            </div>
          {/each}
        </div>

        <!-- The crown -->
        <div class="node crown" class:taken={limb.crown.level > 0}>
          <div class="node-text">
            <strong>♦ {limb.crown.name}</strong>
            <p class="dim tiny">{limb.crown.blurb}</p>
          </div>
          <button
            disabled={limb.crown.blocked !== null}
            title={limb.crown.blocked ?? ''}
            onclick={() => buy(limb.crown.id)}
          >
            {limb.crown.level > 0 ? 'Taken' : `${limb.crown.cost} pt`}
          </button>
        </div>
      </div>
    {/each}
  </div>
</section>

<style>
  .respec-row {
    display: flex;
    align-items: center;
    justify-content: center;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin-bottom: 0.5rem;
  }

  .respec-row .spent {
    margin: 0;
  }

  .confirm {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .respec {
    padding: 0.2rem 0.55rem;
    font-size: 0.78rem;
  }

  .head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.6rem;
  }

  .head h2 {
    margin: 0 0 0.15rem;
  }

  .head p {
    margin: 0;
    max-width: 42ch;
  }

  .points {
    flex: none;
    padding: 0.2rem 0.55rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    color: var(--brass-lit);
    font-variant-numeric: tabular-nums;
  }

  .spent {
    text-align: center;
    margin: 0 0 0.5rem;
  }

  .twig {
    fill: none;
    stroke: var(--border);
    stroke-width: 2;
    stroke-linecap: round;
  }

  .twig.lit {
    stroke: var(--brass-lit);
    stroke-width: 3;
  }

  /* Foreclosed, not absent: still drawn so the fork stays legible. */
  .twig.dead {
    stroke: var(--border);
    opacity: 0.3;
    stroke-dasharray: 3 3;
  }

  .bud.dead {
    opacity: 0.3;
  }

  .crown-bud {
    fill: var(--brass-lit);
    stroke: var(--brass);
    stroke-width: 1.5;
  }

  .limbs {
    display: grid;
    gap: 0.6rem;
  }

  .limb-card {
    display: grid;
    gap: 0.3rem;
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    min-width: 0;
  }

  .node {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    min-width: 0;
  }

  .node-text {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
  }

  .node-text p {
    margin: 0;
  }

  .node button {
    flex: none;
    margin-left: auto;
    padding: 0.22rem 0.5rem;
    font-size: 0.78rem;
    white-space: nowrap;
  }

  .lvl {
    color: var(--text-dim);
    font-weight: 400;
    font-variant-numeric: tabular-nums;
  }

  .worth {
    color: var(--brass-lit);
    font-size: 0.78rem;
    font-variant-numeric: tabular-nums;
  }

  .fork {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.3rem;
  }

  .node.taken {
    border-color: var(--brass);
    background: rgba(216, 180, 90, 0.08);
  }

  .node.locked {
    opacity: 0.45;
  }

  .locked-note {
    font-style: italic;
  }

  .node.crown {
    border-style: dashed;
  }

  .node.crown.taken {
    border-style: solid;
  }

  /* A fork side by side is unreadable under about 26rem - the two cards each
     get half of an already narrow column and the blurbs turn into one word a
     line. */
  @media (max-width: 30rem) {
    .fork {
      grid-template-columns: 1fr;
    }
  }

  .tree {
    display: block;
    width: 100%;
    max-width: 30rem;
    margin: 0.2rem auto 0.6rem;
    overflow: visible;
  }

  .trunk,
  .roots {
    fill: none;
    stroke: var(--brass);
    stroke-linecap: round;
  }

  .trunk {
    stroke-width: 9;
  }

  .roots {
    stroke-width: 3;
    opacity: 0.55;
  }

  .limb {
    fill: none;
    stroke: var(--border);
    stroke-linecap: round;
    transition: stroke 140ms ease;
  }

  /* Invested branches are brass and alive; untaken ones stay bark-coloured, so
     the tree reads as something grown rather than something unlocked. */
  .limb.lit {
    stroke: var(--brass-lit);
  }

  .limb.hot {
    stroke: var(--accent);
  }

  .bud {
    fill: var(--bg-raised);
    stroke: var(--border);
    stroke-width: 1.5;
    transition: fill 140ms ease, stroke 140ms ease;
  }

  .bud.lit {
    fill: var(--brass-lit);
    stroke: var(--brass);
  }


  .limb-label {
    fill: var(--text-dim);
    font-size: 11px;
    letter-spacing: 0.04em;
    text-transform: uppercase;
  }

  @media (prefers-reduced-motion: reduce) {
    .limb,
    .bud {
      transition: none;
    }
  }

  .panel {
    background: var(--panel, rgba(127, 127, 127, 0.05));
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 1rem 1.15rem 1.25rem;
  }

  header {
    display: flex;
    align-items: baseline;
    gap: 0.75rem;
    flex-wrap: wrap;
    margin-bottom: 0.4rem;
  }
  header .dim { margin-left: auto; }

  .small { font-size: 0.9rem; max-width: 46rem; }
  .tiny  { font-size: 0.8rem; }
  .dim   { opacity: 0.75; }



  /* A maxed branch stays fully legible - it is an achievement, not a disabled
     control, and dimming it would read as "broken". */

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
  }



</style>
