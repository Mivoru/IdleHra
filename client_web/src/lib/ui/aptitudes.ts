// Modul: the four aptitudes in their wire order, with the short labels the
// village and the Hall print over each number. Those numbers used to be bare
// ("4 3 9 2"), named only in a `title`, which a phone never shows.
// PersonPicker carries the same four labels.

export interface AptitudeCarrier {
  AptitudeStrength: number;
  AptitudeSkill: number;
  AptitudeEndurance: number;
  AptitudeFortune: number;
}

export const APTITUDES = [
  { short: 'STR', name: 'Strength', of: (p: AptitudeCarrier) => p.AptitudeStrength },
  { short: 'SKL', name: 'Skill', of: (p: AptitudeCarrier) => p.AptitudeSkill },
  { short: 'END', name: 'Endurance', of: (p: AptitudeCarrier) => p.AptitudeEndurance },
  { short: 'FOR', name: 'Fortune', of: (p: AptitudeCarrier) => p.AptitudeFortune },
] as const;
