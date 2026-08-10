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

### 5. Správa Gild (Guild Management & Přidávání Přátel)
- **Soubor:** `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`
- **Změna:** Úprava hledání hráčů pro přidání do přátel, aby bylo case-insensitive (pomocí `ILike`). Přidány nové HTTP POST endpointy pro `/api/v1/guilds/kick`, `promote`, `demote`.
- **Soubory:** `server/FolkIdle.Server/Domain/Social/GuildManagementEngine.cs`, `server/FolkIdle.Server/Models/GuildMember.cs`
- **Změna:** Zavedeny nové role v gildě: `RoleMember (0)`, `RoleOfficer (1)`, `RoleLeader (2)`. Přidány metody pro povýšení, degradaci a vyhození člena.
- **Soubor:** `client_web/src/routes/GuildOps.svelte`
- **Změna:** Skrytí tlačítek Join a Apply pokud je hráč již v gildě. Zobrazení rolí členů v seznamu. Přidána tlačítka pro povýšení, degradaci a vyhození člena gildy (viditelná pouze pro Leadera a Officery).

### 6. Balancování
- **Soubor:** `server/GameData/monsters.json`
- **Změna:** Zvýšení obtížnosti u prvního bosse (Kelpie Mare of the Depths), který byl příliš slabý na Lifesteal. Zvýšen útok z 4378 na 6500, MaxHp ze 772k na 1.05M a zrychlen AttackIntervalMs z 2200 na 1800.

### 7. Lifesteal Nerf
- **Soubor:** `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs`
- **Změna:** Snížen strop pro léčení z Lifestealu z 5 % na 1 % z maximálního HP hráče za zásah, aby hráči s rychlým útokem nebyli vůči pomalým bossům zcela nesmrtelní.

### 8. Vytvoření herní Wiki
- **Soubory:** `client_web/src/routes/Wiki.svelte` (NOVÝ), `client_web/src/App.svelte`
- **Změna:** Vytvořena plnohodnotná in-game encyklopedie inspirovaná Terrarií. Rozdělena do kategorií (Basics, Combat, Items, Map, Gathering, Genetics, Guilds).
- **Změna:** Do hlavního menu přidána nová skupina navigace "Others", přes kterou se do Wiki lze dostat.

### 9. Zobrazení Set Bonusů u Postavy
- **Soubor:** `client_web/src/lib/net/content.ts`
- **Změna:** Zavedena funkce `getArmourFamily` pro zjištění rodiny vybavení přímo z klienta na základě předpony v `BaseItemId`.
- **Soubor:** `client_web/src/routes/Character.svelte`
- **Změna:** Přidán seznam "Active Set Bonuses" vykreslovaný dynamicky, pokud hráč nasadí 2 a více kusů z jedné rodiny (např. Linen, Steel, Magus). Zobrazuje aktuální počet nasazených kusů setu.
