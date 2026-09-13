// Modul: WHO CAN BE PICKED, AND WHY NOT - the pure half of the breeding picker.
//
// The Breeding screen used two native <select>s whose option text carried a
// per-second countdown ("resting 3421s") and whose `disabled` flags were
// recomputed off the same ticking clock. Android's WebView draws a <select> as
// a native dialog, and rewriting the options underneath an open dialog closes
// it or drops the choice - reported from the APK as "the pickers glitch, and
// sometimes do not work". Right after a pairing both parents rest for an hour,
// so it happened exactly when a player went to choose again.
//
// So the picker is plain DOM now (PersonPicker.svelte), and everything it shows
// is decided here: no DOM, no clock of its own, testable in a millisecond.
// Time is passed in and rounded to MINUTES, so a label changes at most once a
// minute rather than every tick.
//
// These reasons mirror BreedingGateRules on the server. The server still
// refuses on its own and answers with a command result; a reason shown BEFORE
// the tap is worth more than one after it.

import type { BreedingCandidate, VillageNewcomer } from '../net/rest';
import { raceName } from './races';
import { agePhaseName } from './slots';

export interface PickerPerson {
  /** 'c:<guid>' for one of the player's characters, 'v:<id>' for a newcomer. */
  key: string;
  name: string;
  raceId: number;
  isFemale: boolean;
  /** Strength, Skill, Endurance, Fortune - BreedingAptitudes order. */
  aptitudes: readonly [number, number, number, number];
  /** One line under the name: race, sex, age, generation. */
  detail: string;
  /** Short badges: epic, inbred. */
  marks: readonly string[];
  /** Why this person cannot be chosen right now, or null. */
  blocked: string | null;
}

const sexWord = (isFemale: boolean) => (isFemale ? 'woman' : 'man');

/** Whole minutes left on a breeding rest, rounded UP; 0 when not resting. */
export function restingMinutesLeft(cooldownEndEpoch: number, nowSeconds: number): number {
  const left = cooldownEndEpoch - nowSeconds;
  return left > 0 ? Math.ceil(left / 60) : 0;
}

function restingReason(cooldownEndEpoch: number, nowSeconds: number): string | null {
  const minutes = restingMinutesLeft(cooldownEndEpoch, nowSeconds);
  return minutes > 0 ? `resting, ${minutes} min left` : null;
}

/** Why a character cannot be the hero of a pairing, or null. */
export function heroBlockedReason(candidate: BreedingCandidate, nowSeconds: number): string | null {
  if (candidate.AgePhase < 1) return 'still a child - field it in the Hall to grow up';
  return restingReason(candidate.BreedingCooldownEndEpoch, nowSeconds);
}

/** Why a newcomer cannot marry the chosen hero, or null. Mirrors CheckVillagerPair. */
export function villagerBlockedReason(
  hero: BreedingCandidate | undefined,
  person: VillageNewcomer,
): string | null {
  // "Elder" on the server means SPENT, and collided with the Elder age phase
  // on this screen - so the word is never shown.
  if (person.IsElder) return 'has already married in';
  if (!hero) return null;
  if (hero.IsFemale === person.IsFemale) return `both ${person.IsFemale ? 'women' : 'men'}`;
  if (hero.LocusRaceDominant !== person.RaceId) return `not ${raceName(hero.LocusRaceDominant)}`;
  return null;
}

/** The same question asked of one of the player's own characters. Mirrors CheckPair. */
export function characterPartnerBlockedReason(
  hero: BreedingCandidate | undefined,
  candidate: BreedingCandidate,
  nowSeconds: number,
): string | null {
  if (hero && candidate.CharacterId === hero.CharacterId) return 'that is your hero';
  if (candidate.AgePhase < 1) return 'still a child';
  const resting = restingReason(candidate.BreedingCooldownEndEpoch, nowSeconds);
  if (resting) return resting;
  if (!hero) return null;
  if (hero.IsFemale === candidate.IsFemale) return `both ${candidate.IsFemale ? 'women' : 'men'}`;
  if (hero.LocusRaceDominant !== candidate.LocusRaceDominant) return `not ${raceName(hero.LocusRaceDominant)}`;
  return null;
}

function characterMarks(candidate: BreedingCandidate): string[] {
  const marks: string[] = [];
  if (candidate.IsEpicMutation) marks.push('epic');
  if (candidate.IsInbred) marks.push('inbred');
  return marks;
}

function characterBase(candidate: BreedingCandidate): Omit<PickerPerson, 'blocked'> {
  const race = raceName(candidate.LocusRaceDominant);
  return {
    key: 'c:' + candidate.CharacterId,
    name: candidate.Name || `${race} ${sexWord(candidate.IsFemale)}`,
    raceId: candidate.LocusRaceDominant,
    isFemale: candidate.IsFemale,
    aptitudes: [
      candidate.AptitudeStrength,
      candidate.AptitudeSkill,
      candidate.AptitudeEndurance,
      candidate.AptitudeFortune,
    ],
    detail: `${race} ${sexWord(candidate.IsFemale)} · ${agePhaseName(candidate.AgePhase)} · gen ${candidate.GenerationIndex}`,
    marks: characterMarks(candidate),
  };
}

export function heroPerson(candidate: BreedingCandidate, nowSeconds: number): PickerPerson {
  return { ...characterBase(candidate), blocked: heroBlockedReason(candidate, nowSeconds) };
}

export function partnerCharacterPerson(
  hero: BreedingCandidate | undefined,
  candidate: BreedingCandidate,
  nowSeconds: number,
): PickerPerson {
  return { ...characterBase(candidate), blocked: characterPartnerBlockedReason(hero, candidate, nowSeconds) };
}

export function villagerPerson(hero: BreedingCandidate | undefined, person: VillageNewcomer): PickerPerson {
  const race = raceName(person.RaceId);
  return {
    key: 'v:' + person.Id,
    name: person.Name || `${race} ${sexWord(person.IsFemale)}`,
    raceId: person.RaceId,
    isFemale: person.IsFemale,
    aptitudes: [person.AptitudeStrength, person.AptitudeSkill, person.AptitudeEndurance, person.AptitudeFortune],
    detail: `${race} ${sexWord(person.IsFemale)} · newcomer`,
    marks: [],
    blocked: villagerBlockedReason(hero, person),
  };
}

export const aptitudeTotal = (person: PickerPerson) =>
  person.aptitudes[0] + person.aptitudes[1] + person.aptitudes[2] + person.aptitudes[3];

/**
 * Choosable first, then the strongest blood, then by name - so the answer to
 * "who should I pick" is usually the top card, and a refused card never pushes
 * a choosable one below the fold of a phone.
 */
export function sortForPicker(people: readonly PickerPerson[]): PickerPerson[] {
  return [...people].sort((a, b) => {
    const blockedOrder = Number(a.blocked !== null) - Number(b.blocked !== null);
    if (blockedOrder !== 0) return blockedOrder;
    const strength = aptitudeTotal(b) - aptitudeTotal(a);
    if (strength !== 0) return strength;
    return a.name.localeCompare(b.name);
  });
}

/**
 * The Breeding Grounds lets a player choose up to N aptitudes. A new choice past
 * the limit drops the OLDEST choice - the previous version said so in a comment
 * and dropped the lowest index instead, so ticking Fortune then Strength then
 * Skill at a limit of two discarded Strength, the one chosen second.
 */
export function toggleSelection(order: readonly number[], index: number, limit: number): number[] {
  if (order.includes(index)) return order.filter((i) => i !== index);
  if (limit <= 0) return [...order];
  const next = [...order, index];
  while (next.length > limit) next.shift();
  return next;
}

export function selectionMaskOf(order: readonly number[]): number {
  return order.reduce((mask, index) => mask | (1 << index), 0);
}
