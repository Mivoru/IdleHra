import { describe, it, expect } from 'vitest';
import {
  SKILL_TREE_NODES,
  SKILL_TREE_ROOT_MAX,
  SKILL_TREE_BOUGH_MAX,
  SKILL_TREE_CROWN_COST,
  skillRingOf,
  skillNodeMaxLevel,
  skillTreeUpgradeCost,
  skillNodeBlockedReason,
  isSkillEffectPending,
  SKILL_TREE_EFFECT_PENDING,
  siblingBoughOf,
  boughsOfRoot,
  crownOfRoot,
} from '../src/lib/net/commands';

/** A levels array with everything at zero, overridden by node id. */
function levels(over: Record<number, number> = {}): number[] {
  const l = new Array(20).fill(0);
  for (const [id, v] of Object.entries(over)) l[Number(id)] = v;
  return l;
}

describe('the shape of the tree', () => {
  it('has five roots, ten boughs and five crowns', () => {
    const byRing = { root: 0, bough: 0, crown: 0 };
    for (const n of SKILL_TREE_NODES) byRing[n.ring]++;
    expect(byRing).toEqual({ root: 5, bough: 10, crown: 5 });
  });

  it('places every node at the id its ring implies', () => {
    for (const n of SKILL_TREE_NODES) {
      expect(skillRingOf(n.id)).toBe(n.ring);
    }
  });

  it('pairs each root with exactly two boughs and one crown', () => {
    for (let root = 0; root < 5; root++) {
      const [a, b] = boughsOfRoot(root);
      expect(SKILL_TREE_NODES[a].root).toBe(root);
      expect(SKILL_TREE_NODES[b].root).toBe(root);
      expect(SKILL_TREE_NODES[crownOfRoot(root)].root).toBe(root);
    }
  });

  it('makes sibling lookup symmetric', () => {
    for (let id = 5; id < 15; id++) {
      expect(siblingBoughOf(siblingBoughOf(id))).toBe(id);
      expect(siblingBoughOf(id)).not.toBe(id);
    }
    expect(siblingBoughOf(0)).toBe(-1);
    expect(siblingBoughOf(15)).toBe(-1);
  });
});

describe('what a level costs', () => {
  it('charges the rising curve for roots and a flat price above', () => {
    expect(skillTreeUpgradeCost(0, 0)).toBe(1);
    expect(skillTreeUpgradeCost(0, 4)).toBe(1);
    expect(skillTreeUpgradeCost(0, 5)).toBe(2);
    expect(skillTreeUpgradeCost(0, 9)).toBe(2);
    expect(skillTreeUpgradeCost(5, 0)).toBe(2);
    expect(skillTreeUpgradeCost(5, 7)).toBe(2);
    expect(skillTreeUpgradeCost(15, 0)).toBe(SKILL_TREE_CROWN_COST);
  });

  it('charges nothing at the cap, so a capped node cannot be bought', () => {
    expect(skillTreeUpgradeCost(0, SKILL_TREE_ROOT_MAX)).toBe(0);
    expect(skillTreeUpgradeCost(5, SKILL_TREE_BOUGH_MAX)).toBe(0);
    expect(skillTreeUpgradeCost(15, 1)).toBe(0);
  });

  it('costs 43 points to take one limb all the way', () => {
    // The budget the whole design rests on: 43 a limb (root 15 + bough 16 +
    // crown 12), 215 for all five, against roughly 100 points a season. Two
    // limbs and a bit is the choice.
    let total = 0;
    for (let l = 0; l < SKILL_TREE_ROOT_MAX; l++) total += skillTreeUpgradeCost(0, l);
    for (let l = 0; l < SKILL_TREE_BOUGH_MAX; l++) total += skillTreeUpgradeCost(5, l);
    total += SKILL_TREE_CROWN_COST;
    expect(total).toBe(43);
  });
});

describe('the doors that close', () => {
  it('will not sell a bough until its root is at 5', () => {
    expect(skillNodeBlockedReason(6, levels({ 0: 4 }), 99)).toMatch(/Fortune at 5/);
    expect(skillNodeBlockedReason(6, levels({ 0: 5 }), 99)).toBeNull();
  });

  it('locks the other side of a fork once one is taken', () => {
    // Cruelty, whose two boughs are both wired - so the refusal under test is
    // the fork rule rather than the not-yet-implemented guard.
    const taken = levels({ 3: 5, 11: 1 });
    expect(skillNodeBlockedReason(11, taken, 99)).toBeNull();
    expect(skillNodeBlockedReason(12, taken, 99)).toMatch(/Bloodthirst was taken instead/);
  });

  it('will not sell a crown until a bough of that limb reaches 5', () => {
    // Scholar over Insight - a crown whose effect is wired.
    expect(skillNodeBlockedReason(19, levels({ 4: 5, 14: 4 }), 99)).toMatch(/branch of Insight at 5/);
    expect(skillNodeBlockedReason(19, levels({ 4: 5, 14: 5 }), 99)).toBeNull();
  });

  it('accepts either side of the fork as the crown prerequisite', () => {
    expect(skillNodeBlockedReason(19, levels({ 4: 5, 13: 5 }), 99)).toBeNull();
  });

  it('reports the price when the points are short, and says both numbers', () => {
    const reason = skillNodeBlockedReason(19, levels({ 4: 5, 14: 5 }), 3);
    expect(reason).toBe('Costs 12 points; you have 3.');
  });

  it('refuses any node whose effect is not wired up yet', () => {
    // THE DEFECT THIS PREVENTS: spending a real resource on a bonus that
    // silently does nothing. This project has shipped that more than once.
    for (const id of SKILL_TREE_EFFECT_PENDING) {
      expect(isSkillEffectPending(id)).toBe(true);
      // Even with the prerequisites met and points to spare.
      const ready = levels({ 0: 10, 1: 10, 2: 10, 3: 10, 4: 10, 9: 5, 11: 5, 14: 5 });
      expect(skillNodeBlockedReason(id, ready, 999)).toBe('Not in the game yet - coming soon.');
    }
  });

  it('refuses a node already at its limit', () => {
    expect(skillNodeBlockedReason(0, levels({ 0: SKILL_TREE_ROOT_MAX }), 99)).toBe(
      'Already at its limit.',
    );
  });

  it('never blocks a root for a reason other than points or the cap', () => {
    // Roots are the free layer on purpose - nothing gates them.
    for (let root = 0; root < 5; root++) {
      expect(skillNodeBlockedReason(root, levels(), 99)).toBeNull();
    }
  });

  it('cannot be talked into all five crowns on one season budget', () => {
    // 215 points for five full limbs against ~100 a season. If this ever
    // drops near a season budget, the tree has stopped being a choice.
    let allFive = 0;
    for (let root = 0; root < 5; root++) {
      for (let l = 0; l < SKILL_TREE_ROOT_MAX; l++) allFive += skillTreeUpgradeCost(root, l);
      const [a] = boughsOfRoot(root);
      for (let l = 0; l < SKILL_TREE_BOUGH_MAX; l++) allFive += skillTreeUpgradeCost(a, l);
      allFive += SKILL_TREE_CROWN_COST;
    }
    expect(allFive).toBe(215);
    expect(allFive).toBeGreaterThan(100);
  });
});

describe('every node is describable', () => {
  it('names, blurbs and caps all twenty', () => {
    expect(SKILL_TREE_NODES.length).toBe(20);
    for (const n of SKILL_TREE_NODES) {
      expect(n.name.length).toBeGreaterThan(0);
      expect(n.blurb.length).toBeGreaterThan(20);
      expect(skillNodeMaxLevel(n.id)).toBeGreaterThan(0);
    }
    expect(new Set(SKILL_TREE_NODES.map((n) => n.name)).size).toBe(20);
  });
});
