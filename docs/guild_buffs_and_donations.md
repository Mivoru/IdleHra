# Implementace Guild Buffů, Pokladnice a Leaderboardu

Zadání obsahovalo celkem rozsáhlé rozšíření cechovního systému. Zde je přehled toho, co bylo úspěšně implementováno a napojeno do hry.

## Co bylo přidáno

### 1. Guild Buffs Logic (Backend)
- Vytvořena nová entita **`GuildActiveBuff`**, která je uložena v databázi a eviduje, jaký buff a do kdy má cech aktivní.
- Vytvořena vyrovnávací paměť **`GuildBonusesCache`**, která na pozadí synchronizuje aktivní buffy. Tato paměť běží asynchronně jako HostedService, aby databáze nebyla přetížena při každém výpočtu.
- **Napojení na herní mechaniky (v `SimulationEngine.cs` a dalších)**:
  - **Experience Boost (Exp)**: +25% bonus ke získaným zkušenostem z boje.
  - **Gold Gain Boost (Gold)**: +25% šance na získání peněz v boji.
  - **Drop Rate Boost**: Aplikuje se multiplikátor 1.2x (tedy 20%) pro všechny šance na padnutí předmětů.
  - **Damage Boost (Damage)**: V bitvě hráči (nebo společníci) uštědřují o 15% větší zranění z base statistik.

### 2. Darování předmětů (Backend API)
- Vytvořen **nový endpoint `/api/v1/guilds/depot/donate`**, který bere `materialId` a `quantity`.
- Systém automaticky vypočítá tzv. **`WeeklyContributionPoints`** (Týdenní body příspěvku) na základě odeslaného materiálu, jeho množství a zejména *Rarity*. Vzácnější materiály dávají obrovsky více bodů.
- Dané materiály jsou odebrány hráči z inventáře a virtuálně umístěny do společné guildovní pokladnice, kde slouží jako palivo k nakupování buffů. (Pevně stanovena cena **50 000 jednotek** materiálů na jeden buff).

### 3. Týdenní Leaderboard (Cron Engine)
- Do `LeaderboardCronEngine` přidána asynchronní úloha `SyncGuildWeeklyLeaderboardsAsync`.
- Ta se vždy 1x týdně spustí, podívá se, kolik peněz (`Gold`) vložili hráči do cechu celkem a vypočítá celkový *Prize Pool* jako rovných 50% z těchto zlaťáků.
- Najde 3 hráče, kteří v uplynulém týdnu nasbírali **nejvíce `WeeklyContributionPoints`**.
- Těmto třem hráčům automaticky zašle odměnu v poměru **25%** (pro 1. místo), **15%** (pro 2. místo) a **10%** (pro 3. místo) z původních *TotalGuildGold*. Odměna se zapíše přímo do `CommodityRecords` jako položka `gold`.
- Jakmile je vyplaceno, **body se resetují** a žebříček pro další týden začíná čistý (od nuly).

### 4. Svelte UI Client (`GuildOps.svelte`)
- Přidána celá nová sekce **Guild Treasury & Buffs**!
- Zobrazuje **aktuální aktivní buffy** - u těch běžících je ukázán čas expirace, u neaktivních je zobrazeno tlačítko na nákup za *50k materiálů*.
- Zobrazuje **Weekly Leaderboard** pro rychlý přehled aktuálních top 3 přispěvatelů (pokud ty sám mezi nimi jsi, zobrazí se tvé jméno s malou značkou).
- **Donate Materials**: Hráč zde může z roletky vybrat libovolný nasbíraný a stohovatelný materiál a darovat jej do pokladnice cechu pro body. Zobrazuje se tam jen to, co zrovna nese u sebe v inventáři.

## Jak to testovat
1. **Ve hře zajdi do záložky "Social/Guild"**. Zde uvidíš novou sekci "Guild Treasury & Buffs".
2. **Přispěj nějakým dřevem/kamením**. Zadej Donate, bodíky se ti okamžitě přičtou a uvidíš se v Leaderboardu (samozřejmě pokud zrovna daruješ nejvíc).
3. **Nakup si Buff**. Jakmile se nasbírá 50,000 materiálů v rámci guildy, může si Leader koupit libovolný z buffů (např. Damage Boost). Bude fungovat ihned pro celou guildu!
4. Týdenní odměna proběhne automaticky se server-resetem a rozdělí zlato mezi top 3 hráče.
