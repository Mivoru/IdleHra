# Antigravity Changelog

## 2026-08-10 - Vizuální vylepšení předmětů a reorganizace UI
**Autor:** Antigravity

### 1. Zobrazení Tieru u předmětů
- **Soubor:** `client_web/src/lib/ui/ItemIcon.svelte`
- **Změna:** Přidáno asynchronní načítání `ContentRegistry` přímo do komponenty `ItemIcon`.
- **Změna:** Vytvořen vizuální odznak pro "Tier" (T1, T2...) zobrazující se v levém dolním rohu ikony předmětu, pokud je tier větší než 0.
- **Změna:** Atribut `title` (tooltip) byl rozšířen, aby kromě jména a rarity obsahoval i informaci o tieru.

### 2. Zobrazení základních statistik předmětů (Attack/Defense)
- **Soubor:** `client_web/src/lib/ui/Affixes.svelte`
- **Změna:** Přidán volitelný parametr `baseItemId`.
- **Změna:** Z `ContentRegistry` se načítají atributy `FlatAttackPower` a `FlatDefenseRating`. Ty se dynamicky vykreslují nad seznamem affixů jako základní atributy s neutrální (šedou) barvou bodu.
- **Soubory:** `client_web/src/routes/Character.svelte`, `client_web/src/routes/Forge.svelte`
- **Změna:** Upraveno volání komponenty `<Affixes />` tak, aby předávala parametr `baseItemId`.

### 3. Extrakce Leaderboardu
- **Soubor:** `client_web/src/routes/Progression.svelte`
- **Změna:** Z této stránky byly úplně odstraněny tabulky "Player Leaderboard" a "Guild Leaderboard", čímž došlo k výraznému zpřehlednění sekce osobního progresu.
- **Soubor:** `client_web/src/routes/Leaderboards.svelte` (NOVÝ)
- **Změna:** Vytvořena nová samostatná komponenta, která obsahuje vyjmuté tabulky pro hráče a gildy včetně veškeré logiky.

### 4. Reorganizace hlavního menu (Navigace)
- **Soubor:** `client_web/src/App.svelte`
- **Změna:** Nová stránka tabulek začleněna do routeru jako `Leaderboards`.
- **Změna:** Skupina v menu "Others" byla přejmenována na logičtější **"Community"**.
- **Změna:** Uvnitř skupiny "Community" byla položka "Social" přejmenována na **"Friends"**.
- **Změna:** Vytvořena zcela nová tematická skupina **"Genetics"**, do které byly z přeplněné záložky "You" přesunuty položky týkající se pokrevní linie a šlechtění (`Breeding`, `Ancestors` a `Inheritance`).
