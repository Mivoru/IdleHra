// What each race does - GET /api/v1/races.
//
// Modul: the SERVER writes these sentences (RaceBonusRegistry.Describe), from
// the same constants StatsCalculator fights with. Until 2026-10-29 no screen
// said what a race did at all, and three of the effects had sat broken for
// months, because nobody can notice a bonus they were never told about. Do not
// restate any number here: a second copy of a race table is the drift this
// endpoint exists to prevent.
import { api } from './config';

export interface RaceMasteryLine {
  Level: number;
  Text: string;
}

export interface RaceDescription {
  RaceId: number;
  Name: string;
  Innate: string[];
  Mastery: RaceMasteryLine[];
  /** True: the mastery lines pay the whole account. False: only a character of this race. */
  MasteryAccountWide: boolean;
}

export const raceKeys = {
  descriptions: ['races', 'descriptions'] as const,
};

let descriptionsPromise: Promise<RaceDescription[]> | null = null;

/** Content, like /gamedata: fetched once per page load. */
export function loadRaceDescriptions(): Promise<RaceDescription[]> {
  if (!descriptionsPromise) {
    descriptionsPromise = fetch(api('/api/v1/races'))
      .then((r) => {
        if (!r.ok) throw new Error(`races ${r.status}`);
        return r.json() as Promise<RaceDescription[]>;
      })
      .catch((err) => {
        descriptionsPromise = null;
        throw err;
      });
  }
  return descriptionsPromise;
}

/** The sentence under a mastery line: who it pays. */
export function masteryScope(race: RaceDescription): string {
  return race.MasteryAccountWide
    ? 'Mastery bonuses help every character on the account.'
    : `Mastery bonuses only help a ${race.Name} character.`;
}
