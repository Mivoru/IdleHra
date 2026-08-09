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
  import { backgroundUrl } from './sprites';
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
  // Modul: THE PAINTING IS THE TREE. THIS IS THE CIRCUITRY ON TOP OF IT.
  //
  // This used to draw its own trunk, its own roots and five bare limbs in
  // brass, laid over the artwork - which owns a magnificent trunk and a root
  // system of its own. Two trees in the same place, and the drawn one won on
  // z-order: a fat gold smear up the middle, sticks at angles that belonged to
  // no branch in the picture, and a curve on the lower left that read as a
  // banana. It looked like stick figures over a painting because that is what
  // it was.
  //
  // The trunk and the roots are GONE - the art supplies both, better. What is
  // left is only what carries data: a joint per limb, two buds per fork, a
  // crown, and glowing connectors between them. They are positioned on the
  // crotches of the five great branch spreads in the painting rather than on a
  // geometric fan, so the lines look like they belong to the tree underneath.
  //
  // The artwork is an <image> INSIDE this svg rather than a CSS background on
  // the wrapper, which is what guarantees the two cannot drift: one coordinate
  // system, one aspect ratio, no way for a node to land off a branch because a
  // container resized.
  const ART_W = 1600;
  const ART_H = 873;
  const VIEW_W = 460;
  const VIEW_H = Math.round((VIEW_W * ART_H) / ART_W); // 251 - the art's own aspect

  // Where the trunk divides in the painting. Every connector starts here.
  const ORIGIN_X = VIEW_W / 2;
  const ORIGIN_Y = 196;

  /**
   * One anchor per limb, placed on the painting's own branch structure:
   * far-left spread, upper-left, the crown of the canopy, upper-right,
   * far-right. `out` is the direction that spread grows, which is what the
   * buds and the label follow.
   */
  const LIMB_ANCHORS = [
    { x: 78, y: 138, outX: -0.94, outY: -0.34, anchor: 'end' },
    { x: 152, y: 74, outX: -0.66, outY: -0.75, anchor: 'end' },
    { x: 230, y: 38, outX: 0, outY: -1, anchor: 'middle' },
    { x: 308, y: 74, outX: 0.66, outY: -0.75, anchor: 'start' },
    { x: 382, y: 138, outX: 0.94, outY: -0.34, anchor: 'start' },
  ] as const;

  let hovered = $state<number | null>(null);

  const drawn = $derived(
    limbs.map((limb, i) => {
      const a = LIMB_ANCHORS[i % LIMB_ANCHORS.length];

      // A limb still reaches its anchor while untaken, just short of it: the
      // SHAPE must not change as points go in, or a player cannot see what
      // they are choosing between before they choose.
      const growth = 0.82 + 0.18 * (limb.root.level / SKILL_TREE_ROOT_MAX);
      const jointX = ORIGIN_X + (a.x - ORIGIN_X) * growth;
      const jointY = ORIGIN_Y + (a.y - ORIGIN_Y) * growth;

      // Modul: A BRANCH LEAVES THE TRUNK UPWARD AND FLATTENS OUT. It does not
      // bulge sideways.
      //
      // This was one quadratic with a bow set at a fixed FRACTION of the run,
      // perpendicular to it - so every connector was the identical arc at a
      // different scale, and the two long horizontal ones, having the longest
      // run, bowed hardest. They came out as swoops that belonged to no branch
      // in the painting.
      //
      // A cubic with two tangents instead. The first control point pushes
      // straight UP out of the trunk, which is how a limb actually leaves it;
      // the second pulls back along that spread's own outward direction, so
      // the curve ARRIVES running the way the painted branch runs. The shape
      // then falls out of each limb's own geometry rather than being imposed:
      // the centre limb, whose spread is straight up, comes out very nearly
      // straight, while the far left and right rise and then level off.
      //
      // Both offsets are CAPPED rather than proportional. That is the whole
      // fix for "the longer ones are too curly" - past the cap a longer run
      // adds length, not bend.
      const runX = jointX - ORIGIN_X;
      const runY = jointY - ORIGIN_Y;
      const reachLen = Math.hypot(runX, runY);
      const rise = Math.min(reachLen * 0.5, 58);
      const settle = Math.min(reachLen * 0.42, 52);

      const c1x = ORIGIN_X + runX * 0.16;
      const c1y = ORIGIN_Y - rise;
      const c2x = jointX - a.outX * settle;
      const c2y = jointY - a.outY * settle;

      const twigs = limb.boughs.map((bough, side) => {
        const spread = side === 0 ? -0.62 : 0.62;
        const cos = Math.cos(spread);
        const sin = Math.sin(spread);
        const dx = a.outX * cos - a.outY * sin;
        const dy = a.outX * sin + a.outY * cos;
        const reach = 24 + 12 * (bough.level / bough.max);
        return { bough, tipX: jointX + dx * reach, tipY: jointY + dy * reach };
      });

      const taken = twigs.find((t) => t.bough.level > 0) ?? twigs[0];

      return {
        id: limb.root.id,
        name: limb.root.name,
        level: limb.root.level,
        limbPath: `M ${ORIGIN_X} ${ORIGIN_Y} C ${c1x} ${c1y} ${c2x} ${c2y} ${jointX} ${jointY}`,
        width: 1.4 + 2.2 * (limb.root.level / SKILL_TREE_ROOT_MAX),
        forkX: jointX,
        forkY: jointY,
        twigs,
        crown: limb.crown,
        crownX: taken.tipX + a.outX * 13,
        crownY: taken.tipY + a.outY * 13,
        // Along the spread and clear of the buds, so a label never lands on a
        // node it does not name.
        labelDx: a.outX * 30,
        labelDy: a.outY * 26 + 4,
        labelAnchor: a.anchor,
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

  <div class="treewrap">
  <svg
    class="tree"
    viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
    role="img"
    aria-label="Your skill tree: five limbs, each forking into two branches with a crown above"
  >
    <defs>
      <!-- One soft bloom, reused by every lit element. Cheap: a single blur
           merged under the source, not a filter per node. -->
      <filter id="skillglow" x="-60%" y="-60%" width="220%" height="220%">
        <feGaussianBlur stdDeviation="2.2" result="b" />
        <feMerge>
          <feMergeNode in="b" />
          <feMergeNode in="b" />
          <feMergeNode in="SourceGraphic" />
        </feMerge>
      </filter>
    </defs>

    <image
      href={backgroundUrl('yggdrasil')}
      x="0"
      y="0"
      width={VIEW_W}
      height={VIEW_H}
      preserveAspectRatio="xMidYMid meet"
      class="art"
    />

    {#each drawn as limb (limb.id)}
      <!-- Modul: EVERY CONNECTOR NEEDS A DARK CASING UNDER IT.
           The centre limb runs straight up the PAINTED TRUNK - the one part of
           this illustration that is bright, warm and busy, which is also the
           colour of the glow. It vanished completely: the node at the top of
           it lit, and nothing appeared to connect it to anything.

           A pale line is only visible over what is darker than it, and this
           background is a painting, so no single stroke colour can be safe
           everywhere. Casing first, bright stroke on top - the same answer the
           labels and the unlit node halos already use. -->
      <path d={limb.limbPath} class="casing" style={`stroke-width: ${limb.width + 3}`} />
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
          class="casing"
          class:dead={twig.bough.lockedOut}
          style="stroke-width: 4"
        />
        <path
          d={`M ${limb.forkX} ${limb.forkY} L ${twig.tipX} ${twig.tipY}`}
          class="twig"
          class:lit={twig.bough.level > 0}
          class:dead={twig.bough.lockedOut}
        />
        <!-- Modul: A BUD IS A JOINT, not a dot. Halo, ring, core - three
             circles, because a single filled circle at this size reads as a
             speck of dust on the painting rather than as something you can
             spend a point on. -->
        <g
          class="node bud"
          class:lit={twig.bough.level > 0}
          class:dead={twig.bough.lockedOut}
          transform={`translate(${twig.tipX} ${twig.tipY})`}
        >
          <circle class="halo" r={twig.bough.level > 0 ? 6.5 : 4.5} />
          <circle class="ring" r={twig.bough.level > 0 ? 3.6 : 2.8} />
          <circle class="core" r={twig.bough.level > 0 ? 1.7 : 1.1} />
        </g>
      {/each}

      <!-- The limb's own joint, where the fork happens. Larger than a bud:
           it is the thing the two buds hang off. -->
      <g
        class="node joint"
        class:lit={limb.level > 0}
        class:hot={hovered === limb.id}
        transform={`translate(${limb.forkX} ${limb.forkY})`}
      >
        <circle class="halo" r={limb.level > 0 ? 8.5 : 6} />
        <circle class="ring" r={limb.level > 0 ? 4.8 : 3.8} />
        <circle class="core" r={limb.level > 0 ? 2.2 : 1.5} />
      </g>

      {#if limb.crown.level > 0}
        <g class="node crown lit" transform={`translate(${limb.crownX} ${limb.crownY})`}>
          <circle class="halo" r="7.5" />
          <circle class="ring" r="4.2" />
          <circle class="core" r="2" />
        </g>
      {/if}

      <text
        x={limb.forkX + limb.labelDx}
        y={limb.forkY + limb.labelDy}
        class="limb-label"
        class:lit={limb.level > 0}
        text-anchor={limb.labelAnchor}
      >
        {limb.name}{#if limb.level > 0}&#160;{limb.level}{/if}
      </text>
    {/each}
  </svg>
  </div>

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

  /* A CONNECTOR, NOT A BRANCH. The painting supplies the branches; these are
     the lines of force between the nodes, so they are thin, bright and lit
     from within rather than bark-coloured and thick. */
  .twig {
    fill: none;
    stroke: rgba(226, 232, 240, 0.5);
    stroke-width: 1.3;
    stroke-linecap: round;
  }

  .twig.lit {
    stroke: var(--glow-warm);
    stroke-width: 1.8;
    filter: url(#skillglow);
  }

  /* Foreclosed, not absent: still drawn so the fork stays legible. */
  .twig.dead {
    stroke: rgba(226, 232, 240, 0.2);
    opacity: 0.45;
    stroke-dasharray: 2.5 3.5;
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

  /* Modul: THE ART IS A BACKDROP, THE TREE IS STILL THE SVG.
     The obvious reading of "replace the tree with this picture" would delete
     the diagram - but the limbs, twigs and buds are not decoration: they light
     as points go in, dim when a fork locks the other out, and carry the
     labels. A painting cannot do any of that. So Yggdrasil goes BEHIND, and
     the working tree draws on top of it.

     Held back hard (low opacity, blurred a touch) because the drawn limbs have
     to stay the thing the eye follows. */
  /* Modul: this sat at 30rem and then 34rem, which on a desktop panel left the
     illustration a stamp in the middle of a very wide empty box. It is the
     centrepiece of the screen and the thing the player aims at, so it gets the
     room. Still capped rather than full-bleed: past this the labels drift so
     far from the trunk that the fan stops reading as one tree. */
  .treewrap {
    position: relative;
    width: 100%;
    max-width: 52rem;
    margin: 0.2rem auto 0.6rem;
  }

  .tree {
    display: block;
    position: relative;
    width: 100%;
    overflow: visible;
  }

  /* Modul: the art carries this panel now, so it is shown nearly as painted -
     it used to sit at 0.4 opacity behind a blur, which made an expensive
     illustration look like a smudge. Held back only enough that white text
     and lit nodes still win. */
  .art {
    opacity: 0.92;
  }

  /* Drawn under every connector so a pale line has something to be pale
     against, whatever the painting is doing underneath it. */
  .casing {
    fill: none;
    stroke: rgba(6, 10, 14, 0.72);
    stroke-linecap: round;
  }

  .casing.dead {
    opacity: 0.45;
  }

  .limb {
    fill: none;
    stroke: rgba(226, 232, 240, 0.5);
    stroke-linecap: round;
    transition: stroke 140ms ease;
  }

  .limb.lit {
    stroke: var(--glow-warm);
    filter: url(#skillglow);
  }

  .limb.hot {
    stroke: var(--glow-hot);
    filter: url(#skillglow);
  }

  /* --- joints ---------------------------------------------------------------
     Three concentric circles per node: a halo that bleeds light onto the
     foliage, a ring that gives it an edge against a busy background, and a
     core. Unlit ones are cool and quiet; lit ones burn. */
  /* Modul: an UNLIT node needs contrast, not light. Over painted foliage a
     faint white circle vanishes into whatever leaf it landed on, so the halo
     of an unspent node is DARK - it clears a patch of background for the ring
     to sit against. Spending a point flips that same circle to a glow. */
  .node .halo {
    fill: rgba(8, 12, 16, 0.6);
    stroke: none;
  }

  .node .ring {
    fill: rgba(12, 16, 20, 0.7);
    stroke: rgba(226, 232, 240, 0.72);
    stroke-width: 1.1;
  }

  .node .core {
    fill: rgba(226, 232, 240, 0.8);
    stroke: none;
  }

  .node.lit .halo {
    fill: var(--glow-soft);
    filter: url(#skillglow);
  }

  .node.lit .ring {
    fill: rgba(24, 16, 4, 0.6);
    stroke: var(--glow-warm);
    stroke-width: 1.4;
  }

  .node.lit .core {
    fill: #fff6d8;
    filter: url(#skillglow);
  }

  .node.hot .ring {
    stroke: var(--glow-hot);
  }

  .node.dead {
    opacity: 0.35;
  }

  /* The crown is the top of a chosen fork - the one node that should look
     like an achievement rather than a purchase. */
  .node.crown .ring {
    stroke: #bfe9ff;
    stroke-width: 1.6;
  }

  .node.crown .core {
    fill: #eaf8ff;
  }

  .node.crown .halo {
    fill: rgba(120, 200, 255, 0.3);
  }

  .limb-label {
    fill: var(--text);
    font-size: 11px;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    /* The backdrop behind these is painted foliage, not a flat panel, so a
       dim grey label landed on whatever colour that branch happened to be.
       Brightened and given a dark halo so it reads over leaf, bark or gap. */
    paint-order: stroke;
    stroke: rgba(0, 0, 0, 0.85);
    stroke-width: 3px;
    stroke-linejoin: round;
  }

  .limb-label.lit {
    fill: #ffeec2;
  }

  @media (prefers-reduced-motion: reduce) {
    .limb,
    .node .ring,
    .node .core {
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
