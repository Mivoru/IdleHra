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
  import DisabledReason from './DisabledReason.svelte';
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
          ? // Task 109: "+0.4 pts" sat next to "+3.0%" and read as a third
            // currency. 'points' means PERCENTAGE POINTS added to a chance -
            // Precision, the only node in that unit, adds them to crit chance.
            `+${total.toFixed(1)}% crit chance`
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
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
    confirmingRespec = false;
  }

  /** Task 109: a cost button says what it does - and a full node says so,
   * rather than showing a bare dash on a dead button. */
  function costLabel(row: NodeRow): string {
    if (row.level >= row.max) return 'Maxed';
    if (row.lockedOut) return 'Locked';
    return `Learn · ${row.cost} pt`;
  }

  function buy(nodeId: number) {
    const outcome = purchaseSkillTreeLevel(nodeId, levels, points);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
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

  // Modul: the canopy reaches the top of the illustration, so the centre
  // limb's label and crown had nowhere to go that was not on top of its own
  // buds. The viewBox is extended UPWARD by this much and the image still
  // starts at y=0 - the art is not scaled or cropped, it simply gains sky
  // above it. Without this the only way to place that one label was to treat
  // it differently from the other four, which is the thing being fixed.
  const PAD_TOP = 34;

  // Modul: THE SAME PROBLEM SIDEWAYS, and it shipped because the checker that
  // would have caught it excludes SVG.
  //
  // The outer limbs place their labels at `forkX + outX * 52` with a matching
  // text-anchor, so the leftmost one reads LEFTWARD from a fork that is
  // already near x=0. The viewBox started at exactly 0, so "Fortune" ran off
  // the left edge and was clipped by the viewport itself - measured at
  // left=-6px on a 390px phone, with the word unreadable.
  //
  // Why nothing caught it: clipping-check.mjs skips SVG on purpose, because
  // an SVG element reports clientWidth in its own coordinate system and that
  // produced nonsense like "91px overflowing a 29px box". The exclusion was
  // right about clientWidth and too broad about SVG - a VIEWPORT-relative
  // measurement is perfectly valid on an SVG node. The checker is fixed
  // alongside this; see scripts/clipping-check.mjs.
  //
  // Same remedy as PAD_TOP: widen the viewBox rather than special-case one
  // label. The image still starts at x=0 and is not scaled or cropped - the
  // drawing simply gains margin either side. 34 matches PAD_TOP, and the
  // widest label ("PRECISION") needs about 30 beyond its anchor.
  const PAD_X = 34;

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
        // Modul: PAST THE BUDS, not among them.
        //
        // A bud reaches at most 36 from the joint and is 6.5 across, and the
        // crown sits 13 beyond the taken one - so anything closer than about
        // 50 lands on a node. The offset used to be 30, which was inside that
        // for every limb and merely LOOKED fine on four of them, because their
        // spreads run diagonally and the label drifted sideways off the buds.
        // The centre limb's spread is straight up, which is also exactly where
        // its two buds go, so PRECISION sat on its own fork.
        //
        // One rule, one number, all five: out * 52 clears the whole fork
        // whichever way it points. The headroom above the canopy exists for
        // this - see PAD_TOP.
        labelDx: a.outX * 52,
        labelDy: a.outY * 52 + 4,
        labelAnchor: a.anchor,
      };
    }),
  );
</script>

<section class="panel skills">
  <header class="head">
    <div>
      <h2>Skill tree</h2>
      <p class="dim small">
        Roots are cheap and you want some of each. Each root forks into two, and
        <strong>taking one locks the other</strong> for the season. A crown sits
        above whichever fork you chose.
      </p>
    </div>
    <span class="points">{points} <span class="dim tiny">to spend</span></span>
  </header>

  <div class="treewrap">
  <svg
    class="tree"
    viewBox={`${-PAD_X} ${-PAD_TOP} ${VIEW_W + PAD_X * 2} ${VIEW_H + PAD_TOP}`}
    role="img"
    aria-label="Your skill tree: five limbs, each forking into two branches with a crown above"
  >
    <defs>
      <!-- One soft bloom, reused by every lit element. Cheap: a single blur
           merged under the source, not a filter per node. -->
      <!-- Modul: userSpaceOnUse IS LOAD-BEARING, not a tidy-up.
           A filter region defaults to objectBoundingBox units, and the centre
           limb's connector is a PERFECTLY VERTICAL LINE - a bounding box of
           zero width. 220% of zero is zero, so the filter region collapsed and
           the lit stroke was never painted at all. What survived was its dark
           casing, which carries no filter: the middle branch rendered as a
           shadow while the other four glowed, and it looked like the fifth one
           had been built differently on purpose.

           Pinned to the viewBox in user space instead, where no element's own
           geometry can shrink it away. -->
      <filter
        id="skillglow"
        filterUnits="userSpaceOnUse"
        x="-40"
        y="-40"
        width={VIEW_W + 80}
        height={VIEW_H + 80}
      >
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

    {/each}
  </svg>
  <!-- Modul: TASK 109 - THE LIMB LABELS ARE HTML OVER THE PICTURE, NOT SVG
       TEXT IN IT. SVG text scales with the viewBox, so 11 user units came out
       at about 6px on a phone; any size big enough there ran FORTUNE off the
       left edge again (the lesson PAD_X records). HTML text keeps a CSS size
       of its own (11px floor, growing with the container), and each label is
       pinned by the edge it grows AWAY from with a max-width up to the
       picture's border - so a long label wraps instead of leaving the screen. -->
  {#each drawn as limb (limb.id)}
    {@const px = ((limb.forkX + limb.labelDx + PAD_X) / (VIEW_W + PAD_X * 2)) * 100}
    {@const py = ((limb.forkY + limb.labelDy + PAD_TOP) / (VIEW_H + PAD_TOP)) * 100}
    <span
      class="limb-label anchor-{limb.labelAnchor}"
      class:lit={limb.level > 0}
      style={limb.labelAnchor === 'end'
        ? `right: ${100 - px}%; top: ${py}%; max-width: ${px}%`
        : limb.labelAnchor === 'start'
          ? `left: ${px}%; top: ${py}%; max-width: ${100 - px}%`
          : `left: ${px}%; top: ${py}%`}
    >
      {limb.name}{#if limb.level > 0}{' '}{limb.level}{/if}
    </span>
  {/each}
  </div>

  {#if spent === 0 && points === 0}
    <!-- Task 109: nothing learned and nothing to spend - a live Respec here
         was the only thing on the screen that looked pressable, and it would
         have refunded nothing. ProgressionEngine grants one point per level;
         DeedRegistry.SkillPointsPerSeal adds two per sealed chapter. -->
    <p class="dim small earn" data-testid="skills-earn">
      You earn a skill point every level, and two more for each chapter you seal
      in the Book of Deeds.
    </p>
  {:else}
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
        <!-- {' '} on purpose: Svelte drops the space before an {#if}, which
             is how this read "Respec(free)". -->
        Respec{' '}{#if !freeUsed}(free){:else}({grants} left){/if}
      </button>
      <DisabledReason text={respecBlocked} />
    {/if}
  </div>
  {/if}

  {#if points > 0}
    <p class="to-spend" data-testid="skills-to-spend"><strong>{points}</strong> to spend - pick a root below.</p>
  {/if}

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
            <DisabledReason text={limb.root.level >= limb.root.max ? null : limb.root.blocked} />
          </div>
          <button
            disabled={limb.root.blocked !== null}
            title={limb.root.blocked ?? ''}
            onclick={() => buy(limb.root.id)}
          >
            {costLabel(limb.root)}
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
                <DisabledReason text={bough.lockedOut || bough.level >= bough.max ? null : bough.blocked} />
              </div>
              <button
                disabled={bough.blocked !== null}
                title={bough.blocked ?? ''}
                onclick={() => buy(bough.id)}
              >
                {costLabel(bough)}
              </button>
            </div>
          {/each}
        </div>

        <!-- The crown -->
        <div class="node crown" class:taken={limb.crown.level > 0}>
          <div class="node-text">
            <strong>
              <!-- Modul: the crown mark, drawn. It sits next to a name and was
                   a ♦ - a glyph whose weight and size vary by font family, on
                   a panel already carrying its own art direction. -->
              <svg class="crownmark" viewBox="0 0 12 12" aria-hidden="true">
                <path d="M6 1 L11 6 L6 11 L1 6 Z" fill="currentColor" />
              </svg>
              {limb.crown.name}
            </strong>
            <p class="dim tiny">{limb.crown.blurb}</p>
            <DisabledReason text={limb.crown.level > 0 ? null : limb.crown.blocked} />
          </div>
          <button
            disabled={limb.crown.blocked !== null}
            title={limb.crown.blocked ?? ''}
            onclick={() => buy(limb.crown.id)}
          >
            {limb.crown.level > 0 ? 'Taken' : `Take · ${limb.crown.cost} pt`}
          </button>
        </div>
      </div>
    {/each}
  </div>
</section>

<style>
  /* Task 109: on a desktop the cards ran the full width of the window, a
     blurb one long line and its button a screen away. */
  .skills {
    max-width: 60rem;
    margin-inline: auto;
  }

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
  @media (max-width: 40rem) {
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
    /* The limb labels size against this box (cqw), not the window. */
    container-type: inline-size;
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
    position: absolute;
    /* The label's baseline sat at the anchor point in the SVG; lifting the
       box by its own height puts it back there. */
    translate: 0 -85%;
    color: var(--text);
    font-size: clamp(0.6875rem, 2cqw, 1rem);
    font-weight: 600;
    line-height: 1.15;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    pointer-events: none;
    /* The backdrop behind these is painted foliage, not a flat panel, so a
       dim grey label landed on whatever colour that branch happened to be.
       Brightened and given a dark halo so it reads over leaf, bark or gap. */
    text-shadow:
      0 0 3px rgba(0, 0, 0, 0.95),
      0 0 2px rgba(0, 0, 0, 0.95),
      0 1px 1px rgba(0, 0, 0, 0.9);
  }

  .limb-label.anchor-end {
    text-align: right;
  }

  .limb-label.anchor-middle {
    translate: -50% -85%;
    white-space: nowrap;
    text-align: center;
  }

  .limb-label.lit {
    color: #ffeec2;
  }

  .earn {
    text-align: center;
    margin: 0 auto 0.6rem;
  }

  .to-spend {
    margin: 0 0 0.5rem;
    color: var(--brass-lit);
  }

  @media (prefers-reduced-motion: reduce) {
    .limb,
    .node .ring,
    .node .core {
      transition: none;
    }
  }

  .panel {
    background: var(--bg-panel);
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




  .crownmark {
    width: 0.7em;
    height: 0.7em;
    margin-right: 0.25em;
  }
</style>
