// Modul: THE BREEDING RULES, RESTATED SO THE PREVIEW CAN EXPLAIN ITSELF.
//
// The preview already quoted a band and a price. What it never said was WHY -
// which parent a number came from, how likely that was, what the drift roll is,
// what a lucky roll can add. Breeding is the densest system in the game and it
// carried the least explanation; a band with no reasoning behind it is a number
// to accept, not a decision to make.
//
// Everything here mirrors a server constant and names it. Nothing is invented
// and nothing new is asked of the wire: the preview endpoints already return
// both parents' values, so every probability below is arithmetic the client can
// do on numbers it already has. Same contract as aptitudeBonusPercent in
// net/commands.ts, which mirrors BreedingAptitudes.BonusPercentFor for exactly
// the same reason.
//
// The canonical vocabulary is docs/breeding_model.md section 0. In short:
// APTITUDE for the four bred numbers, BLOODLINE for the set of them, GENE (not
// "locus") for the four dominant/recessive pairs, COPY (not "allele") for one
// half of a gene, NEWCOMER before they marry and ELDER after.

/** BreedingAptitudes.MutationUpPercent. */
export const MUTATION_UP_PERCENT = 25;

/** BreedingAptitudes.MutationDownPercent. */
export const MUTATION_DOWN_PERCENT = 10;

/** BreedingAptitudes.EpicChancePercent. */
export const EPIC_CHANCE_PERCENT = 5;

/** BreedingAptitudes.EpicChancePercentInbred - all but gone. */
export const EPIC_CHANCE_PERCENT_INBRED = 1;

/** BreedingAptitudes.EpicBonus, added to every one of the four. */
export const EPIC_BONUS = 1;

/** BreedingEngine.BreedingCooldownSeconds. ONE HOUR, not the day the spec
 *  describes - see the disagreements list in docs/breeding_model.md. */
export const BREEDING_COOLDOWN_SECONDS = 3600;

/** BreedingEngine.BaseBreedingCostGold. */
export const BASE_BREEDING_COST_GOLD = 500;

/** BreedingEngine.CostFor - linear in the older parent's generation. The
 *  preview endpoint quotes the real price and this never overrides it; it is
 *  here so the screen can show the ARITHMETIC behind a number the server
 *  already sent. */
export function breedingCostFor(maxGenerationIndex: number): number {
  return BASE_BREEDING_COST_GOLD * (Math.max(0, maxGenerationIndex) + 1);
}

/**
 * BreedingAptitudes.InheritOne, as a probability rather than a draw.
 *
 *     P(from A) = A / (A + B)
 *
 * This is the single most useful sentence the preview can say: a parent at 12
 * against a parent at 4 passes their 12 three times in four. It is also what
 * makes crossing two DIFFERENT specialists the strategy, because each aptitude
 * independently favours whichever parent is better at it.
 *
 * Two zeroes carry no information to weight by, and the server returns the
 * value unchanged in that case; a coin flip is the honest description.
 */
export function inheritChancePercent(a: number, b: number): number {
  const total = Math.max(0, a) + Math.max(0, b);
  if (total <= 0) return 50;
  return Math.round((Math.max(0, a) / total) * 100);
}

/** The drift roll applied after inheritance. INVERTED for a related pair,
 *  which is how the game degrades inbreeding instead of forbidding it. */
export function driftOdds(isInbred: boolean): { up: number; down: number; same: number } {
  const up = isInbred ? MUTATION_DOWN_PERCENT : MUTATION_UP_PERCENT;
  const down = isInbred ? MUTATION_UP_PERCENT : MUTATION_DOWN_PERCENT;
  return { up, down, same: 100 - up - down };
}

export function epicChancePercent(isInbred: boolean): number {
  return isInbred ? EPIC_CHANCE_PERCENT_INBRED : EPIC_CHANCE_PERCENT;
}

/**
 * What each gene actually does, keyed by the LocusName the preview endpoints
 * send ("Race", "Speed", "Crit", "Yield").
 *
 * Genes were the half of the preview with numbers and no meaning: four rows of
 * dominant values against a mutation percentage, describing bonuses the screen
 * never named. Consumed by StatsCalculator (Speed, Crit) and by both the live
 * and offline gathering paths (Yield, +4% a point).
 */
export const GENE_BLURBS: Record<string, string> = {
  Race: 'Which folk the child is. A pair of different races cannot breed at all.',
  Speed: 'Attack speed.',
  Crit: 'Crit chance.',
  Yield: '+4% gathering yield per point, online and away.',
};

export function geneBlurb(geneName: string): string {
  return GENE_BLURBS[geneName] ?? '';
}
