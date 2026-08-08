<script lang="ts">
  // Modul: THE SKILL TREE. Five passive branches, bought with the points an
  // account earns one per level.
  //
  // This panel used to drive four ACTIVE skills - buttons with cooldowns and a
  // mana bar. They were removed after being measured: mana refilled faster than
  // the cooldowns cleared, so at a 1.5 second swing nearly every hit could be
  // buffed, which came to +90% damage and +136% with the status synergy. All of
  // it available only to a player willing to click every three seconds, in a
  // game whose premise is that you do not have to.
  //
  // What a branch is worth is stated on its card, because a passive bonus the
  // player cannot see is indistinguishable from one that does not work - a
  // mistake this project has made more than once.
  import { playerState, pushLocalNotice } from '../stores/game';
  import {
    SKILL_TREE_BRANCHES,
    SKILL_TREE_MAX_LEVEL,
    skillTreeUpgradeCost,
    purchaseSkillTreeLevel,
  } from '../net/commands';
  import Bar from './Bar.svelte';

  const snap = $derived($playerState);
  const points = $derived(snap?.AvailableSkillPoints ?? 0);

  // The wire carries one byte per branch, indexed by the same ids the server's
  // SkillTreeRegistry uses, so the two cannot drift on ordering.
  function levelOf(branchId: number): number {
    if (!snap) return 0;
    switch (branchId) {
      case 0: return snap.SkillTree_LootRarity;
      case 1: return snap.SkillTree_WorldBossDamage;
      case 2: return snap.SkillTree_CritChance;
      case 3: return snap.SkillTree_CritDamage;
      case 4: return snap.SkillTree_XpGain;
      default: return 0;
    }
  }

  const rows = $derived(
    SKILL_TREE_BRANCHES.map((branch) => {
      const level = levelOf(branch.id);
      const cost = skillTreeUpgradeCost(level);
      const total = level * branch.perLevel;
      return {
        ...branch,
        level,
        cost,
        capped: level >= SKILL_TREE_MAX_LEVEL,
        affordable: cost > 0 && points >= cost,
        // Crit chance is percentage POINTS; the others are percentages of
        // their own quantity. Writing "+8%" for both would be wrong for one.
        label: branch.unit === 'points' ? `+${total.toFixed(1)} pts` : `+${total.toFixed(1)}%`,
      };
    }),
  );

  const spent = $derived(
    rows.reduce((sum, row) => {
      let total = 0;
      for (let i = 0; i < row.level; i++) total += skillTreeUpgradeCost(i);
      return sum + total;
    }, 0),
  );

  // Modul: THE TREE IS DRAWN, not listed.
  //
  // Five rows with a progress bar each is a settings page. This is meant to be
  // the one screen a player looks forward to opening, and a world-tree is the
  // shape the game's own setting already suggests - so the branches grow out of
  // a trunk, and they THICKEN and REACH FURTHER as they are invested in.
  //
  // Geometry rather than art: an inline SVG whose branch paths are computed
  // from the levels, so the picture is the data. No files, and it cannot fall
  // out of step with the numbers beside it.
  const VIEW_W = 460;
  const VIEW_H = 300;
  const ROOT_X = VIEW_W / 2;
  const ROOT_Y = VIEW_H - 12;

  /**
   * How tall the trunk is. Modul: branch starts were computed as a fraction of
   * the VIEW height, which is taller than the trunk - so the topmost branch
   * left the trunk above its own tip and its label was drawn at a negative y,
   * outside the box, on top of the paragraph above the picture. A branch grows
   * out of the trunk, so the trunk is what its position should be measured
   * against.
   */
  const TRUNK_H = 168;

  /** Where each branch leaves the trunk and which way it goes. */
  /** startY is how far UP the trunk the branch leaves it, 0 at the roots. */
  const BRANCH_LAYOUT = [
    { dx: -1.00, dy: -0.30, startY: 0.30 },
    { dx: -0.80, dy: -0.64, startY: 0.58 },
    { dx: 0.00, dy: -1.00, startY: 0.88 },
    { dx: 0.80, dy: -0.64, startY: 0.58 },
    { dx: 1.00, dy: -0.30, startY: 0.30 },
  ] as const;

  type Limb = {
    id: number;
    name: string;
    level: number;
    path: string;
    width: number;
    tipX: number;
    tipY: number;
    lit: boolean;
  };

  const limbs = $derived.by((): Limb[] =>
    rows.map((row, i) => {
      const layout = BRANCH_LAYOUT[i % BRANCH_LAYOUT.length];
      const startX = ROOT_X;
      const startY = ROOT_Y - TRUNK_H * layout.startY;

      // Modul: an untaken branch reaches MOST of the way already.
      //
      // The first version started at 28% of full reach, which put five labels
      // within thirty pixels of the trunk - GIANTSLAYER and CRUELTY printed on
      // top of each other. The shape of the tree has to be legible before any
      // point is spent, because reading it is how a player decides where the
      // first one goes. Investment thickens and extends a branch; it does not
      // conjure it.
      const growth = 0.74 + 0.26 * (row.level / SKILL_TREE_MAX_LEVEL);
      const reach = 128 * growth;

      const tipX = startX + layout.dx * reach;
      const tipY = startY + layout.dy * reach;

      // One control point, pulled outward, so a branch curves away from the
      // trunk rather than leaving it as a spoke.
      const cx = startX + layout.dx * reach * 0.55;
      const cy = startY + layout.dy * reach * 0.15;

      return {
        id: row.id,
        name: row.name,
        level: row.level,
        path: `M ${startX} ${startY} Q ${cx} ${cy} ${tipX} ${tipY}`,
        width: 2 + 5 * (row.level / SKILL_TREE_MAX_LEVEL),
        tipX,
        tipY,
        lit: row.level > 0,
      };
    }),
  );

  let hovered = $state<number | null>(null);

  function buy(branchId: number, level: number) {
    const outcome = purchaseSkillTreeLevel(branchId, level, points);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

<section class="panel">
  <header>
    <h3>Skill tree</h3>
    <span class="dim tiny">{points} point{points === 1 ? '' : 's'} unspent</span>
  </header>

  <p class="dim small">
    One point per level. Five branches, twenty levels each, and the price rises
    every fifth level - so a season buys two branches deep or five shallow.
    These reset when the season does.
  </p>

  {#if !snap}
    <p class="dim">Waiting for your state to arrive...</p>
  {:else}
    <!-- Modul: the tree, and the list beneath it. The picture is for deciding
         WHERE to put a point; the list is for reading exactly what one buys.
         Neither replaces the other, and the drawing is derived from the same
         rows the list renders, so they cannot disagree. -->
    <svg
      class="tree"
      viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
      role="img"
      aria-label="Your skill tree, drawn as branches that thicken as you invest"
    >
      <!-- The trunk. -->
      <path
        d={`M ${ROOT_X} ${ROOT_Y} C ${ROOT_X - 12} ${ROOT_Y - 70}, ${ROOT_X + 12} ${ROOT_Y - 120}, ${ROOT_X} ${ROOT_Y - 168}`}
        class="trunk"
      />
      <!-- Roots, purely so the trunk does not float. -->
      <path d={`M ${ROOT_X} ${ROOT_Y} l -26 10 M ${ROOT_X} ${ROOT_Y} l 26 10 M ${ROOT_X} ${ROOT_Y} l -8 12 M ${ROOT_X} ${ROOT_Y} l 9 12`} class="roots" />

      {#each limbs as limb (limb.id)}
        <path
          d={limb.path}
          class="limb"
          class:lit={limb.lit}
          class:hot={hovered === limb.id}
          style={`stroke-width: ${limb.width}`}
        />
        <circle
          cx={limb.tipX}
          cy={limb.tipY}
          r={limb.lit ? 4 + limb.level / 6 : 3}
          class="bud"
          class:lit={limb.lit}
          class:hot={hovered === limb.id}
        />
        <text x={limb.tipX} y={limb.tipY - 10} class="limb-label" text-anchor="middle">
          {limb.name}{#if limb.level > 0} {limb.level}{/if}
        </text>
      {/each}
    </svg>

    <ul class="branches">
      {#each rows as row (row.id)}
        <li
          class:capped={row.capped}
          onmouseenter={() => (hovered = row.id)}
          onmouseleave={() => (hovered = null)}
        >
          <div class="head">
            <span class="name">{row.name}</span>
            <span class="value">
              {#if row.level > 0}{row.label}{:else}<span class="dim">not taken</span>{/if}
            </span>
          </div>

          <p class="blurb dim tiny">{row.blurb}</p>

          <Bar
            value={row.level}
            max={SKILL_TREE_MAX_LEVEL}
            color="var(--accent)"
            label={`${row.level} / ${SKILL_TREE_MAX_LEVEL}`}
          />

          <div class="buy">
            {#if row.capped}
              <span class="dim tiny">At maximum.</span>
            {:else}
              <button
                disabled={!row.affordable}
                title={row.affordable ? '' : `Needs ${row.cost} point${row.cost === 1 ? '' : 's'}`}
                onclick={() => buy(row.id, row.level)}
              >
                +{row.perLevel}{row.unit === 'points' ? ' pts' : '%'} for {row.cost}
                point{row.cost === 1 ? '' : 's'}
              </button>
            {/if}
          </div>
        </li>
      {/each}
    </ul>

    {#if spent > 0}
      <p class="dim tiny footer">{spent} points spent this season.</p>
    {/if}
  {/if}
</section>

<style>
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

  .bud.hot {
    fill: var(--accent);
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
  header h3 { margin: 0; }
  header .dim { margin-left: auto; }

  .small { font-size: 0.9rem; max-width: 46rem; }
  .tiny  { font-size: 0.8rem; }
  .dim   { opacity: 0.75; }

  .branches {
    list-style: none;
    margin: 1rem 0 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 0.9rem;
  }

  .branches li {
    display: grid;
    gap: 0.45rem;
    padding: 0.85rem 0.95rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: rgba(127, 127, 127, 0.04);
  }

  /* A maxed branch stays fully legible - it is an achievement, not a disabled
     control, and dimming it would read as "broken". */
  .branches li.capped { border-color: var(--accent); }

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
  }
  .name { font-weight: 650; }
  .value {
    margin-left: auto;
    font-variant-numeric: tabular-nums;
    font-weight: 650;
  }

  .blurb { margin: 0; }

  .buy { margin-top: 0.15rem; }
  .buy button {
    width: 100%;
    font-size: 0.85rem;
    padding: 0.4rem 0.6rem;
  }

  .footer { margin: 1rem 0 0; }
</style>
