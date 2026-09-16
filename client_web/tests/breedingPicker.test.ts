import { describe, expect, it } from 'vitest';
import type { BreedingCandidate, VillageNewcomer } from '../src/lib/net/rest';
import {
  characterPartnerBlockedReason,
  heroBlockedReason,
  heroPerson,
  restingMinutesLeft,
  selectionMaskOf,
  sortForPicker,
  toggleSelection,
  villagerBlockedReason,
  villagerPerson,
} from '../src/lib/ui/breedingPicker';

function candidate(overrides: Partial<BreedingCandidate> = {}): BreedingCandidate {
  return {
    CharacterId: '11111111-1111-1111-1111-111111111111',
    Name: 'Cadoc',
    AgePhase: 1,
    GenerationIndex: 0,
    IsBreedingActive: false,
    BreedingCooldownEndEpoch: 0,
    IsEpicMutation: false,
    IsInbred: false,
    TraitMask: 0,
    IsFemale: false,
    AptitudeStrength: 4,
    AptitudeSkill: 4,
    AptitudeEndurance: 4,
    AptitudeFortune: 4,
    LocusRaceDominant: 1,
    LocusRaceRecessive: 1,
    ...overrides,
  };
}

function newcomer(overrides: Partial<VillageNewcomer> = {}): VillageNewcomer {
  return {
    Id: 7,
    Name: 'Branwen',
    RaceId: 1,
    IsFemale: true,
    AptitudeStrength: 6,
    AptitudeSkill: 6,
    AptitudeEndurance: 6,
    AptitudeFortune: 6,
    ArrivedAtEpoch: 0,
    IsElder: false,
    TraitMask: 0,
    ...overrides,
  };
}

describe('breeding picker', () => {
  // Modul: THE ANDROID DEFECT. The old label counted down in seconds, so the
  // option text under an open native dialog changed every tick. Minutes, and
  // rounded up, so the label is stable for sixty seconds at a time.
  it('counts a rest in whole minutes, rounded up', () => {
    expect(restingMinutesLeft(1000, 1000)).toBe(0);
    expect(restingMinutesLeft(1001, 1000)).toBe(1);
    expect(restingMinutesLeft(1000 + 3600, 1000)).toBe(60);
    expect(restingMinutesLeft(1000 + 61, 1000)).toBe(2);

    const now = 5000;
    const a = heroBlockedReason(candidate({ BreedingCooldownEndEpoch: now + 3000 }), now);
    const b = heroBlockedReason(candidate({ BreedingCooldownEndEpoch: now + 3000 }), now + 30);
    expect(a).toBe(b);
  });

  it('refuses a child and a resting hero, with the reason', () => {
    expect(heroBlockedReason(candidate({ AgePhase: 0 }), 0)).toMatch(/child/);
    expect(heroBlockedReason(candidate({ BreedingCooldownEndEpoch: 600 }), 0)).toMatch(/resting, 10 min/);
    expect(heroBlockedReason(candidate(), 0)).toBeNull();
  });

  it('mirrors the villager gate: spent, same sex, other race', () => {
    const hero = candidate();
    expect(villagerBlockedReason(hero, newcomer({ IsElder: true }))).toMatch(/already married in/);
    expect(villagerBlockedReason(hero, newcomer({ IsFemale: false }))).toBe('both men');
    expect(villagerBlockedReason(hero, newcomer({ RaceId: 2 }))).toBe('not Human');
    expect(villagerBlockedReason(hero, newcomer())).toBeNull();
    // Without a hero only the spent flag can be known.
    expect(villagerBlockedReason(undefined, newcomer({ IsFemale: false }))).toBeNull();
  });

  it('mirrors the roster gate, and never offers the hero as their own partner', () => {
    const hero = candidate();
    expect(characterPartnerBlockedReason(hero, hero, 0)).toMatch(/your hero/);
    expect(characterPartnerBlockedReason(hero, candidate({ CharacterId: 'x', IsFemale: false }), 0)).toBe('both men');
    expect(characterPartnerBlockedReason(hero, candidate({ CharacterId: 'x', IsFemale: true, LocusRaceDominant: 3 }), 0)).toBe('not Human');
    expect(characterPartnerBlockedReason(hero, candidate({ CharacterId: 'x', IsFemale: true }), 0)).toBeNull();
  });

  it('shows a name, and a race and sex when there is none', () => {
    expect(heroPerson(candidate(), 0).name).toBe('Cadoc');
    expect(heroPerson(candidate({ Name: '' }), 0).name).toBe('Human man');
    expect(villagerPerson(undefined, newcomer()).name).toBe('Branwen');
  });

  it('lists the choosable first, then the strongest blood', () => {
    const hero = candidate();
    const sorted = sortForPicker([
      villagerPerson(hero, newcomer({ Id: 1, Name: 'Spent', IsElder: true, AptitudeStrength: 20 })),
      villagerPerson(hero, newcomer({ Id: 2, Name: 'Weak' })),
      villagerPerson(hero, newcomer({ Id: 3, Name: 'Strong', AptitudeFortune: 15 })),
    ]);
    expect(sorted.map((p) => p.name)).toEqual(['Strong', 'Weak', 'Spent']);
  });

  // Modul: the comment said "drop the oldest" and the code dropped the lowest
  // index. Fortune (3), then Strength (0), then Skill (1) at a limit of two
  // must keep Strength and Skill.
  it('drops the OLDEST choice past the limit', () => {
    let order: number[] = [];
    order = toggleSelection(order, 3, 2);
    order = toggleSelection(order, 0, 2);
    order = toggleSelection(order, 1, 2);
    expect(order).toEqual([0, 1]);
    expect(selectionMaskOf(order)).toBe(0b0011);

    expect(toggleSelection([0, 1], 0, 2)).toEqual([1]);
    expect(toggleSelection([], 2, 0)).toEqual([]);
  });
});
