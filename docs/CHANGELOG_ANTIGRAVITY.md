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

### 9. Vylepšení chatu a integrace online hráčů
- **Soubor:** `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`
- **Změna:** Vytvořeny nové endpointy `/api/v1/players/resolve` pro překlad jména na PlayerId a `/api/v1/stats/online` pro sledování počtu aktivních hráčů.
- **Soubor:** `client_web/src/lib/ui/ContextMenu.svelte` (NOVÝ), `client_web/src/routes/Chat.svelte`
- **Změna:** Do chatu přidáno kontextové menu po kliknutí na jméno hráče, které umožňuje rychlé akce: Whisper, Add Friend, Block a View Profile. Zobrazení počtu online hráčů.

### 10. Inspekce hráčských profilů (Player Inspection)
- **Soubor:** `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`
- **Změna:** Přidán endpoint `/api/v1/players/profile?id=X` vracející všechny postavy daného hráče, jejich level, experience a detailní informace o jejich vybavených předmětech a affixech.
- **Soubor:** `client_web/src/lib/ui/PlayerProfileModal.svelte` (NOVÝ)
- **Změna:** Nová UI komponenta (okno) umožňující v reálném čase prohlížet vybavené předměty a základní statistiky ostatních hráčů (přístupné přes kontextové menu v chatu).

### 11. Kompletní přepracování herní Wiki
- **Soubor:** `client_web/src/routes/Wiki.svelte`
- **Změna:** Kompletní vizuální a funkční restrukturalizace Wiki. Jediná dlouhá stránka byla rozdělena na přehledné záložky v postranním panelu.
- **Soubory:** `WikiItemDatabase.svelte`, `WikiDropChances.svelte`, `WikiMonsterDrops.svelte` (NOVÉ)
- **Změna:** Přidána interaktivní kalkulačka šance na padnutí různých tierů rarit podle štěstí hráče.
- **Změna:** Přidán interaktivní vyhledatelný glosář všech předmětů (`ItemDatabase`) se základními statistikami.
- **Změna:** K bestiáři a mapám byly připojeny reálné dropy všech monster získané z API (`/api/v1/monsters/loot`), včetně přesné procentuální šance a množství.
- **Změna:** Integrováno grafické zobrazení ras a doplněno vysvětlení ke Stromu Dovedností (Skill Tree).

### 12. Zobrazení Set Bonusů u Postavy
- **Soubor:** `client_web/src/lib/net/content.ts`
- **Změna:** Zavedena funkce `getArmourFamily` pro zjištění rodiny vybavení přímo z klienta na základě předpony v `BaseItemId`.
- **Soubor:** `client_web/src/routes/Character.svelte`
- **Změna:** Přidán seznam "Active Set Bonuses" vykreslovaný dynamicky, pokud hráč nasadí 2 a více kusů z jedné rodiny (např. Linen, Steel, Magus). Zobrazuje aktuální počet nasazených kusů setu.
