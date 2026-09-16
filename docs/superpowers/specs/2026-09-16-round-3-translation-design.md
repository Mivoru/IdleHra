# Round 3: translate the client's UI chrome

Status: approved design, not yet planned or implemented.
Owner's ordering: round 1 (breeding fixes) and round 2 (heritable traits) are
both shipped. This is round 3 of 3: "translate the whole game into
EN/ES/FR/DE/PL/CS."

## Scope

**In scope — "UI chrome":**
- Static text authored directly in a `.svelte` file: button labels, screen
  headers, tooltips, toasts, validation/error messages, onboarding coach text.
- Server-pushed dynamic strings already in `localizations.json`'s existing
  category — event names, notification prefixes (`"Boss HP: "`,
  `"Active Event: "`). Extending this category, not replacing it.

**Explicitly out of scope**, confirmed with the owner:
- Content names authored in `GameData/*.json` — monster names, item names,
  trait names (`TraitRegistry`), quest/lore text. These are a different
  system with a different owner (game design, not UI) and a different-sized
  problem (thousands of proper nouns vs. UI phrases). A future round if the
  owner wants it.
- Player-generated content — chat messages, character names, guild names.
  Never translated; listed here only to rule it out explicitly.
- `docs/CHANGELOG_ANTIGRAVITY.md` — Czech, written by a different tool,
  already documented in `CLAUDE.md` as not authoritative and out of this
  system entirely.

## Current state, measured

`client_web/src/lib/ui/i18n.ts` already carries the exact measurement that
sizes this task, in its own header comment:

> `localizations.json` holds 28 rows, 25 of which are genuinely translated.
> The client renders over 500 user-visible English strings that are not in
> that table at all.

Confirmed by grepping for i18n imports: only 3 of 27 route screens
(`Settings.svelte`, `Hub.svelte`, `Combat.svelte`) and a handful of shared
`lib/ui/*` components (`Toasts`, `OnboardingCoach`, `EventBanner`, `ChatDock`,
`AchievementToast`, `HitSpark`, `FloatingDamage`, `Burst`, `races.ts`) touch
the translation system at all. The other ~23 screens are English-only,
hardcoded.

Four languages exist today: En, Cs, De, Pl. Cs is missing diacritics
("Aktivni", "Zadny" — a typo, not a style). Es and Fr do not exist as columns
anywhere in the schema.

**The schema exists in two places that must move together** — this is the
finding that changes this task's risk profile:
- Client: `LocalizationRow` interface in `i18n.ts`.
- Server: `LocalizationJson` DTO in `ContentRegistry.cs` (~line 1213), whose
  `Initialize` boot path (~line 1252-1263) throws
  `InvalidOperationException` and **refuses to start the server** if any row
  is missing `En`/`Cs`/`De`/`Pl`. This is not dead code mirroring the client —
  it is an independent content-QA gate, "the same safety net every other
  authored content file gets." Adding `Es`/`Fr` columns to the JSON without
  teaching this validator about them is invisible until the next deploy,
  where it becomes a boot crash.

The wire already has headroom: `ClientCommandPacket.TargetLanguageId` is a
`byte`, and `ValidateLanguageSwitchRequest` merely rejects `> 4`. Two more
languages is a validator-constant change, not a packet change — no
`generate:protocol` run needed.

## Design

### 1. Widen the schema, in both places, together

- `server/GameData/localizations.json`: every existing row gains `Es` and
  `Fr` columns. Existing `Cs` values are corrected for diacritics in the same
  pass (they are being touched anyway).
- `ContentRegistry.cs`'s `LocalizationJson` DTO gains `Es`/`Fr` fields; the
  `Initialize` non-empty check is extended to all six. A row missing any of
  the six still crash-fails boot, preserving the existing safety net rather
  than loosening it.
- `client_web/src/lib/ui/i18n.ts`: `LocalizationRow` gains `Es`/`Fr`.
  `LANGUAGES` gains two entries:
  `{ index: 4, wireId: 5, code: 'Es', name: 'Español' }` and
  `{ index: 5, wireId: 6, code: 'Fr', name: 'Français' }`. `t()`,
  `translate()`, and `coverage()` need no changes — already generic over the
  row shape.
- `ClientCommandValidator.ValidateLanguageSwitchRequest`: bound widens from
  `packet.TargetLanguageId > 4` to `> 6`.
- `Settings.svelte`'s picker needs **no changes** — it already iterates
  `LANGUAGES` and renders a `translated/total` coverage badge per entry from
  `coverage()`. That badge is this project's built-in, player-visible
  progress meter; no new UI is being built to track rollout.

This is a symmetrical, mechanical change across four files, done once, before
any screen migration starts — every subsequent task can then just add rows
and rely on the six-language shape already being valid everywhere.

### 2. Key convention: no new templating

New keys follow the existing style (`HeaderMailbox`, `BossHpPrefix`):
`PascalCase`, `<Screen><Purpose>` — e.g. `BreedingPickHeroLabel`,
`SettingsLanguagePickerTitle`.

Existing dynamic strings are **translated prefixes concatenated with a raw
value in code** (`"Active Event: " + eventName`), not a template/placeholder
system. This design keeps that convention rather than building an
interpolation engine: the large majority of chrome text is static labels,
headers, and buttons with no embedded value, and building placeholder
support is scope nobody asked for. A screen whose chrome genuinely needs an
embedded value (a count, a name) gets a translated prefix/suffix pair, the
same shape `BossHpPrefix`/`PushQueuePrefix` already use. If a string turns
out to need a value in the *middle* of a sentence in some language (common in
German/Polish), that is a real limitation of this convention and is called
out per-screen if it happens, not solved speculatively here.

### 3. Translation authorship

Per the owner's direction, all five non-English languages (Cs, De, Pl, Es,
Fr) are drafted directly by the implementer as each key is extracted — no
external translator dependency, no placeholder-then-backfill step. This
matches the existing precedent (the current 4-language table was populated
the same way) and keeps the task self-contained. Quality is "good enough for
a solo/small-team game," correctable per-string later; it is not run past a
native-speaker review as part of this round.

### 4. Migration shape: one spec, many small plan tasks

Per the owner's chosen rollout shape, matching the breeding-traits plan's
precedent (13 numbered tasks, each its own commit):

- **Task 0** (this spec's mechanism section): the four-file schema widen,
  landed and tested before any screen migration starts.
- **One task per screen**, or a small natural grouping for screens with very
  little static text, each following the same inner loop:
  1. Read the screen, list every hardcoded user-visible string.
  2. Add a key + all six translations to `localizations.json` for each.
  3. Replace the hardcoded string with `{$t('Key')}` (or `translate('Key')`
     outside a template).
  4. `npm run check:ratchet`, `npm run exercise`, and — for screens already
     covered by `npm run check:clipping` / `check:overlap` — a pass with a
     non-English language selected (see Risks).
  5. Commit.
- Screens already partly wired (`Settings`, `Hub`, `Combat`, and the shared
  `lib/ui/*` components) get a "finish the job" task each, not a fresh one —
  the remaining hardcoded strings on those screens are found the same way.
- **Suggested order**: `Login` (first thing any player sees, and currently
  has zero i18n coverage) → `Hub`/`Combat`/`Village` (core loop, partial
  coverage already, highest player-visible payoff) → the remaining ~20
  screens, roughly in `screens.mjs`'s existing order so each task's
  verification step has a natural home.
- The implementation plan (written next, via the `writing-plans` skill)
  turns this into the actual numbered task list with file-level detail per
  screen, the way `docs/superpowers/plans/2026-09-13-breeding-traits.md` did
  for round 2.

### 5. Guardrail against drift

This codebase's own documented dominant bug class is two copies of one
truth silently disagreeing (`CLAUDE.md`, repeated across a dozen incidents).
This task creates exactly that shape twice — client `LANGUAGES`/schema vs.
server `LocalizationJson`, and every `t('Key')`/`$t('Key')` call site vs. the
`localizations.json` table — so both get a mechanical guard:

- The **server boot validator** (existing, extended in step 1) already
  guards one direction: an incomplete row can never reach a running server.
- A **new client test** (`client_web/tests/i18n.test.ts` or similar) scans
  source for every `t('Key')` / `$t('Key')` call and asserts the key exists
  in `localizations.json` — the same shape as `serverMirrors.test.ts`. This
  guards the other direction: a renamed or typo'd key reads as the literal
  key string in-game (already `i18n.ts`'s own designed fallback for an
  *unknown* key — "visibly wrong rather than invisibly missing") but should
  never survive to a commit un-caught.

## Risks

- **Text length varies by language**, and this repo has aggressive,
  measured geometry checkers (`check:clipping`, `check:overlap`,
  `check:touch`) precisely because a control squeezed to zero width is
  invisible to every other kind of check. **None of the three geometry
  scripts are language-aware today** — checked directly in
  `clipping-check.mjs`, no language/locale handling at all. German and
  Polish are the two languages already in the table most likely to run
  longer than English. Mitigation: add a language override to the geometry
  scripts (e.g. `FOLKIDLE_E2E_LANG=De`, setting the same `localStorage` key
  `i18n.ts` reads before navigating) as part of Task 0 or an early task, then
  run `check:clipping`/`check:overlap` once against German near the end of
  the rollout — not per-screen, to avoid multiplying an already-large task by
  six.
- **Scope creep into content names.** The boundary (chrome only, not
  monster/item/trait names) is easy to blur mid-task, since a screen like
  `Wiki.svelte` mixes both in the same view. Each screen task should name
  explicitly which strings it touched and which it deliberately left as
  content.
- **~500 strings × 5 languages is a lot of judgment calls with no review
  step.** Accepted per the owner's chosen authorship option; flagged here so
  it is a recorded decision, not a silent gap.

## Done when

- `localizations.json`, `ContentRegistry.cs`'s DTO/validator, `i18n.ts`'s
  schema, and `ValidateLanguageSwitchRequest` all agree on six languages.
- Every one of the 27 route screens and the shared `lib/ui/*` chrome renders
  through `t()`/`translate()` rather than a hardcoded English string, for the
  categories defined as in-scope above.
- `Settings.svelte`'s coverage badge reads at or near 100% for all six
  languages (some gap is acceptable where a string turned out to be content,
  not chrome, and was correctly left out).
- The key-existence guard test passes, and the server boot validator passes
  for all six languages on every row.
- `npm run exercise`, `npm run check:ratchet`, and a German-language pass of
  `check:clipping`/`check:overlap` are all green.
