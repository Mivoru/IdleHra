// Modul: localisation. The descendant of LocalizationMatrix, reading the very
// same `localizations.json` the Unity client does - served over /gamedata, so
// there is one table and not two.
//
// THE LANGUAGE INDEX AND THE WIRE ID ARE DIFFERENT NUMBERS, and that is the
// one trap here. LocalizationMatrix uses 0-based array indices (En 0, Cs 1,
// De 2, Pl 3), while `SwitchLanguage`'s `TargetLanguageId` is 1-4 and
// ValidateLanguageSwitchRequest rejects 0 outright. Sending the index would
// therefore switch to the wrong language at best and be refused at worst, so
// the two are named separately below rather than left as "the language number".

import { writable, derived, get } from 'svelte/store';
import { GAMEDATA_BASE } from '../net/config';

export interface LocalizationRow {
  Key: string;
  En: string;
  Cs: string;
  De: string;
  Pl: string;
}

/** Index is LocalizationMatrix's 0-based ordering; wireId is what the command takes. */
export const LANGUAGES = [
  { index: 0, wireId: 1, code: 'En' as const, name: 'English' },
  { index: 1, wireId: 2, code: 'Cs' as const, name: 'Čeština' },
  { index: 2, wireId: 3, code: 'De' as const, name: 'Deutsch' },
  { index: 3, wireId: 4, code: 'Pl' as const, name: 'Polski' },
];

export type LanguageCode = (typeof LANGUAGES)[number]['code'];

const STORAGE_KEY = 'folkidle.language';

function initialLanguage(): LanguageCode {
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored && LANGUAGES.some((l) => l.code === stored)) return stored as LanguageCode;

  // Fall back to the browser's own preference before English, so a Czech
  // browser gets Czech without touching a setting.
  const preferred = navigator.language?.slice(0, 2).toLowerCase();
  const matched = LANGUAGES.find((l) => l.code.toLowerCase() === preferred);
  return matched?.code ?? 'En';
}

export const language = writable<LanguageCode>('En');
export const translations = writable<Map<string, LocalizationRow>>(new Map());

export function initLanguage(): void {
  language.set(initialLanguage());
}

export function setLanguage(code: LanguageCode): void {
  language.set(code);
  localStorage.setItem(STORAGE_KEY, code);
}

export async function loadTranslations(): Promise<void> {
  const response = await fetch(`${GAMEDATA_BASE}/localizations.json`);
  if (!response.ok) return;
  const rows = (await response.json()) as LocalizationRow[];
  translations.set(new Map(rows.map((row) => [row.Key, row])));
}

/**
 * Reactive lookup: `$t('Key')`.
 *
 * A derived store rather than a bare function, because a plain call in a
 * template does NOT re-run when the language store changes - the text would
 * render once and then stay in whatever language happened to be active at
 * mount. That is a silent failure: the picker appears to do nothing.
 */
export const t = derived([language, translations], ([$language, $translations]) => {
  return (key: string): string => {
    const row = $translations.get(key);
    if (!row) return key;
    return row[$language] || row.En || key;
  };
});

/**
 * Non-reactive lookup, for code outside a template.
 *
 * Falls back to English when a translation is blank, exactly as
 * LocalizationMatrix does - the table has real gaps, and showing an empty
 * string would be worse than showing English. An unknown key returns the key
 * itself, which is visibly wrong rather than invisibly missing.
 */
export function translate(key: string): string {
  const row = get(translations).get(key);
  if (!row) return key;
  const code = get(language);
  return row[code] || row.En || key;
}

/** How many of the 28 keys have a non-empty translation in a language. */
export function coverage(code: LanguageCode): number {
  let translated = 0;
  for (const row of get(translations).values()) {
    if (row[code]) translated++;
  }
  return translated;
}
