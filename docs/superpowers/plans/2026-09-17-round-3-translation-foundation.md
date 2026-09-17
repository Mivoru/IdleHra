# Round 3 Translation Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Widen FolkIdle's language system from four languages to six (En/Cs/De/Pl/Es/Fr) everywhere it is authored — server content, server boot validation, the wire, and the client — and add a mechanical guard against the client asking for a translation key that does not exist. This is the foundation only: no screen's hardcoded strings are migrated in this plan. That is intentionally separate, per-screen, follow-up work (see "Not in this plan" below).

**Architecture:** The language schema exists in three places that must move together: `server/GameData/localizations.json` (content), `ContentRegistry.cs`'s `LocalizationJson` DTO and boot validator (server-side schema + a fail-fast gate), and `client_web/src/lib/ui/i18n.ts`'s `LocalizationRow`/`LANGUAGES` (client-side schema). A fourth piece, `ClientCommandValidator.ValidateLanguageSwitchRequest`, gates which language ids the wire accepts — independent of the schema, it only needs its numeric bound widened. This plan changes all four in lockstep, each as its own reviewable task, then adds a fifth piece (a client-side test) that will catch any future screen-migration task that references a key nobody added, and a sixth (a dev-box tooling change) that lets the existing geometry checkers sweep a non-English language once screens start carrying real translated chrome.

**Tech Stack:** C# / .NET 8 (server, xUnit + Testcontainers Postgres), TypeScript / Svelte 5 (client, Vitest), Playwright (dev-box geometry scripts).

**Spec:** `docs/superpowers/specs/2026-09-16-round-3-translation-design.md`

## Global Constraints

- Six languages, exact wire ids: En=1, Cs=2, De=3, Pl=4, Es=5, Fr=6. (Spec §1)
- Every row in `localizations.json` must have all six language fields non-empty, or the server refuses to boot — this is `ContentRegistry.Initialize`'s existing content-QA gate, extended rather than loosened. (Spec: "Current state, measured")
- No pluralization/interpolation engine. Dynamic values stay translated-prefix-plus-raw-value, exactly as `BossHpPrefix`/`PushQueuePrefix` already work. (Spec §2)
- Content names (monster/item/trait/quest) and player-generated text (chat, character/guild names) are never in scope for this system. (Spec "Scope")
- `TargetLanguageId` is a `byte` already on the wire (`ClientCommandPacket.cs:286`) — widening the accepted range is a validator-constant change only, never a packet-layout change, and never needs `npm run generate:protocol`.

---

### Task 1: Server resolves Spanish and French, and Czech reads correctly again

**Files:**
- Modify: `server/GameData/localizations.json`
- Modify: `server/FolkIdle.Server/Engine/ContentRegistry.cs:263-298` (the `_localizations` field comment and `TryGetLocalization`), `:1205-1220` (the `LocalizationJson` DTO), `:1252-1263` (the `Initialize` non-empty check)
- Test: `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ContentRegistry.TryGetLocalization(string key, string languageCode, out string value)` now resolves `"es"` and `"fr"` to real translated text instead of falling back to English. `ContentRegistry.Initialize` now throws `InvalidOperationException` if any `localizations.json` row is missing `Es` or `Fr`. Task 4 (the client key-guard test) and every future screen-migration task rely on this: a key added to the table must carry all six languages or the next deploy will not boot.

- [ ] **Step 1: Replace `localizations.json` with real Es/Fr content and corrected Cs diacritics**

Every existing row gains `Es` and `Fr`; every `Cs` value is corrected (the file was authored with diacritics stripped — `"Aktivni"` should read `"Aktivní"`). No keys are added or removed, and no `En`/`De`/`Pl` values change.

```json
[
  {
    "Key": "ActiveEventPrefix",
    "En": "Active Event: ",
    "Cs": "Aktivní event: ",
    "De": "Aktives Ereignis: ",
    "Pl": "Aktywne wydarzenie: ",
    "Es": "Evento activo: ",
    "Fr": "Événement actif : "
  },
  {
    "Key": "EventNone",
    "En": "None",
    "Cs": "Žádný",
    "De": "Keins",
    "Pl": "Brak",
    "Es": "Ninguno",
    "Fr": "Aucun"
  },
  {
    "Key": "EventGoldenHarvest",
    "En": "Golden Harvest",
    "Cs": "Zlatá sklizeň",
    "De": "Goldene Ernte",
    "Pl": "Zlote zniwa",
    "Es": "Cosecha Dorada",
    "Fr": "Récolte Dorée"
  },
  {
    "Key": "EventBloodMoon",
    "En": "Blood Moon",
    "Cs": "Krvavý měsíc",
    "De": "Blutmond",
    "Pl": "Krwawy ksiezyc",
    "Es": "Luna de Sangre",
    "Fr": "Lune de Sang"
  },
  {
    "Key": "EventMasterArtisan",
    "En": "Master Artisan",
    "Cs": "Mistr řemesel",
    "De": "Meister Handwerk",
    "Pl": "Mistrz rzemiosla",
    "Es": "Maestro Artesano",
    "Fr": "Maître Artisan"
  },
  {
    "Key": "EventDiamondStar",
    "En": "Diamond Star",
    "Cs": "Diamantová hvězda",
    "De": "Diamantstern",
    "Pl": "Diamentowa gwiazda",
    "Es": "Estrella de Diamante",
    "Fr": "Étoile de Diamant"
  },
  {
    "Key": "PushQueuePrefix",
    "En": "Push Queue: ",
    "Cs": "Push fronta: ",
    "De": "Push Warten: ",
    "Pl": "Kolejka Push: ",
    "Es": "Cola de notificaciones: ",
    "Fr": "File d'attente push : "
  },
  {
    "Key": "BossHpPrefix",
    "En": "Boss HP: ",
    "Cs": "Boss HP: ",
    "De": "Boss LP: ",
    "Pl": "Boss PZ: ",
    "Es": "PV del Jefe: ",
    "Fr": "PV du Boss : "
  },
  {
    "Key": "HeaderMailbox",
    "En": "Mailbox",
    "Cs": "Poštovní schránka",
    "De": "Postfach",
    "Pl": "Skrzynka pocztowa",
    "Es": "Buzón",
    "Fr": "Boîte aux lettres"
  },
  {
    "Key": "HeaderBankVault",
    "En": "Bank Vault",
    "Cs": "Bankovní trezor",
    "De": "Banktresor",
    "Pl": "Skarbiec bankowy",
    "Es": "Bóveda del Banco",
    "Fr": "Coffre-fort"
  },
  {
    "Key": "HeaderStore",
    "En": "Store",
    "Cs": "Obchod",
    "De": "Shop",
    "Pl": "Sklep",
    "Es": "Tienda",
    "Fr": "Boutique"
  },
  {
    "Key": "HeaderSeasonPass",
    "En": "Season Pass",
    "Cs": "Sezónní propustka",
    "De": "Season-Pass",
    "Pl": "Przepustka sezonowa",
    "Es": "Pase de Temporada",
    "Fr": "Passe Saisonnier"
  },
  {
    "Key": "HeaderGuildRoster",
    "En": "Guild Roster",
    "Cs": "Seznam členů gildy",
    "De": "Gildenliste",
    "Pl": "Lista gildii",
    "Es": "Lista del Gremio",
    "Fr": "Liste de la Guilde"
  },
  {
    "Key": "HeaderOfflineSummary",
    "En": "Offline Summary",
    "Cs": "Souhrn offline",
    "De": "Offline-Zusammenfassung",
    "Pl": "Podsumowanie offline",
    "Es": "Resumen sin conexión",
    "Fr": "Résumé hors ligne"
  },
  {
    "Key": "ErrorTransactionPending",
    "En": "Transaction pending",
    "Cs": "Transakce probíhá",
    "De": "Transaktion ausstehend",
    "Pl": "Transakcja w toku",
    "Es": "Transacción pendiente",
    "Fr": "Transaction en cours"
  },
  {
    "Key": "ErrorMaxTierReached",
    "En": "Max tier reached",
    "Cs": "Maximální úroveň dosažena",
    "De": "Maximale Stufe erreicht",
    "Pl": "Osiagnieto maksymalny poziom",
    "Es": "Nivel máximo alcanzado",
    "Fr": "Niveau maximum atteint"
  },
  {
    "Key": "ErrorInsufficientFunds",
    "En": "Insufficient funds",
    "Cs": "Nedostatek prostředků",
    "De": "Unzureichendes Guthaben",
    "Pl": "Niewystarczajace srodki",
    "Es": "Fondos insuficientes",
    "Fr": "Fonds insuffisants"
  },
  {
    "Key": "ErrorInventoryFull",
    "En": "Inventory full",
    "Cs": "Inventář je plný",
    "De": "Inventar voll",
    "Pl": "Ekwipunek jest pelny",
    "Es": "Inventario lleno",
    "Fr": "Inventaire plein"
  },
  {
    "Key": "StateLevelUp",
    "En": "Level Up!",
    "Cs": "Postup na úroveň!",
    "De": "Stufenaufstieg!",
    "Pl": "Nowy poziom!",
    "Es": "¡Subida de nivel!",
    "Fr": "Niveau supérieur !"
  },
  {
    "Key": "StateAllProgressSaved",
    "En": "All progress saved",
    "Cs": "Veškerý postup uložen",
    "De": "Gesamter Fortschritt gespeichert",
    "Pl": "Caly postep zapisany",
    "Es": "Todo el progreso guardado",
    "Fr": "Toute la progression enregistrée"
  },
  {
    "Key": "StateSavedPrefix",
    "En": "Saved ",
    "Cs": "Uloženo před ",
    "De": "Gespeichert vor ",
    "Pl": "Zapisano ",
    "Es": "Guardado hace ",
    "Fr": "Enregistré il y a "
  },
  {
    "Key": "StateMinutesAgoSuffix",
    "En": "m ago",
    "Cs": " min",
    "De": " Min.",
    "Pl": " min temu",
    "Es": " min",
    "Fr": " min"
  },
  {
    "Key": "StateHoursAgoSuffix",
    "En": "h ago",
    "Cs": " hod",
    "De": " Std.",
    "Pl": " godz temu",
    "Es": " h",
    "Fr": " h"
  },
  {
    "Key": "OfflineAwayForPrefix",
    "En": "Away for ",
    "Cs": "Nepřítomen ",
    "De": "Abwesend seit ",
    "Pl": "Nieobecny przez ",
    "Es": "Ausente durante ",
    "Fr": "Absent depuis "
  },
  {
    "Key": "OfflineHoursSuffix",
    "En": "h ",
    "Cs": "h ",
    "De": "Std. ",
    "Pl": "godz ",
    "Es": "h ",
    "Fr": "h "
  },
  {
    "Key": "OfflineMinutesSuffix",
    "En": "m",
    "Cs": "m",
    "De": "Min.",
    "Pl": "min",
    "Es": "m",
    "Fr": "m"
  },
  {
    "Key": "GuildWarStatusActive",
    "En": "War Active",
    "Cs": "Válka probíhá",
    "De": "Krieg aktiv",
    "Pl": "Wojna trwa",
    "Es": "Guerra Activa",
    "Fr": "Guerre Active"
  },
  {
    "Key": "GuildWarStatusInactive",
    "En": "No Active War",
    "Cs": "Žádná aktivní válka",
    "De": "Kein aktiver Krieg",
    "Pl": "Brak aktywnej wojny",
    "Es": "Sin Guerra Activa",
    "Fr": "Aucune Guerre Active"
  }
]
```

This step alone changes no behavior: `System.Text.Json` ignores JSON properties that have no matching C# property, so the server still boots and resolves exactly as before until Step 3.

- [ ] **Step 2: Write the failing tests**

In `HardenedEngineIntegrationTests.cs`, immediately after `Test_ContentRegistry_LocalizationLookup_ResolvesFinalProductionPolishKeysAcrossAllLanguages` (ends at line 6965), add:

```csharp
        // Modul: added when Es/Fr joined the schema. "it" (Italian) stands in
        // for "any language this client still does not support" now that fr
        // is a real, resolved language rather than a stand-in for that case -
        // the test above this one used to make that point with "fr" itself.
        [Fact]
        public void Test_ContentRegistry_LocalizationLookup_ResolvesSpanishAndFrench()
        {
            Assert.True(ContentRegistry.TryGetLocalization("BossHpPrefix", "es", out string esValue));
            Assert.Equal("PV del Jefe: ", esValue);

            Assert.True(ContentRegistry.TryGetLocalization("BossHpPrefix", "fr", out string frValue));
            Assert.Equal("PV du Boss : ", frValue);

            Assert.True(ContentRegistry.TryGetLocalization("EventNone", "es", out string esNone));
            Assert.Equal("Ninguno", esNone);

            Assert.True(ContentRegistry.TryGetLocalization("EventNone", "fr", out string frNone));
            Assert.Equal("Aucun", frNone);

            Assert.True(ContentRegistry.TryGetLocalization("EventNone", "it", out string fallbackValue));
            Assert.Equal("None", fallbackValue);
        }

        // Modul: the row that ships with one missing column is the one nobody
        // notices in review - this proves the boot gate catches exactly one
        // missing language as reliably as it catches all six, mirroring
        // Test_ContentPipeline_MissingOrMalformedJson_FailsFast's temp-dir
        // pattern rather than touching the real, shared GameData directory.
        [Fact]
        public void Test_ContentPipeline_LocalizationMissingATranslation_FailsFast()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "folkidle_content_test_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);

            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "monsters.json"),
                    "[{\"Id\":1,\"MaxHp\":100,\"AttackPower\":1,\"BaseGoldReward\":1,\"BaseXpReward\":1,\"AttackIntervalMs\":1000,\"LootTableId\":1,\"Name\":\"X\",\"EnemyId\":\"x\"}]");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "items.json"), "[]");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "gathering_nodes.json"), "[]");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "localizations.json"),
                    "[{\"Key\":\"X\",\"En\":\"x\",\"Cs\":\"x\",\"De\":\"x\",\"Pl\":\"x\",\"Es\":\"x\",\"Fr\":\"\"}]");

                Assert.Throws<InvalidOperationException>(() => ContentRegistry.Initialize(tempDir));
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }
```

Then change the existing `Test_ContentRegistry_LocalizationLookup_ResolvesGermanAndCzechWithEnglishFallback` (line ~6905-6920) so its "unrecognized language code" assertion no longer uses `"fr"`, since `fr` is about to become a real, resolved language rather than an example of one that is not:

```csharp
            Assert.True(ContentRegistry.TryGetLocalization("EventNone", "it", out string fallbackValue));
            Assert.Equal("None", fallbackValue);
```
(replacing the existing `"fr"` line and its assertion, which currently reads `Assert.True(ContentRegistry.TryGetLocalization("EventNone", "fr", ...)); Assert.Equal("None", fallbackValue);`)

- [ ] **Step 3: Run the tests to verify they fail**

Stop the running server first if one is up (see CLAUDE.md's stale-build rule), then:

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_ContentRegistry_LocalizationLookup_ResolvesSpanishAndFrench|FullyQualifiedName~Test_ContentPipeline_LocalizationMissingATranslation_FailsFast|FullyQualifiedName~Test_ContentRegistry_LocalizationLookup_ResolvesGermanAndCzechWithEnglishFallback"`

Expected: `Test_ContentRegistry_LocalizationLookup_ResolvesSpanishAndFrench` FAILS (`es`/`fr` currently resolve to the English fallback, not the Spanish/French text). `Test_ContentPipeline_LocalizationMissingATranslation_FailsFast` FAILS (`Assert.Throws` fails because `Initialize` does not yet look at `Es`/`Fr` at all, so a row missing `Fr` boots successfully today). `Test_ContentRegistry_LocalizationLookup_ResolvesGermanAndCzechWithEnglishFallback` PASSES already (the `it` fallback works the same way `fr` used to) — that one is not expected to go red, it is only being kept accurate.

- [ ] **Step 4: Implement — widen the DTO, the boot validator, and the resolver**

In `ContentRegistry.cs`, replace the `_localizations` field's doc comment (lines 263-271):

```csharp
        // Modul: Production Release Hardening, Part 3, widened 2026-09-17 to
        // six languages. Keyed by the same Key string localizations.json
        // uses (matches client LocalizationMatrix's LocalizationKey enum
        // member names) mapped to each of the six supported language codes.
        // Server-side exposure exists for content-QA/testability, not
        // because any gameplay logic reads localized text at runtime
        // (nothing does - this is client-rendering-only data); see
        // TryGetLocalization for the fallback-safe (default to "en", never
        // throws) lookup this whole registry exists to prove correct.
```

Replace the `LocalizationJson` DTO and its comment (lines 1205-1220):

```csharp
        // Modul: Production Release Hardening, Part 3, widened 2026-09-17 to
        // six languages (round 3: EN/ES/FR/DE/PL/CS). Flat localization
        // schema - Key mapped directly to each of the six supported
        // languages, one entry per translatable string. Mirrors client
        // LocalizationMatrix.cs's own DTO exactly (Key matches that side's
        // LocalizationKey enum member names, validated there via
        // Enum.TryParse at boot - this server-side DTO deliberately does
        // not depend on that client-only enum, so validation here is
        // purely structural: every field present and non-empty).
        private sealed class LocalizationJson
        {
            public string Key { get; set; } = string.Empty;
            public string En { get; set; } = string.Empty;
            public string Cs { get; set; } = string.Empty;
            public string De { get; set; } = string.Empty;
            public string Pl { get; set; } = string.Empty;
            public string Es { get; set; } = string.Empty;
            public string Fr { get; set; } = string.Empty;
        }
```

Replace the non-empty check inside `Initialize` (lines 1252-1263):

```csharp
            for (int i = 0; i < localizationJson.Count; i++)
            {
                LocalizationJson entry = localizationJson[i];
                if (string.IsNullOrEmpty(entry.Key))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: 'localizations.json' entry at index {i} has an empty Key.");
                }
                if (string.IsNullOrEmpty(entry.En) || string.IsNullOrEmpty(entry.Cs) || string.IsNullOrEmpty(entry.De) || string.IsNullOrEmpty(entry.Pl) || string.IsNullOrEmpty(entry.Es) || string.IsNullOrEmpty(entry.Fr))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: 'localizations.json' entry Key='{entry.Key}' is missing a translation for one or more of En/Cs/De/Pl/Es/Fr.");
                }
            }
```

Replace the `languageCode switch` inside `TryGetLocalization` (lines 282-289):

```csharp
            string? resolved = languageCode switch
            {
                "en" => entry.En,
                "cs" => entry.Cs,
                "de" => entry.De,
                "pl" => entry.Pl,
                "es" => entry.Es,
                "fr" => entry.Fr,
                _ => entry.En
            };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~HardenedEngineIntegrationTests"`

Expected: all PASS, including the three named in Step 3.

- [ ] **Step 6: Commit**

```bash
git add server/GameData/localizations.json server/FolkIdle.Server/Engine/ContentRegistry.cs server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs
git commit -m "feat(i18n): server resolves Spanish and French, Czech diacritics restored"
```

---

### Task 2: The wire accepts language ids 5 and 6

**Files:**
- Modify: `server/FolkIdle.Server/Engine/ClientCommandValidator.cs:1123`
- Test: `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 — this is the wire gate, independent of content.
- Produces: `ClientCommandValidator.ValidateLanguageSwitchRequest` now accepts `TargetLanguageId` 1 through 6. Task 3's client `LANGUAGES` array sends ids up to 6 and relies on the server no longer refusing them.

- [ ] **Step 1: Write the failing test**

In `HardenedEngineIntegrationTests.cs`, directly after the new `Test_ContentPipeline_LocalizationMissingATranslation_FailsFast` from Task 1, add:

```csharp
        // Modul: untested before this - ValidateLanguageSwitchRequest had no
        // dedicated test at all. Six languages now, so ids 5 and 6 must pass
        // and 0 and 7 must still be refused.
        [Fact]
        public void Test_ValidateLanguageSwitchRequest_AcceptsAllSixLanguagesAndRejectsOutOfRange()
        {
            for (byte id = 1; id <= 6; id++)
            {
                var payload = new TickStatePayload { PlayerId = 970003000L + id };
                var packet = new ClientCommandPacket { Command = CommandType.SwitchLanguage, TargetLanguageId = id };
                Assert.True(ClientCommandValidator.ValidateLanguageSwitchRequest(ref payload, ref packet),
                    $"Language id {id} should be accepted - six languages are supported now.");
            }

            var zeroPayload = new TickStatePayload { PlayerId = 970003100L };
            var zeroPacket = new ClientCommandPacket { Command = CommandType.SwitchLanguage, TargetLanguageId = 0 };
            Assert.False(ClientCommandValidator.ValidateLanguageSwitchRequest(ref zeroPayload, ref zeroPacket),
                "0 is not a language, it is 'no field set'.");

            var sevenPayload = new TickStatePayload { PlayerId = 970003101L };
            var sevenPacket = new ClientCommandPacket { Command = CommandType.SwitchLanguage, TargetLanguageId = 7 };
            Assert.False(ClientCommandValidator.ValidateLanguageSwitchRequest(ref sevenPayload, ref sevenPacket),
                "7 is one past the sixth language and must still be refused.");
        }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_ValidateLanguageSwitchRequest_AcceptsAllSixLanguagesAndRejectsOutOfRange"`

Expected: FAIL at id 5 (the first id the current `> 4` bound rejects).

- [ ] **Step 3: Implement**

In `ClientCommandValidator.cs:1123`, change:

```csharp
            if (packet.TargetLanguageId == 0 || packet.TargetLanguageId > 4)
```

to:

```csharp
            if (packet.TargetLanguageId == 0 || packet.TargetLanguageId > 6)
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_ValidateLanguageSwitchRequest_AcceptsAllSixLanguagesAndRejectsOutOfRange"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Engine/ClientCommandValidator.cs server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs
git commit -m "feat(i18n): the wire accepts language ids 5 and 6"
```

---

### Task 3: The client knows about Spanish and French

**Files:**
- Modify: `client_web/src/lib/ui/i18n.ts:1-29`
- Test: `client_web/tests/language.test.ts`

**Interfaces:**
- Consumes: nothing from Tasks 1-2 at compile time (the client and server build independently) — but is meaningless without them: `Settings.svelte`'s picker would offer Spanish/French and send wire ids the server (pre-Task 2) refuses, and would show untranslated (English-fallback) text without Task 1's content.
- Produces: `LANGUAGES` (six entries, `code`/`wireId`/`index`/`name`) and `LocalizationRow` (six string fields) are what every later screen-migration task and Task 4's guard test import.

- [ ] **Step 1: Write the failing test**

In `client_web/tests/language.test.ts`, add a new `describe` block after the existing `'the translation table itself'` block:

```typescript
describe('the six supported languages', () => {
  it('lists all six, with the wire ids the server actually accepts', async () => {
    const { LANGUAGES } = await import('../src/lib/ui/i18n');

    expect(LANGUAGES.map((l) => l.code)).toEqual(['En', 'Cs', 'De', 'Pl', 'Es', 'Fr']);
    expect(LANGUAGES.map((l) => l.wireId)).toEqual([1, 2, 3, 4, 5, 6]);

    // The index is LocalizationMatrix's 0-based ordering, one less than the
    // wire id - i18n.ts's own header comment calls this "the one trap here".
    LANGUAGES.forEach((l) => expect(l.index).toBe(l.wireId - 1));
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd client_web; npx vitest run tests/language.test.ts`

Expected: FAIL — `LANGUAGES.map((l) => l.code)` is currently `['En', 'Cs', 'De', 'Pl']`, length 4, not 6.

- [ ] **Step 3: Implement**

In `client_web/src/lib/ui/i18n.ts`, replace the header comment and the `LocalizationRow`/`LANGUAGES` declarations (lines 1-29):

```typescript
// Modul: localisation. The descendant of LocalizationMatrix, reading the very
// same `localizations.json` the Unity client did - served over /gamedata, so
// there is one table and not two.
//
// THE LANGUAGE INDEX AND THE WIRE ID ARE DIFFERENT NUMBERS, and that is the
// one trap here. LocalizationMatrix uses 0-based array indices, while
// `SwitchLanguage`'s `TargetLanguageId` is 1-based and
// ValidateLanguageSwitchRequest rejects 0 outright. Sending the index would
// therefore switch to the wrong language at best and be refused at worst, so
// the two are named separately below rather than left as "the language
// number".

import { writable, derived, get } from 'svelte/store';
import { GAMEDATA_BASE } from '../net/config';

export interface LocalizationRow {
  Key: string;
  En: string;
  Cs: string;
  De: string;
  Pl: string;
  Es: string;
  Fr: string;
}

/** Index is LocalizationMatrix's 0-based ordering; wireId is what the command takes. */
export const LANGUAGES = [
  { index: 0, wireId: 1, code: 'En' as const, name: 'English' },
  { index: 1, wireId: 2, code: 'Cs' as const, name: 'Čeština' },
  { index: 2, wireId: 3, code: 'De' as const, name: 'Deutsch' },
  { index: 3, wireId: 4, code: 'Pl' as const, name: 'Polski' },
  { index: 4, wireId: 5, code: 'Es' as const, name: 'Español' },
  { index: 5, wireId: 6, code: 'Fr' as const, name: 'Français' },
];

export type LanguageCode = (typeof LANGUAGES)[number]['code'];
```

Nothing below line 29 (`STORAGE_KEY` onward, `t()`, `translate()`, `coverage()`) changes — they are already generic over `LANGUAGES`/`LocalizationRow`.

- [ ] **Step 4: Run it to verify it passes**

Run: `cd client_web; npx vitest run tests/language.test.ts`

Expected: PASS, all tests in the file (including the pre-existing ones — `initLanguage`/`setLanguage` behavior is unchanged).

- [ ] **Step 5: Run the type check**

Run: `cd client_web; npm run check:ratchet`

Expected: `svelte-check errors: 4 (baseline 4)` — unchanged, since `Settings.svelte` iterates `LANGUAGES` generically and never assumed a length of 4.

- [ ] **Step 6: Commit**

```bash
git add client_web/src/lib/ui/i18n.ts client_web/tests/language.test.ts
git commit -m "feat(i18n): the client knows about Spanish and French"
```

---

### Task 4: A renamed or mistyped translation key fails the build, not the game

**Files:**
- Create: `client_web/tests/i18nKeys.test.ts`

**Interfaces:**
- Consumes: `server/GameData/localizations.json`'s `Key` column (read directly as JSON, no server build needed — same technique `language.test.ts` and `serverMirrors.test.ts` already use).
- Produces: a standing guard every future screen-migration task runs into automatically via `npm test` — no new export, this is a test-only file.

- [ ] **Step 1: Write the test**

Create `client_web/tests/i18nKeys.test.ts`:

```typescript
// Modul: A KEY WRITTEN ON ONE SIDE AND READ ON THE OTHER, again - the shape
// serverMirrors.test.ts exists for, applied to the translation table instead
// of a balance constant. `i18n.ts`'s own `t()`/`translate()` return the KEY
// ITSELF for an unknown lookup ("visibly wrong rather than invisibly
// missing") precisely because a call site can reference a key that was
// renamed or never added - this test exists so that is caught here, once,
// rather than read live in a language nobody proofreads by eye.
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const srcRoot = join(repoRoot, 'client_web', 'src');
const i18nModulePath = join('lib', 'ui', 'i18n.ts');

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) walk(full, out);
    else if (extname(entry) === '.svelte' || extname(entry) === '.ts') out.push(full);
  }
  return out;
}

/** Every key passed to `$t(...)` or `translate(...)` anywhere in the client,
 *  found by a plain regex over source - i18n.ts itself is excluded, since it
 *  DEFINES those functions rather than calling them. */
function referencedKeys(): Set<string> {
  const keys = new Set<string>();
  const pattern = /(?:\$t|translate)\(\s*'([A-Za-z0-9_]+)'\s*\)/g;
  for (const file of walk(srcRoot)) {
    if (file.endsWith(i18nModulePath)) continue;
    const text = readFileSync(file, 'utf8');
    for (const m of text.matchAll(pattern)) keys.add(m[1]);
  }
  return keys;
}

describe('every translation key the client asks for exists in the table', () => {
  it('has a row in localizations.json for each $t()/translate() call', () => {
    const rows = JSON.parse(
      readFileSync(join(repoRoot, 'server', 'GameData', 'localizations.json'), 'utf8').replace(/^﻿/, ''),
    ) as { Key: string }[];
    const known = new Set(rows.map((r) => r.Key));

    const used = referencedKeys();
    expect(
      used.size,
      'no $t()/translate() call sites found at all - the scan pattern needs updating, not deleting',
    ).toBeGreaterThan(0);

    const missing = [...used].filter((k) => !known.has(k)).sort();
    expect(missing, `key(s) referenced but not in localizations.json: ${missing.join(', ')}`).toEqual([]);
  });
});
```

- [ ] **Step 2: Run it to verify it passes**

Run: `cd client_web; npx vitest run tests/i18nKeys.test.ts`

Expected: PASS — every key currently referenced (in `Settings.svelte`, `Hub.svelte`, `Combat.svelte`, and the `lib/ui/*` components already wired) is already in the table.

- [ ] **Step 3: Prove the guard actually catches drift**

In `client_web/src/routes/Settings.svelte:382`, temporarily change `{$t('EventNone')}` to `{$t('EventNoneDoesNotExist')}`.

Run: `cd client_web; npx vitest run tests/i18nKeys.test.ts`

Expected: FAIL, naming `EventNoneDoesNotExist` in the failure message.

Revert the temporary edit (`git checkout -- client_web/src/routes/Settings.svelte`), then rerun:

Run: `cd client_web; npx vitest run tests/i18nKeys.test.ts`

Expected: PASS again.

- [ ] **Step 4: Commit**

```bash
git add client_web/tests/i18nKeys.test.ts
git commit -m "test(i18n): a stale translation key fails the suite, not the game"
```

---

### Task 5: The geometry checkers can be pointed at a non-English language

**Files:**
- Modify: `client_web/scripts/screens.mjs:15-52` (the `open()` function)

**Interfaces:**
- Consumes: `i18n.ts`'s `STORAGE_KEY` (`'folkidle.language'`, the localStorage key `initLanguage()` reads) — not imported, matched by literal string, exactly as this file already matches UI text by literal string elsewhere.
- Produces: an `FOLKIDLE_E2E_LANG` environment variable every script that calls `open()` (`clipping-check.mjs`, `overlap-check.mjs`, `touch-check.mjs`, `smoke-screens.mjs`, `exercise.mjs`, and more) now honours automatically, with no per-script change needed.

- [ ] **Step 1: Implement**

In `client_web/scripts/screens.mjs`, add after the `DEV_PASSWORD` constant (line 36) and before `open()`:

```javascript
/**
 * Which language to boot the browser into. Unset (the default) means
 * English, i18n.ts's own default - set FOLKIDLE_E2E_LANG to one of En, Cs,
 * De, Pl, Es, Fr to sweep the geometry checkers in another language, since
 * none of clipping-check.mjs / overlap-check.mjs / touch-check.mjs are
 * language-aware on their own and German/Polish text tends to run longer
 * than English.
 */
export const E2E_LANG = process.env.FOLKIDLE_E2E_LANG || null;
```

Then change `open()` (lines 39-52) to:

```javascript
/** A browser and a page, with console/pageerror collection wired up. */
export async function open({ width = 1500, height = 1000 } = {}) {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width, height } });
  if (E2E_LANG) {
    // Modul: i18n.ts's initLanguage() runs at App.svelte's component-script
    // top level, before any onMount - so setting localStorage AFTER
    // page.goto (e.g. via page.evaluate) would run after the app already
    // read it and defaulted to English. addInitScript runs before every
    // script on the page, including the first one the bundle runs.
    await page.addInitScript((lang) => localStorage.setItem('folkidle.language', lang), E2E_LANG);
  }
  const errors = [];
  page.on('console', (m) => {
    // A 403 is the server saying no CORRECTLY: every client asks
    // /api/v1/admin/status whether this account may see the admin tools, and an
    // ordinary account is told no. The browser logs the refusal regardless.
    if (m.type() === 'error' && /status of 403/.test(m.text())) return;
    if (m.type() === 'error') errors.push(`console: ${m.text()}`);
  });
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  return { browser, page, errors };
}
```

- [ ] **Step 2: Verify the override actually reaches the page**

With the local stack running (`.\run-dev.ps1`), run a short throwaway check from `client_web`:

```bash
node -e "
import('./scripts/screens.mjs').then(async ({ open, signIn }) => {
  process.env.FOLKIDLE_E2E_LANG = 'De';
  const { open: openWithEnv } = await import('./scripts/screens.mjs?t=' + Date.now());
  const { browser, page } = await openWithEnv();
  await signIn(page);
  const stored = await page.evaluate(() => localStorage.getItem('folkidle.language'));
  console.log('localStorage folkidle.language =', stored);
  await browser.close();
  if (stored !== 'De') { console.error('FAIL: expected De'); process.exit(1); }
  console.log('PASS');
});
"
```

Expected: prints `localStorage folkidle.language = De` then `PASS`. (This is a one-off verification, not a committed script — the assertion belongs to the live-browser class of check CLAUDE.md already treats as dev-box-only, like `check:touch`.)

- [ ] **Step 3: Sweep the existing geometry checkers once in German, to confirm nothing already breaks**

```powershell
$env:FOLKIDLE_E2E_LANG='De'; npm run check:clipping
$env:FOLKIDLE_E2E_LANG='De'; npm run check:overlap
```

Expected: same result as an unset-language run today (0 new findings) — only `Settings`, `Hub`, `Combat`, and a handful of `lib/ui/*` components render any German text yet, and all of it already exists in the pre-Task-1 four-language table. This step exists to prove the override is wired end-to-end before any screen-migration task starts relying on it, not to find a defect.

- [ ] **Step 4: Commit**

```bash
git add client_web/scripts/screens.mjs
git commit -m "feat(i18n): geometry checkers can sweep a non-English language via FOLKIDLE_E2E_LANG"
```

---

## Not in this plan

Per the spec's rollout shape ("one spec, many small plans"), migrating any screen's hardcoded strings into keys is deliberately **not** a task here — it is real translated content for each of the ~27 route screens and the `lib/ui/*` components not yet covered, and each screen becomes its own plan when it is picked up, following the pattern Task 1 of this plan already demonstrates (real key names, real six-language content, a test proving resolution). `language.test.ts`'s `rows.length < 200` assertion (the reason `navigator.language` auto-detection is currently off) is unaffected by this plan — this plan adds zero new rows — but the first screen-migration plan that pushes the table past 200 rows must revisit that decision deliberately, per its own comment.
