<script lang="ts">
  // Modul: the wiki. Fifteen pages, a search across all of them, and a ledger
  // at the end saying which of the game's screens each one covers.
  //
  // THE RULE THIS PAGE IS WRITTEN UNDER: a claim here is either read live from
  // the server, computed from a mirror that a test holds against the C#, or
  // sourced from a named engine file. It is not written from documentation.
  // Three of the sentences that used to be on this page were wrong in exactly
  // the way documentation goes wrong - plausible, confident, and describing a
  // rule the code had moved on from:
  //
  //   "DEX increases Accuracy and Dodge Rating"  - DEX grants no dodge at all;
  //                                                StatsCalculator sets
  //                                                DodgeChancePct to 0.
  //   "STR increases Attack Power and Block"     - block is CON's.
  //   "Inheritance: perks from your Ancestors"   - Inheritance is the diamond
  //                                                shop, and it has nothing to
  //                                                do with the Hall.
  //
  // See docs/breeding_model.md section 0 for the last of those: the words
  // aptitude / bloodline / gene / copy / ancestor / elder / gene pool /
  // fielding are canon, and "Inheritance" capitalised means the diamond screen
  // and nothing else.

  import { onMount } from 'svelte';
  import { loadContent, type ContentRegistry, getArmourFamily } from '../lib/net/content';
  import { locationName, nodeLocation, LOCATION_NAMES } from '../lib/ui/locations';
  import { professionName, EQUIPMENT_SLOTS, HALT_REASONS, AGE_PHASES } from '../lib/ui/slots';
  import { RACE_NAMES, ALL_RACE_IDS } from '../lib/ui/races';
  import { RARITY_TIER_NAMES, rarityColor, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import WikiDropChances from '../lib/ui/WikiDropChances.svelte';
  import WikiMonsterDrops from '../lib/ui/WikiMonsterDrops.svelte';
  import WikiItemDatabase from '../lib/ui/WikiItemDatabase.svelte';
  import WikiVillage from '../lib/ui/WikiVillage.svelte';
  import WikiRecipes from '../lib/ui/WikiRecipes.svelte';
  import WikiGuildBuffs from '../lib/ui/WikiGuildBuffs.svelte';
  import WikiScreenIndex from '../lib/ui/WikiScreenIndex.svelte';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import {
    SKILL_TREE_NODES,
    SKILL_TREE_ROOT_MAX,
    SKILL_TREE_BOUGH_MAX,
    SKILL_TREE_BOUGH_COST,
    SKILL_TREE_CROWN_COST,
    SKILL_TREE_BOUGH_NEEDS_ROOT,
    SKILL_TREE_CROWN_NEEDS_BOUGH,
    INHERITANCE_STATS,
    INHERITANCE_MAX_LEVEL,
    INHERITANCE_PCT_PER_LEVEL,
    inheritanceUpgradeCost,
    APTITUDES,
    APTITUDE_MAX,
    APTITUDE_VILLAGE_CEILING,
    aptitudeBonusPercent,
  } from '../lib/net/commands';
  import {
    REROLL_GOLD_BY_REGION,
    AFFIX_COUNT_BANDS,
    AFFIX_RARITIES,
    AFFIX_POOL,
    TOOL_TIERS,
    MASTERY_SPEED_PCT_PER_LEVEL,
    VILLAGE_SPEED_PCT_PER_LEVEL,
    MIN_GATHER_TICKS,
    DEED_CHAPTERS,
    SKILL_POINTS_PER_SEAL,
    HALL_BASE_SLOTS,
    HALL_MAX_SLOTS,
    HALL_SLOT_COSTS,
    CULL_ORDER,
    SEASON_CARRIES,
    SEASON_LOST,
    GUILD_TAX_MIN_PCT,
    GUILD_TAX_MAX_PCT,
    MARKET_FEE_BRACKETS,
    WORLD_BOSS_HP,
    WORLD_BOSS_ATTEMPTS,
    WORLD_BOSS_DAMAGE_FLOOR,
    WORLD_BOSS_PLATES,
    WORLD_BOSS_WEAK_MULTIPLIER,
    WORLD_BOSS_SESSION_MINUTES,
    WORLD_BOSS_MAILBOX_LIMIT,
    WORLD_BOSS_REWARDS,
    DAILY_LOGIN_MATRICES,
    DAILY_LOGIN_DAY7_DIAMONDS,
    ACHIEVEMENTS,
    WIKI_SEARCH_INDEX,
    SLOT2_TOWN_HALL,
    SLOT3_TOWN_HALL,
    WAREHOUSE_PER_LEVEL,
  } from '../lib/ui/wikiData';

  let registry = $state<ContentRegistry | null>(null);
  let activeTab = $state('basics');
  let search = $state('');
  let page = $state<HTMLElement | null>(null);

  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  interface Tab {
    id: string;
    label: string;
    sub: string;
  }

  const GROUPS: readonly { label: string; tabs: readonly Tab[] }[] = [
    {
      label: 'Starting out',
      tabs: [
        { id: 'basics', label: 'Basics', sub: 'The core loop' },
        { id: 'combat', label: 'Combat & stats', sub: 'Fighting and surviving' },
        { id: 'skills', label: 'Skill tree', sub: 'Where points go' },
      ],
    },
    {
      label: 'Gear',
      tabs: [
        { id: 'items', label: 'Items & rarity', sub: 'Loot and the catalogue' },
        { id: 'forge', label: 'Forge & rerolls', sub: 'Fusion and affixes' },
      ],
    },
    {
      label: 'The world',
      tabs: [
        { id: 'map', label: 'Map & regions', sub: 'Where things live' },
        { id: 'gathering', label: 'Gathering & tools', sub: 'The other half' },
        { id: 'crafting', label: 'Crafting', sub: 'The bench' },
      ],
    },
    {
      label: 'Your village',
      tabs: [
        { id: 'village', label: 'The Village', sub: 'Buildings and costs' },
        { id: 'breeding', label: 'Breeding', sub: 'Aptitudes and bloodlines' },
        { id: 'longgame', label: 'The Long Game', sub: 'Seasons, Seals, the Hall' },
      ],
    },
    {
      label: 'Together',
      tabs: [
        { id: 'guilds', label: 'Guilds & social', sub: 'Buffs and channels' },
        { id: 'economy', label: 'Market & mail', sub: 'Trading and diamonds' },
        { id: 'events', label: 'Events & rewards', sub: 'World boss, daily, deeds' },
      ],
    },
    {
      label: 'Reference',
      tabs: [{ id: 'screens', label: 'Screen index', sub: 'What is documented' }],
    },
  ];

  const ALL_TABS = GROUPS.flatMap((g) => g.tabs);

  const currentTab = $derived(ALL_TABS.find((t) => t.id === activeTab) ?? ALL_TABS[0]);

  // --- search ---------------------------------------------------------------
  //
  // Over the hand-kept index in wikiData rather than over the DOM: only the
  // open tab is rendered, so a DOM search would only ever find the page the
  // reader is already on.
  const results = $derived.by(() => {
    const needle = search.trim().toLowerCase();
    if (needle.length < 2) return [];
    const words = needle.split(/\s+/);
    return WIKI_SEARCH_INDEX.filter((entry) => {
      const haystack = `${entry.title} ${entry.keywords} ${
        ALL_TABS.find((t) => t.id === entry.tab)?.label ?? ''
      }`.toLowerCase();
      return words.every((word) => haystack.includes(word));
    }).slice(0, 12);
  });

  function goTo(tab: string, anchor?: string) {
    activeTab = tab;
    search = '';
    // The section only exists after the tab has rendered.
    requestAnimationFrame(() => {
      if (!anchor) {
        page?.scrollIntoView({ block: 'start', behavior: 'smooth' });
        return;
      }
      document.getElementById(anchor)?.scrollIntoView({ block: 'start', behavior: 'smooth' });
    });
  }

  // --- derived content ------------------------------------------------------

  const armourFamilies = $derived.by(() => {
    if (!registry) return [];
    const sets = new Set<string>();
    for (const item of registry.items.values()) {
      const fam = getArmourFamily(item.BaseId);
      if (fam) sets.add(fam);
    }
    return Array.from(sets).sort();
  });

  /** The average gold an ordinary (non-boss) monster of a region pays. */
  const regionGold = $derived.by(() => {
    if (!registry) return [] as number[];
    return registry.regions.map((region) => {
      const regulars = region.slice(0, 4);
      if (regulars.length === 0) return 0;
      return regulars.reduce((sum, m) => sum + m.BaseGoldReward, 0) / regulars.length;
    });
  });

  /** A reroll priced in kills, which is the unit a player actually has. */
  const rerollInKills = $derived.by(() =>
    regionGold.map((gold, index) =>
      gold > 0 ? Math.ceil(REROLL_GOLD_BY_REGION[index + 1] / gold) : 0,
    ),
  );

  const nodesByProfession = $derived.by(() => {
    if (!registry) return [];
    const grouped = new Map<number, typeof registry.gatheringNodes>();
    for (const node of registry.gatheringNodes) {
      const list = grouped.get(node.ProfessionType) ?? [];
      list.push(node);
      grouped.set(node.ProfessionType, list);
    }
    return Array.from(grouped.entries()).sort((a, b) => a[0] - b[0]);
  });

  const REGION_BOSS_RACE: readonly { region: number; boss: string; race: string }[] = [
    { region: 1, boss: 'Alpha Wolf', race: 'Vila' },
    { region: 2, boss: 'Shadow Lynx', race: 'Draugr' },
    { region: 3, boss: 'Magma Wyrm', race: 'Kobold' },
    { region: 4, boss: 'Frost Titan', race: 'Vodnik' },
    { region: 5, boss: 'Malakor', race: 'Moosleute' },
  ];

  const rootNodes = SKILL_TREE_NODES.filter((n) => n.ring === 'root');
</script>

<div class="wrap">
  <div class="wiki-layout">
    <aside class="panel sidebar">
      <h2>Wiki</h2>
      <input
        class="search"
        type="search"
        bind:value={search}
        placeholder="Search every page…"
        aria-label="Search the wiki"
      />

      {#if search.trim().length >= 2}
        <div class="results">
          {#if results.length === 0}
            <p class="dim tiny">Nothing matches "{search.trim()}".</p>
          {:else}
            {#each results as result (result.tab + result.anchor)}
              <button class="result" type="button" onclick={() => goTo(result.tab, result.anchor)}>
                <span class="r-title">{result.title}</span>
                <span class="dim tiny">
                  {ALL_TABS.find((t) => t.id === result.tab)?.label}
                </span>
              </button>
            {/each}
          {/if}
        </div>
      {:else}
        <nav class="wiki-nav">
          {#each GROUPS as group (group.label)}
            <p class="group">{group.label}</p>
            {#each group.tabs as tab (tab.id)}
              <button
                class="tab-btn"
                class:active={activeTab === tab.id}
                onclick={() => goTo(tab.id)}
              >
                <span class="t-label">{tab.label}</span>
                <span class="dim tiny">{tab.sub}</span>
              </button>
            {/each}
          {/each}
        </nav>
      {/if}
    </aside>

    <main class="panel content" bind:this={page}>
      {#if !registry}
        <Skeleton rows={10} />
      {:else}
        <div class="head">
          <h2>{currentTab.label}</h2>
          <span class="dim tiny">{currentTab.sub}</span>
        </div>

        <!-- ================================================== BASICS -->
        {#if activeTab === 'basics'}
          <p>
            FolkIdle plays itself. You choose what a character is doing — fighting
            a particular monster, working a particular gathering node, or crafting
            — and the server runs that choice ten times a second whether or not
            this page is open. Everything else in the game is a decision about
            what to point that loop at next.
          </p>

          <!-- Modul: THE LARDER IS FIRST, and "the fourth monster" was wrong.
               This list used to open with Fight and put the larder third,
               saying it was the fourth monster of a region that would kill an
               unfed character. Measured on a brand-new account: the FIRST
               monster kills it, at 29 seconds, with Field Mouse still on 264
               of its 465 HP. Onboarding made the same mistake and made it
               fatal - see docs/onboarding_steps.md section 2 - and a wiki that
               still taught the old order would have been the surviving copy of
               it. -->
          <h3 id="the-loop">The core loop</h3>
          <ol class="steps">
            <li><strong>Fill the larder first.</strong> Fish, then load the catch into Auto-Eat. It heals you mid-fight, and without it the very first monster kills you before you can kill it.</li>
            <li><strong>Fight.</strong> Kills pay experience, gold and — 15% of the time — a piece of equipment.</li>
            <li><strong>Wear what drops.</strong> Nearly all of your power is gear, not levels. A level gives a Warrior no health at all.</li>
            <li><strong>Gather.</strong> Logs and ore build the village; the village unlocks characters, breeding and offline income.</li>
            <li><strong>Beat the boss.</strong> Each region's boss opens the next region — and the gear of that region, which you may not wear before it.</li>
          </ol>

          <h3 id="offline">Offline progression</h3>
          <p class="dim small">
            When you close the page your characters keep their assignment and the
            server extrapolates what they would have done, up to a cap. You get a
            summary of the loot and experience on your return. The Town Hall pays
            gold and the Lumberjack and Mine produce materials over the same
            window, capped by the Warehouse at {WAREHOUSE_PER_LEVEL.toLocaleString()} per level per
            material — a Warehouse at level 0 banks nothing at all.
          </p>

          <h3 id="halts">Why a character stopped</h3>
          <p class="dim small">
            An idle character and a halted one look identical, so the game names
            the reason. These are the ones it can say:
          </p>
          <ul class="styled-list">
            {#each Object.entries(HALT_REASONS).filter(([, text]) => text) as [code, text] (code)}
              <li>{text}</li>
            {/each}
          </ul>

          <h3 id="currencies">Gold, diamonds and materials</h3>
          <dl class="stats-list">
            <div>
              <dt>Gold</dt>
              <dd>
                Falls out of every kill and every sale. It buys village upgrades,
                fusions and affix rerolls. Most players end a season with far more
                than they spent.
              </dd>
            </div>
            <div>
              <dt>Diamonds</dt>
              <dd>
                The premium currency. Earned from the day-7 login bonus and from
                achievements, or bought. There are exactly three things worth
                spending them on: Inheritance levels, Hall of Ancestors slots, and
                upgrading a single affix's rarity.
              </dd>
            </div>
            <div>
              <dt>Materials</dt>
              <dd>
                Logs, ore and fish. Gathered, or produced by the village while you
                are away. Never sold to a vendor — they are what the village and
                the workbench eat.
              </dd>
            </div>
          </dl>

        <!-- ================================================== COMBAT -->
        {:else if activeTab === 'combat'}
          <p>
            Combat is automatic. Everything you decide happens before the fight
            starts: which monster, what you are wearing, and what is in the larder.
          </p>

          <h3 id="attributes">STR, DEX, CON and LCK</h3>
          <p class="dim small">
            Per point, from <code>StatsCalculator.Calculate</code>. Note what is
            <em>not</em> here: nothing grants dodge, and block belongs to CON.
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr><th>Attribute</th><th>Per point</th></tr>
              </thead>
              <tbody>
                <tr>
                  <td><strong>STR</strong> Strength</td>
                  <td>+2 melee damage, +1 armour penetration</td>
                </tr>
                <tr>
                  <td><strong>DEX</strong> Dexterity</td>
                  <td>+2 ranged damage, +0.05% attack speed, +0.1% crit chance, +1 accuracy</td>
                </tr>
                <tr>
                  <td><strong>CON</strong> Constitution</td>
                  <td>+15 max health, +1 armour, +0.1 out-of-combat regen per second, +0.05% block strength</td>
                </tr>
                <tr>
                  <td><strong>LCK</strong> Luck</td>
                  <td>+0.05% forge success, +0.1% loot luck</td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            Completing a region adds another +1% loot luck on top, permanently.
          </p>

          <h3 id="damage">How a hit is resolved</h3>
          <ul class="styled-list">
            <li><strong>Armour is subtracted, not ignored.</strong> A monster's armour is taken off every hit before it lands, and it grows steeply across the five regions — which is why a weapon upgrade matters more than a level.</li>
            <li><strong>Lifesteal is capped at 1% of your maximum health per hit.</strong> Uncapped, a fast weapon with a lifesteal roll made a character unkillable; the cap keeps it sustain rather than immunity.</li>
            <li><strong>Bosses roll loot twice</strong> and carry five times their listed health the first time you meet them.</li>
          </ul>

          <h3 id="autoeat">Auto-eat and the larder</h3>
          <p class="dim small">
            The larder holds three food slots and eats from them automatically when
            your health falls below the threshold you set. Anything with
            <code>_food</code> in its id counts, and so does every one of the ten
            raw fish — <strong>there is no cooking step</strong>, so a fish is the
            meal. Running out does not stop combat, but it removes the only healing
            you have, and the game says so.
          </p>

          <h3 id="slots">The eleven equipment slots</h3>
          <p class="dim small">
            Eight combat slots and three tool slots. The tools are equipment in
            exactly the same sense as a sword: they carry a rarity and roll their
            own affixes, and each character wears its own.
          </p>
          <div class="slot-grid">
            {#each EQUIPMENT_SLOTS as slot (slot.index)}
              <div class="slot" class:tool={slot.index >= 8}>
                <span class="slot-n">{slot.index}</span>
                <span>{slot.label}</span>
              </div>
            {/each}
          </div>
          <p class="dim tiny">
            There is no offhand and no shield slot. Amulets and rings are real and
            always were — one of each per tier has been in the catalogue from the
            start.
          </p>

          <h3 id="sets">Armour set bonuses</h3>
          <p class="dim small">
            Wearing several pieces of one family pays a bonus at
            <strong>2, 3 and 5 pieces</strong>. The families in the catalogue:
          </p>
          <div class="badge-list">
            {#each armourFamilies as fam (fam)}
              <span class="fam-badge">{fam}</span>
            {/each}
          </div>

          <h3 id="ageing">Ageing</h3>
          <p class="dim small">
            A character in one of your played slots ages as it ticks:
            {AGE_PHASES.join(' → ')}. Roughly an hour of ticked play makes a Child
            an Adult. <strong>A character not in a played slot does not age at
            all</strong>, which is the single most surprising rule in the game —
            a newborn stays a Child forever until you field it from the Hall of
            Ancestors.
          </p>

        <!-- ================================================== SKILLS -->
        {:else if activeTab === 'skills'}
          <p>
            One skill point a level, plus {SKILL_POINTS_PER_SEAL} more every season
            for each Seal you hold. The tree is three rings deep.
          </p>

          <h3 id="rings">Roots, boughs and crowns</h3>
          <div class="scroll">
            <table>
              <thead>
                <tr><th>Ring</th><th class="num">Max level</th><th class="num">Price</th><th>Opens when</th></tr>
              </thead>
              <tbody>
                <tr>
                  <td><strong>Root</strong> — five of them</td>
                  <td class="num">{SKILL_TREE_ROOT_MAX}</td>
                  <td class="num">1, then 2 from level 5</td>
                  <td>Always open</td>
                </tr>
                <tr>
                  <td><strong>Bough</strong> — two per root</td>
                  <td class="num">{SKILL_TREE_BOUGH_MAX}</td>
                  <td class="num">{SKILL_TREE_BOUGH_COST} a level</td>
                  <td>Its root at level {SKILL_TREE_BOUGH_NEEDS_ROOT}</td>
                </tr>
                <tr>
                  <td><strong>Crown</strong> — one per root</td>
                  <td class="num">1</td>
                  <td class="num">{SKILL_TREE_CROWN_COST}</td>
                  <td>One of its boughs at level {SKILL_TREE_CROWN_NEEDS_BOUGH}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            The two boughs under a root are <strong>exclusive</strong>: taking one
            closes the other for the season. That is the only choice in the tree
            you can permanently get wrong, and it is why the respec exists.
          </p>

          <h3 id="nodes">Every node</h3>
          {#each rootNodes as root (root.id)}
            <div class="limb">
              <div class="limb-head">
                <strong>{root.name}</strong>
                <span class="dim tiny">{root.blurb}</span>
                <span class="rate">
                  {root.perLevel}{root.unit === 'pct' ? '%' : ''} a level
                </span>
              </div>
              <ul class="styled-list">
                {#each SKILL_TREE_NODES.filter((n) => n.root === root.id && n.ring !== 'root') as node (node.id)}
                  <li>
                    <strong>{node.name}</strong>
                    <span class="dim tiny ring">{node.ring}</span>
                    — {node.blurb}
                    {#if node.perLevel > 0}
                      <span class="rate">
                        {node.perLevel}{node.unit === 'pct' ? '%' : ''} a level
                      </span>
                    {/if}
                  </li>
                {/each}
              </ul>
            </div>
          {/each}
          <p class="dim tiny">
            Spend the points on the <a href="#/progression">Progression screen</a>.
          </p>

        <!-- ================================================== ITEMS -->
        {:else if activeTab === 'items'}
          <p>
            Every equipment piece carries a <strong>rarity tier</strong> from 1 to
            {MAX_QUALITY_TIER}. The tier decides how many affixes the piece
            carries; each affix's own rarity decides how big it is. Two different
            axes, and confusing them is the commonest mistake about this game's
            loot.
          </p>

          <h3 id="rarity">The fourteen tiers</h3>
          <div class="tier-grid">
            {#each RARITY_TIER_NAMES.slice(1) as name, index (name)}
              <span class="t-badge" style="border-color: {rarityColor(index + 1)}; color: {rarityColor(index + 1)}">
                T{index + 1} {name}
              </span>
            {/each}
          </div>

          <div class="scroll narrow-top">
            <table>
              <thead>
                <tr><th>Rarity</th><th class="num">Affixes it carries</th></tr>
              </thead>
              <tbody>
                {#each AFFIX_COUNT_BANDS as band (band.tiers)}
                  <tr><td>{band.tiers}</td><td class="num">{band.count}</td></tr>
                {/each}
              </tbody>
            </table>
          </div>

          <h3 id="droprates">Drop chances and luck</h3>
          <WikiDropChances />

          <h3 id="namespaces">Three kinds of material share one shelf</h3>
          <p class="dim small">
            Worth knowing, because it explains a stack you cannot spend. Three
            different families of id sit in the same inventory:
          </p>
          <ul class="styled-list">
            <li><strong>Catalogued items</strong> — everything with a picture, a region tier and a gold value. Logs, ores, fish, gear, tools. This is the real economy.</li>
            <li><strong>Gathering slugs</strong> — a handful of older ids like <code>wood</code> that have no catalogue entry at all. Nothing keyed on an item id will accept them.</li>
            <li><strong>Legacy crafting materials</strong> — fifty ids ending <code>_crafting_material</code>. None of them can be obtained or spent any more; if you are holding thousands of Copper Ore Crafting Material, that is why. They are kept rather than deleted because they are yours.</li>
          </ul>

          <h3 id="database">Item database</h3>
          <p class="dim small">Every item in the catalogue, searchable.</p>
          <WikiItemDatabase {registry} />

        <!-- ================================================== FORGE -->
        {:else if activeTab === 'forge'}
          <p>
            The Forge does two different things to an item you already own: it
            raises the item's rarity by consuming two others, and it rerolls the
            affixes on it. They cost different currencies and answer different
            problems.
          </p>

          <h3 id="fusion">Fusion</h3>
          <ul class="styled-list">
            <li>A target plus <strong>two sacrifices</strong> becomes the target one rarity tier higher.</li>
            <li><strong>Your Forge building's level is the ceiling.</strong> A level 5 Forge cannot produce anything above rarity 5, and this is the reason most refused fusions are refused.</li>
            <li>Nothing currently equipped — on any of your characters — may be a target or a sacrifice.</li>
            <li>Rarity {MAX_QUALITY_TIER} is the top. An item already there is refused for that reason, not for the Forge level.</li>
          </ul>

          <h3 id="reroll">Affix rerolls, and what they cost</h3>
          <p class="dim small">
            A reroll is <strong>flat, per region</strong>. It does not escalate
            with repeated attempts and it does not depend on the item's rarity —
            only on the region the item comes from.
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr>
                  <th>Region</th>
                  <th class="num">Gold a reroll</th>
                  <th class="num">Average kill in that region</th>
                  <th class="num">Kills to pay for it</th>
                </tr>
              </thead>
              <tbody>
                {#each LOCATION_NAMES as name, index (name)}
                  <tr>
                    <td>{index + 1}. {name}</td>
                    <td class="num">{REROLL_GOLD_BY_REGION[index + 1].toLocaleString()}</td>
                    <td class="num">{Math.round(regionGold[index] ?? 0).toLocaleString()}g</td>
                    <td class="num">{rerollInKills[index] ?? '—'}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            Worked example: a reroll on a Scorched Wasteland item costs
            {REROLL_GOLD_BY_REGION[3].toLocaleString()} gold, which is about
            {rerollInKills[2] ?? '—'} kills of an average Scorched Wasteland
            regular — a few minutes. The chase for a Legendary affix is measured in
            hundreds of attempts, which is exactly why the price is flat: an
            escalating one made its own headline outcome arithmetically
            unreachable.
          </p>
          <p class="dim tiny">
            An affix can be <strong>locked</strong>, and a locked item cannot be
            fused — the Forge will refuse it and the screen says which gate stopped
            it.
          </p>

          <h3 id="affixrarity">Affix rarity</h3>
          <p class="dim small">
            Every affix rolls one of five rarities of its own, independently. The
            magnitude multiplier is 1.6 per step, so a Legendary roll is 6.55× a
            Common one — a Rare item whose two affixes both came up Legendary can
            compete with a Godly item.
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr>
                  <th>Affix rarity</th>
                  <th class="num">Chance on a fresh roll</th>
                  <th class="num">Magnitude</th>
                  <th class="num">Diamonds to step up</th>
                </tr>
              </thead>
              <tbody>
                {#each AFFIX_RARITIES as row (row.rarity)}
                  <tr>
                    <td>{row.name}</td>
                    <td class="num">{(row.weightPerMille / 10).toFixed(1)}%</td>
                    <td class="num">{row.multiplier.toFixed(2)}×</td>
                    <td class="num">{row.upgradeDiamonds > 0 ? row.upgradeDiamonds : '—'}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            A five-affix item averages roughly one Legendary per thirteen items, so
            rerolling — not the drop table — is the realistic path to a full
            Legendary set.
          </p>

          <h3 id="affixpool">Which affixes roll where</h3>
          <p class="dim small">
            An affix only rolls on a slot it is legal for. When a slot's legal pool
            runs out — a chest piece has only two legal affixes — the roll
            <strong>stacks</strong> another instance of one it already has instead
            of leaving the item short.
          </p>
          <div class="scroll">
            <table>
              <thead><tr><th>Affix</th><th>Legal slots</th></tr></thead>
              <tbody>
                {#each AFFIX_POOL as affix (affix.id)}
                  <tr><td>{affix.label}</td><td class="dim">{affix.slots}</td></tr>
                {/each}
              </tbody>
            </table>
          </div>

        <!-- ================================================== MAP -->
        {:else if activeTab === 'map'}
          <h3 id="unlock">Unlocking a region</h3>
          <p class="dim small">
            <strong>One number answers two questions.</strong> Beating a region's
            boss opens the next region <em>and</em> permits you to wear that
            region's gear. There is no separate level requirement on equipment —
            once a region is open, everything that drops there is wearable at every
            rarity. The unlock is consecutive: killing a later boss out of order
            does not skip the ones before it.
          </p>
          <p class="dim tiny">
            Gear above region 5 exists — it drops from the ninety non-canonical
            monsters — and needs <strong>every one of the five bosses down</strong>
            before it can be worn.
          </p>

          <h3 id="races">Races, and which boss unlocks them</h3>
          <div class="badge-list">
            {#each ALL_RACE_IDS as raceId (raceId)}
              <span class="fam-badge icon">
                <RaceIcon {raceId} size="sm" />
                {RACE_NAMES[raceId]}
              </span>
            {/each}
          </div>
          <div class="scroll">
            <table>
              <thead><tr><th>Region</th><th>Boss</th><th>Unlocks</th></tr></thead>
              <tbody>
                {#each REGION_BOSS_RACE as row (row.region)}
                  <tr>
                    <td>{row.region}. {locationName(row.region)}</td>
                    <td>{row.boss}</td>
                    <td>{row.race}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            Human needs no unlock. A locked race never appears among the newcomers
            at the Inn, because breeding refuses a mixed-race pair and a newcomer
            of a race you own nobody of would be a portrait and nothing else.
          </p>

          <h3 id="regions">The five regions</h3>
          <p class="dim small">
            Five monsters each — four regulars and a boss. The ladder is one
            continuous curve across all twenty-five, so a region border is never a
            step down.
          </p>

          <h3 id="monsters">Every monster and its drops</h3>
          {#each registry.regions as region, index (index)}
            <h4>{index + 1}. {locationName(index + 1)}</h4>
            <div class="monster-grid">
              {#each region as monster (monster.Id)}
                <div class="monster-card">
                  <div class="monster-header">
                    <MonsterPortrait monsterId={monster.Id} name={monster.Name} size="sm" />
                    <div class="monster-info">
                      <strong>{monster.Name}</strong>
                      <span class="dim tiny">
                        {monster.MaxHp.toLocaleString()} hp · {monster.AttackPower} dmg ·
                        {monster.Armor} armour · {monster.DodgeRating} dodge
                      </span>
                      <span class="dim tiny">
                        {monster.BaseGoldReward.toLocaleString()}g · {monster.BaseXpReward.toLocaleString()} xp
                      </span>
                    </div>
                  </div>
                  <WikiMonsterDrops monsterId={monster.Id} />
                </div>
              {/each}
            </div>
          {/each}

        <!-- ================================================== GATHERING -->
        {:else if activeTab === 'gathering'}
          <p>
            Not everything is solved with a sword. Logs and ore build the village;
            fish are the only healing you have.
          </p>

          <h3 id="professions">The professions</h3>
          <ul class="styled-list">
            <li><strong>Woodcutting</strong> — logs, for the village, tools and the guild depot.</li>
            <li><strong>Mining</strong> — ore, for the same. One common and one rare ore per region.</li>
            <li><strong>Fishing</strong> — the ten fish, which go straight into the larder uncooked.</li>
          </ul>
          <p class="dim tiny">
            Each has its own mastery track, levelled by working its nodes.
          </p>

          <h3 id="tools">Tools are equipment</h3>
          <p class="dim small">
            An axe, a pickaxe and a rod, worn in slots 8, 9 and 10 — a character
            carries all three at once and each accelerates its own profession. They
            are proper equipment: they roll a rarity and up to five affixes, out of
            a pool of three that only tools can have (gathering speed, gathering
            yield, and rare find).
          </p>
          <p class="dim small">
            Ten tiers, two per region band, on a geometric 1.35× curve. The
            within-band upgrade is the one you feel: whichever band you are in, the
            second tool is about a third faster than the first.
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr><th class="num">Tier</th><th>Wood</th><th>Band</th><th class="num">Speed bonus</th></tr>
              </thead>
              <tbody>
                {#each TOOL_TIERS as tool (tool.tier)}
                  <tr>
                    <td class="num">{tool.tier}</td>
                    <td>{tool.wood}</td>
                    <td class="dim">{tool.band}</td>
                    <td class="num">+{tool.speedPct}%</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            The three starter tools (Normal axe, pickaxe and rod) are tier 0 and
            grant nothing. All thirty tiered tools are crafted, not dropped.
          </p>

          <h3 id="speed">What makes gathering faster</h3>
          <ul class="styled-list">
            <li><strong>Mastery</strong> — +{MASTERY_SPEED_PCT_PER_LEVEL}% a level in that profession, compounding with everything below.</li>
            <li><strong>Your tool's tier</strong> — the table above.</li>
            <li><strong>Your tool's affixes</strong> — gathering speed rolls on tools and nothing else.</li>
            <li><strong>The village</strong> — the Lumberjack speeds woodcutting and the Mine speeds mining, +{VILLAGE_SPEED_PCT_PER_LEVEL}% a level each.</li>
          </ul>
          <p class="dim tiny">
            Nothing can take a node below {MIN_GATHER_TICKS} ticks — a fifth of a
            second — per unit.
          </p>

          <h3 id="nodes">Node list</h3>
          {#each nodesByProfession as [profession, nodes] (profession)}
            <h4>{professionName(profession)}</h4>
            <div class="node-grid">
              {#each nodes as node (node.ActivityId)}
                <div class="node-card">
                  <strong>{locationName(nodeLocation(node.ActivityId))}</strong>
                  <span class="dim tiny">{node.BaseTickThreshold} ticks a unit, bare</span>
                  <span class="dim tiny">{node.BaseMasteryXpReward} mastery xp</span>
                </div>
              {/each}
            </div>
          {/each}

        <!-- ================================================== CRAFTING -->
        {:else if activeTab === 'crafting'}
          <h3 id="bench">How crafting works</h3>
          <ul class="styled-list">
            <li>Materials come out of your chest and the village stash together — you do not have to move anything first.</li>
            <li>A craft is an activity like any other: it occupies the character and runs on the tick, including while you are away.</li>
            <li>You can queue up to <strong>ten at once</strong>.</li>
            <li><strong>Crafted equipment always comes out Normal.</strong> A craft makes the base object; rarity is raised afterwards at the Forge. It still rolls one affix, because even Normal gets one.</li>
          </ul>
          <p class="dim tiny">
            The recipe list below is read from the server as you open this page, so
            it is exactly what the bench will accept.
          </p>

          <h3 id="recipes">Every recipe</h3>
          <WikiRecipes />

        <!-- ================================================== VILLAGE -->
        {:else if activeTab === 'village'}
          <p>
            The village is the half of the game that pays out while you are not
            playing, and the Town Hall gates all of it. Every upgrade costs logs
            and ore; everything except the two structural buildings also costs
            gold.
          </p>
          <p class="dim small">
            Slots for a second and third character come from the Town Hall at
            level {SLOT2_TOWN_HALL} and level {SLOT3_TOWN_HALL} — not from your
            character level.
          </p>
          <WikiVillage />

        <!-- ================================================== BREEDING -->
        {:else if activeTab === 'breeding'}
          <p>
            Breeding is why a season ends rather than stops. Levels and gear are
            wiped at the rollover; a bloodline is not.
          </p>

          <h3 id="words">The words</h3>
          <div class="scroll">
            <table>
              <thead><tr><th>Word</th><th>What it means</th></tr></thead>
              <tbody>
                <tr><td><strong>aptitude</strong></td><td>One of the four bred numbers: Strength, Skill, Endurance, Fortune.</td></tr>
                <tr><td><strong>bloodline</strong></td><td>The four aptitudes your line carries, collectively.</td></tr>
                <tr><td><strong>gene</strong></td><td>One of the four dominant/recessive pairs: Race, Speed, Crit, Yield.</td></tr>
                <tr><td><strong>copy</strong></td><td>One half of a gene — the dominant copy and the recessive copy.</td></tr>
                <tr><td><strong>ancestor</strong></td><td>Anyone on the Hall of Ancestors roster.</td></tr>
                <tr><td><strong>newcomer</strong></td><td>Somebody who has arrived at the Inn and has not married.</td></tr>
                <tr><td><strong>elder</strong></td><td>A newcomer who has married into your line. They never marry again.</td></tr>
                <tr><td><strong>the gene pool</strong></td><td>Newcomers plus elders, as a resource. Not "the village" — that is the buildings.</td></tr>
                <tr><td><strong>fielding</strong></td><td>Putting an ancestor into one of your three played character slots.</td></tr>
                <tr><td><strong>the cull</strong></td><td>The end-of-season deletion down to the Hall's cap. Who survives it <em>carries</em>.</td></tr>
                <tr><td><strong>Inheritance</strong></td><td>Capitalised, this is the diamond shop of permanent account bonuses — and nothing else. What a child gets from its parents, it <em>inherits</em>.</td></tr>
              </tbody>
            </table>
          </div>

          <h3 id="requirements">What you need</h3>
          <ul class="styled-list">
            <li>A <strong>Breeding Grounds</strong> at level 1 or better.</li>
            <li>A hero who is <strong>level 50 and an Adult</strong>. Both, not either.</li>
            <li><strong>500 gold × (highest parent generation + 1)</strong>. A founder costs 500; a generation-3 parent costs 2,000.</li>
            <li>No breeding cooldown, and not locked in a market trade.</li>
          </ul>
          <p class="dim tiny">
            Both parents rest for one hour afterwards. There is no gestation — the
            child exists the instant the pairing is confirmed.
          </p>

          <h3 id="pairings">The two pairings</h3>
          <div class="two-col">
            <div class="card">
              <strong>Hero × newcomer — the standard pair</strong>
              <p class="dim small">
                Only the hero needs level 50 and adulthood. The newcomer only has to
                be of the opposite sex and the same race. This pairing is
                <strong>never inbred</strong> — a newcomer has no parents in this
                world. They marry exactly once and become an elder.
              </p>
            </div>
            <div class="card">
              <strong>Hero × hero — crossing your own</strong>
              <p class="dim small">
                Both parents need level 50 and adulthood. It <em>can</em> be inbred:
                sharing a parent, or one being the other's parent. That is allowed
                but degraded — drift inverts to 10% up and 25% down, epic mutation
                falls from 5% to 1%, and the Speed, Crit and Yield genes each lose
                a quarter of both copies.
              </p>
            </div>
          </div>

          <h3 id="inherits">What a child inherits</h3>
          <p class="dim small">For each aptitude, independently:</p>
          <ol class="steps">
            <li><strong>One parent's exact value is copied</strong>, weighted by who is stronger in it — a parent at 12 against a parent at 4 gives a 75% chance of the 12.</li>
            <li><strong>A drift roll</strong>: 25% +1, 10% −1, 65% unchanged.</li>
            <li><strong>An epic mutation</strong>, 5% of the time, adds +1 to all four and marks the child.</li>
            <li>Clamped to 0…{APTITUDE_MAX}.</li>
          </ol>
          <p class="dim small">
            <strong>The consequence that is the whole design:</strong> each aptitude
            independently favours whichever parent is better at it. Cross a fighter
            (12,4,4,4) with a gatherer (4,12,4,4) and the child comes out around
            (12,12,4,4) — good at both. You do not want two similar parents. You
            want two different ones.
          </p>
          <p class="dim small">
            <strong>The consequence that makes the village necessary:</strong> a child
            copies a value that already exists in the pair. Drift and the epic roll
            together average about +0.15 per aptitude per generation, so crossing
            your own converges on what you already have. Outside blood is the only
            thing that puts a new number into a bloodline.
          </p>

          <div class="scroll">
            <table>
              <thead>
                <tr><th>Aptitude</th><th>What it does</th><th class="num">At 20</th><th class="num">At {APTITUDE_MAX}</th></tr>
              </thead>
              <tbody>
                {#each APTITUDES as apt (apt.field)}
                  <tr>
                    <td><strong>{apt.name}</strong></td>
                    <td class="dim">{apt.blurb}</td>
                    <td class="num">+{aptitudeBonusPercent(APTITUDE_VILLAGE_CEILING).toFixed(1)}%</td>
                    <td class="num">+{aptitudeBonusPercent(APTITUDE_MAX).toFixed(1)}%</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            An aptitude point is worth 1.5% up to 20, 0.7% from 21 to 35 and 0.3%
            from 36 to {APTITUDE_MAX}. Deliberately diminishing, so a veteran's
            advantage is visible but never decisive on a shared leaderboard.
          </p>

          <h4>The four genes</h4>
          <p class="dim small">
            Race, Speed, Crit and Yield, each a dominant and a recessive copy. Each
            parent passes one of its two at random; the higher of the two received
            becomes the child's dominant. Speed and Crit feed attack speed and crit
            chance, Yield adds +4% gathering yield per point, and a pair whose Race
            dominants differ cannot breed at all. Genes are a slow curiosity;
            aptitudes are the axis a season leaves standing.
          </p>

          <h3 id="genepool">The Inn and the gene pool</h3>
          <p class="dim small">
            A newcomer contributes one race, one sex and four aptitudes — nothing
            else. Their aptitudes roll <strong>2 + up to the Inn's level</strong>,
            capped at {APTITUDE_VILLAGE_CEILING}. That is the whole two-phase climb:
            0 → {APTITUDE_VILLAGE_CEILING} is village-driven, and above
            {APTITUDE_VILLAGE_CEILING} only drift and selection across seasons can
            reach.
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr><th class="num">Inn level</th><th>Somebody arrives every</th><th class="num">Village holds</th><th>Aptitudes roll</th></tr>
              </thead>
              <tbody>
                <tr><td class="num">0</td><td>48h</td><td class="num">6</td><td>2</td></tr>
                <tr><td class="num">1</td><td>46h</td><td class="num">7</td><td>2–3</td></tr>
                <tr><td class="num">5</td><td>38h</td><td class="num">11</td><td>2–7</td></tr>
                <tr><td class="num">12+</td><td>24h (the floor)</td><td class="num">16 (the ceiling)</td><td>2–20 (the ceiling)</td></tr>
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            <strong>A full village stops the clock entirely.</strong> Nothing is
            banked against a slot freeing up later, so a mediocre newcomer sitting
            in the last slot is costing you the arrival you would otherwise have
            had. Sending them on is a real move. A feast buys an arrival now for
            2,500 × 1.6ⁿ gold, where n is how many you have thrown this season.
          </p>

        <!-- ================================================== LONG GAME -->
        {:else if activeTab === 'longgame'}
          <p>
            A season ends and takes almost everything with it. What it leaves is
            the point of the whole system, and it is decided in advance by what you
            built and who you marked — the rollover runs server-side with everyone
            disconnected, so nothing prompts you.
          </p>

          <h3 id="season">What a season resets</h3>
          <div class="two-col">
            <div class="card good">
              <strong>Carries</strong>
              <ul class="styled-list">
                {#each SEASON_CARRIES as line (line)}<li>{line}</li>{/each}
              </ul>
            </div>
            <div class="card bad">
              <strong>Is lost</strong>
              <ul class="styled-list">
                {#each SEASON_LOST as line (line)}<li>{line}</li>{/each}
              </ul>
            </div>
          </div>
          <p class="dim tiny">
            The rollover sets every surviving ancestor to level 1 and Adult, so the
            whole roster is breeding-age on day one — and nobody can actually breed
            until somebody is back at level 50.
          </p>

          <h3 id="deeds">The Book of Deeds, and Seals</h3>
          <p class="dim small">
            Five chapters of six deeds. Finishing a chapter awards a
            <strong>Seal</strong>, and a Seal is worth
            <strong>+{SKILL_POINTS_PER_SEAL} skill points every season, forever</strong>
            — a second source of points, earned by exploring the game rather than by
            levelling. Five Seals is +{SKILL_POINTS_PER_SEAL * 5} against a base of
            about a hundred. There is no claim button: a Seal is awarded the moment
            the server sees the chapter complete.
          </p>
          {#each DEED_CHAPTERS as chapter (chapter.index)}
            <div class="chapter">
              <div class="limb-head">
                <strong>{chapter.index}. {chapter.title}</strong>
                <span class="rate">{chapter.reward}</span>
              </div>
              <p class="dim small">{chapter.about}</p>
            </div>
          {/each}
          <p class="dim tiny">
            The live counters are on the <a href="#/progression">Progression screen</a>.
            A chapter opens when the one before it completes.
          </p>

          <h3 id="hall">The Hall of Ancestors, and the cull</h3>
          <p class="dim small">
            Everyone you have bred lives on the Hall's roster. It holds
            <strong>{HALL_BASE_SLOTS}</strong>, plus one per diamond slot bought,
            hard cap <strong>{HALL_MAX_SLOTS}</strong>. Slots cost
            {HALL_SLOT_COSTS.map((c) => c.toLocaleString()).join(' / ')} diamonds.
          </p>
          <p class="dim small">
            The Hall is also where you <strong>field</strong> a character into one
            of your played slots. This matters more than it sounds: only fielded
            characters age, so a newborn stays a Child forever until you field it.
          </p>
          <p class="dim small">
            If you hold more than the cap when the season turns, the surplus is
            <strong>deleted</strong>. Who survives, in order:
          </p>
          <ol class="steps">
            {#each CULL_ORDER as line (line)}<li>{line}</li>{/each}
          </ol>
          <p class="dim tiny">
            Marking more than the cap is legal — the same ranking resolves it. The
            Hall screen shows who would go if the season ended right now, faded.
          </p>

          <h3 id="inheritance">Inheritance</h3>
          <p class="dim small">
            Six permanent account bonuses bought with diamonds. Every level is
            +{INHERITANCE_PCT_PER_LEVEL} percentage points, every stat caps at
            {INHERITANCE_MAX_LEVEL} levels (+{INHERITANCE_MAX_LEVEL * INHERITANCE_PCT_PER_LEVEL}%),
            and the price climbs 28% a level from 40. So the only decision is how
            <em>wide</em> to go against how <em>deep</em>.
          </p>
          <div class="scroll">
            <table>
              <thead><tr><th>Bonus</th><th>What it moves</th></tr></thead>
              <tbody>
                {#each INHERITANCE_STATS as stat (stat.id)}
                  <tr><td><strong>{stat.name}</strong></td><td class="dim">{stat.blurb}</td></tr>
                {/each}
              </tbody>
            </table>
          </div>
          <div class="scroll">
            <table>
              <thead>
                <tr><th class="num">Level</th><th class="num">Diamonds for it</th><th class="num">Total spent</th><th class="num">Bonus</th></tr>
              </thead>
              <tbody>
                {#each [1, 5, 10, 15, 20] as level (level)}
                  <tr>
                    <td class="num">{level}</td>
                    <td class="num">{inheritanceUpgradeCost(level - 1).toLocaleString()}</td>
                    <td class="num">
                      {Array.from({ length: level }, (_, i) => inheritanceUpgradeCost(i))
                        .reduce((a, b) => a + b, 0)
                        .toLocaleString()}
                    </td>
                    <td class="num">+{level * INHERITANCE_PCT_PER_LEVEL}%</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            A full stat runs to roughly 25,000 diamonds. That is the sink the
            premium currency exists for, and it is why the Hall slots — four of
            them for {HALL_SLOT_COSTS.reduce((a, b) => a + b, 0).toLocaleString()} —
            are a real competing choice rather than an obvious one.
          </p>

          <h3 id="leaderboard">Season rank</h3>
          <p class="dim small">
            The seasonal leaderboard ranks by level, then by the hardest monster you
            ever put down. Your best finish is one of the few things that carries,
            and a top-fifty finish is one of the six deeds in the last chapter.
          </p>

        <!-- ================================================== GUILDS -->
        {:else if activeTab === 'guilds'}
          <p>
            A guild is a shared depot and four shared buffs. It is the only place in
            the game where somebody else's gathering makes you stronger.
          </p>

          <h3 id="roles">Roles</h3>
          <ul class="styled-list">
            <li><strong>Leader</strong> — invites, kicks, promotes, demotes, sets the tax, activates buffs.</li>
            <li><strong>Officer</strong> — invites, kicks ordinary members, activates buffs, reviews applications.</li>
            <li><strong>Member</strong> — donates and benefits. Cannot activate a buff.</li>
          </ul>
          <p class="dim tiny">
            Joining is by name or by application, depending on how the guild is set.
            There is no invite-and-accept flow.
          </p>

          <h3 id="depot">The depot</h3>
          <p class="dim small">
            Members donate logs and ore into a shared depot. Donations raise the
            guild's tier and your own standing on the roster; the depot is what
            buffs are paid out of.
          </p>

          <h3 id="buffs">Guild buffs</h3>
          <WikiGuildBuffs />

          <h3 id="tax">The guild tax</h3>
          <p class="dim small">
            A guild takes a cut of every market sale one of its members makes,
            between {GUILD_TAX_MIN_PCT}% and {GUILD_TAX_MAX_PCT}%, set by the
            leader. It goes into the guild's treasury and comes off the seller's
            proceeds <em>on top of</em> the market's own fee.
          </p>

          <h3 id="chat">Chat</h3>
          <ul class="styled-list">
            <li><strong>World</strong> — everybody online.</li>
            <li><strong>Guild</strong> — your guild only.</li>
            <li><strong>Private</strong> — one conversation with one player, kept as a thread.</li>
          </ul>

        <!-- ================================================== ECONOMY -->
        {:else if activeTab === 'economy'}
          <h3 id="market">The market</h3>
          <p class="dim small">
            Player-to-player trading in equipment. A listing holds the item in
            escrow — it leaves your chest the moment you list it, so it cannot be
            equipped, fused or sold twice.
          </p>
          <ul class="styled-list">
            <li><strong>Buyers are region-gated.</strong> You cannot buy gear from a region you have not opened, for exactly the same reason you cannot wear it.</li>
            <li><strong>The seller pays a fee that scales with their own wealth</strong> — a gold sink aimed at the accounts that have too much of it.</li>
            <li>If the buyer's chest is full the item goes to their mailbox instead.</li>
          </ul>
          <div class="scroll">
            <table>
              <thead><tr><th>Seller is holding</th><th class="num">Fee on the sale</th></tr></thead>
              <tbody>
                {#each MARKET_FEE_BRACKETS as bracket (bracket.wealth)}
                  <tr><td>{bracket.wealth}</td><td class="num">{bracket.feePct}%</td></tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            The guild tax comes off on top of this, so a wealthy seller in a
            20% guild keeps 65% of the sale price.
          </p>

          <h3 id="mailbox">The mailbox</h3>
          <p class="dim small">
            Where things arrive that had nowhere else to go: world boss rewards,
            market goods that would not fit in a full chest, and anything an
            administrator sends. Items sit unclaimed until you claim them.
          </p>
          <p class="dim tiny">
            <strong>Keep it under {WORLD_BOSS_MAILBOX_LIMIT} items.</strong> A world
            boss reward is skipped outright for anybody whose mailbox is at that
            limit when the boss dies — it is not queued, it is lost.
          </p>

          <h3 id="diamonds">Diamonds</h3>
          <p class="dim small">
            Earned from the day-7 login bonus ({DAILY_LOGIN_DAY7_DIAMONDS} at a
            time), from achievements, or bought. Three sinks, all permanent:
          </p>
          <ul class="styled-list">
            <li><strong>Inheritance levels</strong> — the six permanent percentage bonuses. Roughly 25,000 for a full stat.</li>
            <li><strong>Hall of Ancestors slots</strong> — four of them, {HALL_SLOT_COSTS.reduce((a, b) => a + b, 0).toLocaleString()} in total, taking the roster from {HALL_BASE_SLOTS} to {HALL_MAX_SLOTS}.</li>
            <li><strong>Affix rarity upgrades</strong> — one step, on one affix, from 5 diamonds at the bottom to 196 at the top.</li>
          </ul>

        <!-- ================================================== EVENTS -->
        {:else if activeTab === 'events'}
          <h3 id="worldboss">The world boss</h3>
          <p class="dim small">
            A server-wide encounter with a single shared health bar —
            {WORLD_BOSS_HP.toLocaleString()} to start — that everybody online chips
            at together. It is the one place your progress is visible to strangers
            in real time.
          </p>
          <ul class="styled-list">
            <li><strong>{WORLD_BOSS_ATTEMPTS} attempts per encounter</strong>, and each one is a choice.</li>
            <li>
              <strong>The boss wears {WORLD_BOSS_PLATES} armour plates and one of them is soft.</strong>
              Striking the soft one does {WORLD_BOSS_WEAK_MULTIPLIER}x damage. Striking any other does
              full damage and <strong>breaks</strong> that plate — for everyone, for the rest of the
              encounter.
            </li>
            <li>
              So the boss you arrive at is a message from everyone who came before you: read the
              plates before you swing. Which one is soft changes every encounter, so it cannot be
              looked up.
            </li>
            <li>
              A wrong guess is <strong>not punished</strong> — it does full damage and strips armour
              that narrows the search for whoever is next.
            </li>
            <li>Your hit is your character's real attack power, floored at {WORLD_BOSS_DAMAGE_FLOOR.toLocaleString()} — an account that has never fought still contributes something.</li>
            <li>
              <strong>You have {WORLD_BOSS_SESSION_MINUTES} minutes from your first strike</strong> to
              use the rest. After that your remaining attempts are gone until the next encounter.
            </li>
            <li><strong>An empty larder makes the server discard your attack without a word.</strong> Stock it before you swing.</li>
            <li>Rewards land in the mailbox when the boss dies, by your percentile among everybody who dealt damage.</li>
          </ul>
          <div class="scroll">
            <table>
              <thead><tr><th>Bracket</th><th class="num">Tokens</th><th class="num">Gold</th></tr></thead>
              <tbody>
                {#each WORLD_BOSS_REWARDS as row (row.bracket)}
                  <tr>
                    <td>{row.bracket}</td>
                    <td class="num">{row.tokens}</td>
                    <td class="num">{row.gold.toLocaleString()}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>

          <h3 id="daily">The daily bonus</h3>
          <p class="dim small">
            Paid <strong>on sign-in</strong>, not claimed — there is no button. The
            streak runs over seven days and then starts again; miss a day and it
            resets to day 1. Completing day 7 pays
            {DAILY_LOGIN_DAY7_DIAMONDS} diamonds on top of the gold.
          </p>
          <p class="dim small">
            A "day" is the <strong>UTC day</strong>, so everybody on earth crosses
            the boundary at the same second — midnight UTC. Signing in at 23:00 and
            again at 00:00 is two consecutive days; signing in twice in one UTC day
            pays once.
          </p>
          <p class="dim small">
            Three gold schedules rotate weekly, so the week decides which of these
            you are on:
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr>
                  <th>Week</th>
                  {#each [1, 2, 3, 4, 5, 6, 7] as day (day)}<th class="num">Day {day}</th>{/each}
                </tr>
              </thead>
              <tbody>
                {#each DAILY_LOGIN_MATRICES as matrix, index (index)}
                  <tr>
                    <td>{['A', 'B', 'C'][index]}</td>
                    {#each matrix as gold, day (day)}
                      <td class="num">{gold.toLocaleString()}</td>
                    {/each}
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>

          <h3 id="achievements">Achievements</h3>
          <p class="dim small">
            Four of them. Three run in tiers I–IV and pay out automatically as you
            cross each threshold; the oldest one is claimed by hand.
          </p>
          <div class="scroll">
            <table>
              <thead>
                <tr><th>Achievement</th><th>Counts</th><th>Thresholds</th><th>Pays</th></tr>
              </thead>
              <tbody>
                {#each ACHIEVEMENTS as row (row.name)}
                  <tr>
                    <td><strong>{row.name}</strong></td>
                    <td class="dim">{row.metric}</td>
                    <td class="dim">{row.thresholds}</td>
                    <td class="dim">{row.rewards}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="dim tiny">
            Logistics is the only one that pays a permanent stat as well as
            diamonds — up to +8% gathering speed, forever.
          </p>

        <!-- ================================================== SCREENS -->
        {:else if activeTab === 'screens'}
          <p>
            Every screen the game has, and where in this wiki it is written down —
            or why it needs no page. A test asserts this list against the screens
            that actually exist, so it cannot fall behind quietly.
          </p>
          <div id="ledger">
            <WikiScreenIndex onJump={(tab) => goTo(tab)} />
          </div>
        {/if}
      {/if}
    </main>
  </div>
</div>

<style>
  /* Modul: a CONTAINER query, not a media query. The wiki sits inside a panel
     grid, so "the viewport is wide" and "this column is wide" are different
     facts - and the one that decides whether a table crops is the second. Dense
     panels cropping at a narrow container width has been a real shipped bug
     here more than once.

     The container is the WRAPPER, not the layout: a container query styles the
     container's descendants and can never style the container itself, so
     hanging `container-type` on `.wiki-layout` would have made the rule below
     silently do nothing - which looks exactly like a working responsive layout
     right up until somebody narrows the column. */
  .wrap {
    container: wiki / inline-size;
  }

  .wiki-layout {
    display: flex;
    gap: 1.5rem;
    align-items: flex-start;
  }

  @container wiki (max-width: 52rem) {
    .wiki-layout {
      flex-direction: column;
    }

    .sidebar {
      position: static;
      flex: 1 1 auto;
      width: 100%;
    }
  }

  /* The backstop for browsers without container queries, and for a genuinely
     narrow device. */
  @media (max-width: 52rem) {
    .wiki-layout {
      flex-direction: column;
    }

    .sidebar {
      position: static;
      flex: 1 1 auto;
      width: 100%;
    }
  }

  .sidebar {
    flex: 0 0 15rem;
    position: sticky;
    top: 1rem;
    padding: 1.25rem 0.9rem;
    min-width: 0;
  }

  .sidebar h2 {
    margin: 0 0 0.75rem 0.4rem;
    font-size: 1.1rem;
  }

  .search {
    width: 100%;
    margin-bottom: 0.75rem;
  }

  .wiki-nav {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
  }

  .group {
    margin: 0.75rem 0 0.25rem 0.5rem;
    font-size: 0.68rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--text-dim);
    opacity: 0.75;
  }

  .group:first-child {
    margin-top: 0;
  }

  .tab-btn,
  .result {
    display: flex;
    flex-direction: column;
    gap: 0.1rem;
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-dim);
    text-align: left;
    padding: 0.45rem 0.7rem;
    border-radius: var(--radius, 8px);
    cursor: pointer;
    font-size: 0.9rem;
    width: 100%;
    min-width: 0;
  }

  .tab-btn:hover,
  .result:hover {
    background: rgba(128, 128, 128, 0.12);
    color: var(--text);
  }

  .tab-btn.active {
    background: rgba(128, 128, 128, 0.12);
    border-color: var(--border);
    color: var(--text);
  }

  .t-label,
  .r-title {
    color: inherit;
  }

  .tab-btn.active .t-label {
    font-weight: 600;
  }

  .results {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
  }

  .content {
    flex: 1;
    min-width: 0;
    padding: 1.5rem;
  }

  .head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.6rem;
    margin-bottom: 0.75rem;
  }

  .head h2 {
    margin: 0;
  }

  .content :global(h3) {
    margin: 2rem 0 0.75rem;
    color: var(--text);
    font-size: 1.1rem;
    scroll-margin-top: 1rem;
  }

  h4 {
    margin: 1.25rem 0 0.5rem;
    font-size: 0.95rem;
    color: var(--text);
  }

  p {
    margin: 0.6rem 0;
    line-height: 1.5;
  }

  code {
    font-size: 0.85em;
    background: rgba(128, 128, 128, 0.15);
    padding: 0 0.25rem;
    border-radius: 3px;
    overflow-wrap: anywhere;
  }

  .styled-list,
  .steps {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    padding-left: 1.2rem;
    margin: 0.5rem 0;
    color: var(--text-dim);
    font-size: 0.88rem;
    line-height: 1.45;
  }

  .styled-list strong,
  .steps strong {
    color: var(--text);
  }

  .stats-list {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr));
    gap: 1rem;
    background: rgba(0, 0, 0, 0.12);
    padding: 1rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
    margin: 0.5rem 0;
  }

  .stats-list dt {
    font-weight: 600;
    color: var(--text);
    margin-bottom: 0.2rem;
  }

  .stats-list dd {
    margin: 0;
    color: var(--text-dim);
    font-size: 0.83rem;
    line-height: 1.45;
  }

  /* Every table in the wiki scrolls inside its own box. A dense table is the
     one thing that cannot be made to wrap. */
  .scroll {
    overflow-x: auto;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    background: rgba(0, 0, 0, 0.12);
    margin: 0.5rem 0;
  }

  .scroll table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
    min-width: 22rem;
  }

  .scroll th {
    text-align: left;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--border);
    color: var(--text-dim);
    font-weight: 600;
    white-space: nowrap;
  }

  .scroll td {
    padding: 0.45rem 0.6rem;
    border-bottom: 1px solid rgba(128, 128, 128, 0.12);
    vertical-align: top;
    line-height: 1.4;
  }

  .scroll tbody tr:last-child td {
    border-bottom: none;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .narrow-top {
    margin-top: 0.75rem;
  }

  .tier-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(8rem, 1fr));
    gap: 0.4rem;
    margin-top: 0.5rem;
  }

  .t-badge {
    display: block;
    padding: 0.35rem;
    text-align: center;
    border-radius: var(--radius, 4px);
    font-size: 0.75rem;
    background: rgba(0, 0, 0, 0.2);
    border: 1px solid;
    font-family: monospace;
    overflow-wrap: anywhere;
  }

  .slot-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(7.5rem, 1fr));
    gap: 0.4rem;
    margin: 0.5rem 0;
  }

  .slot {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    padding: 0.35rem 0.5rem;
    font-size: 0.82rem;
    background: rgba(0, 0, 0, 0.12);
    min-width: 0;
  }

  .slot.tool {
    border-color: var(--brass, var(--border));
    color: var(--accent);
  }

  .slot-n {
    font-family: monospace;
    font-size: 0.7rem;
    color: var(--text-dim);
    border: 1px solid var(--border);
    border-radius: 3px;
    padding: 0 0.25rem;
  }

  .monster-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(14rem, 1fr));
    gap: 0.75rem;
  }

  .monster-card {
    display: flex;
    flex-direction: column;
    background: rgba(0, 0, 0, 0.12);
    padding: 0.6rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
    min-width: 0;
  }

  .monster-header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    min-width: 0;
  }

  .monster-info {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    min-width: 0;
  }

  .node-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr));
    gap: 0.5rem;
  }

  .node-card {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    background: rgba(0, 0, 0, 0.12);
    padding: 0.6rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
    min-width: 0;
  }

  .badge-list {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin: 0.5rem 0;
  }

  .fam-badge {
    padding: 0.2rem 0.65rem;
    border-radius: 999px;
    background: rgba(128, 128, 128, 0.12);
    border: 1px solid var(--border);
    font-size: 0.82rem;
    text-transform: capitalize;
  }

  .fam-badge.icon {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
  }

  .two-col {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 0.75rem;
    margin: 0.5rem 0;
  }

  .card {
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    padding: 0.75rem;
    background: rgba(0, 0, 0, 0.12);
    min-width: 0;
  }

  .card.good {
    border-color: var(--good);
  }
  .card.bad {
    border-color: var(--danger);
  }

  .limb,
  .chapter {
    border-left: 2px solid var(--border);
    padding-left: 0.75rem;
    margin: 0.75rem 0;
  }

  .limb-head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.5rem;
  }

  .rate {
    font-size: 0.72rem;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }

  .ring {
    text-transform: uppercase;
    letter-spacing: 0.05em;
    opacity: 0.7;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
</style>
