# SEO and listings

State on 2026-11-15: folkidle.cz is indexed by Google (Search Console verified
2026-10-08, first search traffic 2026-11-11). What the site carries for search
engines, and the steps only the owner can take because they need an account.

## What is on the site

| File | Purpose |
|---|---|
| `client_web/index.html` | Title, description, canonical, Open Graph, JSON-LD `WebSite` + `VideoGame`. The game is an SPA, so this page has almost no readable text. |
| `client_web/public/about.html` | **The readable page** (English). Regions, races, crafts, village, family line, FAQ. |
| `client_web/public/o-hre.html` | The same page in Czech. The two are `hreflang` alternates; change them together. |
| `client_web/public/sitemap.xml` | Lists both pages with their `hreflang` pair and `lastmod`. Bump `lastmod` when the text changes. |
| `client_web/public/robots.txt` | Allows all except `/api/` and `/gamedata/`. |
| `client_web/public/llms.txt` | The same map for AI crawlers. |
| `client_web/public/seznam-wmt-*.txt` | Seznam Webmaster ownership proof (2026-11-15). Keep it: removing it unverifies the site. |
| `ops/oracle/caddy/Caddyfile` | `/download/*` sends `X-Robots-Tag: noindex` (the APK is not a page). |
| `Login.svelte` | "About the game" link, browser only - the one internal link to `about.html`. |

## Owner steps (need an account)

### 1. Search Console

- Sitemaps: resubmit `https://folkidle.cz/sitemap.xml` so the two new pages are fetched.
- URL inspection: request indexing for `https://folkidle.cz/about.html` and `https://folkidle.cz/o-hre.html`.
- After the deploy, click "Validate fix" on the APK issue: it moves to "Excluded by noindex".
- In 2-4 weeks, read Performance > Queries and write the pages towards what people actually search.

### 2. Seznam Webmaster

1. Sign in at https://webmaster.seznam.cz/ with a Seznam account.
2. Add `https://folkidle.cz/`. Verification: Seznam gives a file name or a meta tag. Send it over and it goes into `client_web/public/` (like `google78aba877d2a2cb61.html`) in the next deploy.
3. Submit `https://folkidle.cz/sitemap.xml`.

### 3. Inbound links (biggest effect, nothing on the site replaces them)

Search engines rank pages that other pages link to. Ready texts below.

- **itch.io** - create a "HTML / external link" game page pointing at https://folkidle.cz/. Free, and itch.io pages rank well for "idle rpg browser".
- **r/incremental_games** - post on a Friday in the "Feedback Friday" thread, not as a standalone post (the sub's rules).
- **databaze-her.cz**, **Doupě**, **Zing** - Czech game databases; submit the game.
- **Google Play** - a store listing is the strongest single link. Needs the developer account.
- Czech gaming Discords and Facebook groups about Slavic mythology.

## Ready texts

### Short (EN, ~160 characters)

> FolkIdle - a free idle RPG rooted in Slavic folklore. Fight five regions, raise a family line of heroes, build a village. Plays on while you're away.

### Short (CS)

> FolkIdle – bezplatná idle RPG ze slovanských pověstí. Bojuj v pěti krajích, vychovej rod hrdinů a postav vesnici. Hraje dál, i když jsi pryč.

### Long (EN, itch.io / Reddit)

> **FolkIdle** is a free idle RPG set in Slavic legend, playable in the browser and on Android.
>
> - Five regions, twenty-five monsters, a boss at the end of each region that opens the next.
> - Six races - Human, Fairy, Draugr, Leshy, Vodyanoy and Bies - each with its own aptitudes. Characters of any races marry, and children inherit.
> - Up to three characters work at once: one fights, one chops, one mines. The server keeps playing while you are away.
> - Woodcutting, mining, fishing and herbalism; a village that produces offline; gear with fourteen rarity tiers, rerollable affixes, sets and pets.
> - Guilds, a server-wide world boss, a market, expeditions, dungeons and a free seasonal Chronicle.
>
> No sign-up needed - start as a guest: https://folkidle.cz/

### Long (CS)

> **FolkIdle** je bezplatná idle RPG ze světa slovanských pověstí, v prohlížeči i na Androidu.
>
> - Pět krajů, dvacet pět příšer, na konci každého kraje boss, který otevře další.
> - Šest ras – člověk, víla, draugr, leší, vodník a běs – každá s vlastními vlohami. Postavy se berou a děti dědí.
> - Najednou pracují až tři postavy: jedna bojuje, druhá kácí, třetí těží. Hra běží dál, i když jsi pryč.
> - Dřevorubectví, hornictví, rybaření a bylinkářství; vesnice, která vyrábí i offline; vybavení se čtrnácti úrovněmi vzácnosti, afixy, sadami a mazlíčky.
> - Družiny, světový boss pro celý server, tržiště, výpravy, žaláře a bezplatná sezónní kronika.
>
> Bez registrace, rovnou jako host: https://folkidle.cz/o-hre.html
