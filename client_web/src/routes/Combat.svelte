<script lang="ts">
  import BossChallenges from '../lib/ui/BossChallenges.svelte';
  import BossAscension from '../lib/ui/BossAscension.svelte';
  import { bossChallengeKeys, fetchBossChallenges, bossAscensionKeys, fetchBossAscension } from '../lib/net/cosmetics';
  import { createQuery } from '@tanstack/svelte-query';
  import { locationName } from '../lib/ui/locations';
  import {
    firstClearHpMultiplier,
    firstClearAttackMultiplier,
    describeBossGearRequirement,
    bossRegionOf,
    bossGearProgress,
    FIRST_CLEAR_HP_MULTIPLIERS,
    FIRST_CLEAR_ATTACK_MULTIPLIERS,
    FIRST_CANONICAL_MONSTER_ID,
    MONSTERS_PER_REGION,
  } from '../lib/ui/victories';
  import { AFFIX_RARITY_NAMES } from '../lib/ui/affixes';
  import { rarityName } from '../lib/ui/rarity';
  import { xpToNextLevel } from '../lib/ui/levelCurve';
  import { queryKeys, fetchWorn, fetchCombatProjection, type HuntingEstimate } from '../lib/net/rest';
  import { bestPick, estimateLine, killTimeText, safety } from '../lib/ui/huntingEstimate';
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import Money from '../lib/ui/Money.svelte';
  import { readPref, writePref, PREF_LAST_MONSTER, PREF_OPEN_REGIONS } from '../lib/net/prefs';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
  import { selectedCharacterSlot } from '../lib/stores/selectedCharacter';
  import { workersOf, describeJob, kindTakenBy, kindTakenMessage, slotBars, type Worker } from '../lib/ui/workers';
  import WorkerPicker from '../lib/ui/WorkerPicker.svelte';
  import { fetchBreedingRoster } from '../lib/net/rest';
  import { locationBackground } from '../lib/ui/sprites';
  import { onMount } from 'svelte';
  import { playerState, visualState, connectionStatus, damageEvents, pushLocalNotice, levelUpPulse } from '../lib/stores/game';
  import {
    loadContent,
    itemName,
    monsterName,
    prettifyBaseId,
    type ContentRegistry,
    type MonsterDefinition,
    type MonsterLootEntry,
  } from '../lib/net/content';
  import { authedGet } from '../lib/net/auth';
  import { HALT_REASONS } from '../lib/ui/slots';
  import { requestScreen } from '../lib/stores/navigation';
  import Bar from '../lib/ui/Bar.svelte';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import FloatingDamage from '../lib/ui/FloatingDamage.svelte';
  import HitSpark from '../lib/ui/HitSpark.svelte';
  import Burst from '../lib/ui/Burst.svelte';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import { spriteUrl } from '../lib/ui/spriteUrl';
  import SessionLoot from '../lib/ui/SessionLoot.svelte';
  import {
    combatLog,
    describeCombatLine,
    killPulse,
    CombatEventKind,
    CombatEventFlag,
  } from '../lib/stores/combatLog';

  // Task 55: which boss challenges are done. Refreshed every minute while the
  // screen is open, so one met at a kill ticks over without a reload.
  const challenges = createQuery(() => ({
    queryKey: bossChallengeKeys.all,
    queryFn: fetchBossChallenges,
    refetchInterval: 60_000,
  }));

  // Task 87: the Ascension ladder per boss. Any command result invalidates
  // every query (game.ts), so a cleared step refreshes the ladder by itself.
  const ascension = createQuery(() => ({
    queryKey: bossAscensionKeys.all,
    queryFn: fetchBossAscension,
    refetchInterval: 60_000,
  }));

  const snap = $derived($playerState);

  // Modul: THE PICKED CHARACTER FIGHTS (2026-10-08). This screen sent every
  // Fight for slot 1 and drew slot 1's bars, whoever was picked on the
  // Character screen - so picking slot 3 and pressing Fight pulled slot 1 off
  // its gathering node. The pick is shared now (selectedCharacterSlot), and
  // every bar below reads the picked character's slot. Slot 1 keeps the
  // interpolated values (visualState); 2 and 3 draw the raw snapshot.
  const rosterNames = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster, staleTime: 60_000 }));
  const nameById = $derived(new Map((rosterNames.data ?? []).map((c) => [c.CharacterId, c.Name])));
  const workers = $derived(workersOf(snap));
  const chosen = $derived<Worker | null>(workers.find((w) => w.slot === $selectedCharacterSlot) ?? workers[0] ?? null);
  const chosenSlot = $derived(chosen?.slot ?? 1);
  const bars = $derived(slotBars(snap, chosenSlot));
  const jobOf = (w: Worker) => describeJob(w.activity, w.halt, { monsterName: (id) => registry?.monsters.get(id)?.Name ?? 'a monster' });
  const shownPlayerHp = $derived(chosenSlot === 1 ? ($visualState?.PlayerHp ?? bars.playerHp) : bars.playerHp);
  const shownMonsterHp = $derived(chosenSlot === 1 ? ($visualState?.CurrentMonsterHp ?? bars.monsterHp) : bars.monsterHp);

  // Modul: region progression. The server refuses a target in a region whose
  // Modul: A BOSS YOU HAVE NEVER BEATEN IS FIVE TIMES THE MONSTER THE CONTENT
  // TABLES DESCRIBE.
  //
  // BossFirstClearRules gives an unbeaten region boss 5x health and 2x attack,
  // and reverts it once it falls - so a first clear is a milestone and farming
  // it afterwards is not a wall. The list here read the authored figure and
  // showed 5,850 HP for a fight that arrives with 29,250. Reported from play
  // as "the numbers written next to the boosted boss do not match what it
  // actually has".
  //
  // The mask says which bosses are already down; every fifth monster of a
  // region is its boss, which is content canon rather than an inference from
  // this screen.
  // Modul: A HIT HAS TO LAND SOMEWHERE.
  //
  // The bar simply got shorter, which is information without impact - the
  // difference between a progress meter and a fight. The portrait now flinches
  // when the monster loses health.
  //
  // Driven off the SERVER's HP field, not the interpolated one: the smoothed
  // value changes every animation frame, so keying on it would restart the
  // flash sixty times a second. This codebase has already paid for an effect
  // keyed to a per-frame signal - it starved the main thread.
  //
  // Rate-limited to one flash per 140ms. At ten ticks a second an unthrottled
  // class toggle is a strobe, and a strobing screen is a health problem rather
  // than a flourish.
  let struck = $state(false);
  let lastServerMonsterHp = 0;
  let lastFlashAtMs = 0;

  $effect(() => {
    const hp = bars.monsterHp;
    const previous = lastServerMonsterHp;
    lastServerMonsterHp = hp;

    if (previous <= 0 || hp >= previous) return;

    const now = Date.now();
    if (now - lastFlashAtMs < 140) return;
    lastFlashAtMs = now;

    struck = true;
    const handle = setTimeout(() => (struck = false), 120);
    return () => clearTimeout(handle);
  });

  // Modul: A MONSTER USED TO SIMPLY VANISH AND BE REPLACED.
  //
  // On a fast fight the death is the only moment there IS. Measured
  // 2026-09-04: a geared character kills an early monster inside a single
  // snapshot, so the health bar never animates and the target just becomes a
  // different one - which reads as the screen glitching rather than as a
  // victory. The server now says outright that a kill happened
  // (ResponseCombatEventPacket, KindKill), which is the only signal that can
  // arrive in time to animate it.
  //
  // Keyed on a kill COUNTER, not on the monster id: two Field Mice in a row
  // are the same id, and a value that does not change cannot restart an
  // animation.
  let dying = $state(false);

  $effect(() => {
    // Read so the effect re-runs on each kill; the value itself is not used.
    void $killPulse;
    if ($killPulse === 0) return;

    dying = true;
    const handle = setTimeout(() => (dying = false), 480);
    return () => clearTimeout(handle);
  });

  // Modul: was a local `const FIRST_CLEAR_HP = 5` - a hand-copy of a server
  // constant that no longer exists. The wall is per-region now
  // (BossFirstClearRules), and victories.ts is the one client-side home for the
  // table; serverMirrors.test.ts compares it to the C# element by element.
  const defeatedMask = $derived(snap?.DefeatedRegionBossMask ?? 0);


  function isFirstClearPending(monsterId: number): boolean {
    const region = bossRegionOf(monsterId);
    return region > 0 && (defeatedMask & (1 << (region - 1))) === 0;
  }

  // Modul: THE SERVER SAYS WHAT THE BAR'S MAXIMUM IS. This used to be
  // `monster.MaxHp * FIRST_CLEAR_HP` computed here, which was a hand-copy of
  // BossFirstClearRules and wrong twice: First Blood softens the first-clear
  // penalty (about 3.4x rather than 5x at level 8) and endgame regions scale
  // authored health on top. A bar whose maximum is too large sits part-empty
  // and barely moves, which is one of the two halves of "I can't see the
  // fight".
  //
  // The monster LIST below still uses the local rule, because there is no
  // snapshot for a monster the player is not fighting - and being approximate
  // about a monster you have not met is fine in a way that being wrong about
  // the one in front of you is not.
  const serverMonsterMaxHp = $derived(bars.monsterMaxHp);

  function shownMaxHp(monster: { Id: number; MaxHp: number }): number {
    return isFirstClearPending(monster.Id)
      ? Math.round(monster.MaxHp * firstClearHpMultiplier(bossRegionOf(monster.Id)))
      : monster.MaxHp;
  }

  /** The active monster's true maximum, from the server, with the local rule
   *  as a fallback for the frame before the first snapshot lands. */
  function activeMaxHp(monster: { Id: number; MaxHp: number }): number {
    return serverMonsterMaxHp > 0 ? serverMonsterMaxHp : shownMaxHp(monster);
  }

  // Modul: and the player's own maximum, which was a SESSION HIGH-WATER MARK
  // of the largest PlayerHp ever seen. A measured trace caught the bar reading
  // "2320 / 2320" while PlayerHp was 3701: the mark starts at whatever the
  // first snapshot happened to show and only ever grows. That estimate is gone;
  // before the first snapshot the bar is simply scaled against itself.
  const playerMaxHp = $derived(Math.max(1, bars.playerMaxHp, bars.playerHp));

  // predecessor's boss is still standing (CommandResultCode.RegionLocked), so
  // the list has to say which those are. Offering a Fight button that is
  // guaranteed to be rejected is how a rule reads as a bug.
  //
  // Falls back to region 1 rather than to "everything unlocked" when no state
  // has arrived yet: showing a locked region as open and having the click fail
  // is worse than showing an open one as locked for the moment before the
  // first packet lands.
  const unlockedRegion = $derived($playerState?.HighestUnlockedRegion || 1);

  // Modul: THE NEXT REGION, NOT THE WHOLE MAP. A new player's Combat screen
  // was 3,500px tall at 390px and four fifths of it was twenty locked monster
  // rows. The region you can open next stays listed, dimmed, because it is the
  // goal; everything past it folds into one line.
  const lastListedRegion = $derived(unlockedRegion + 1);

  // Modul: THE SAME THREE FIELDS THE TUTORIAL READS (tutorialSteps.ts). A
  // fight without food is usually lost - at level 1 against the first monster
  // it is lost in about thirty seconds - and the Fight button gave no sign of
  // it. Advice, not a gate: the button stays enabled.
  const larderEmpty = $derived(
    snap ? Number(snap.Food1_Count) + Number(snap.Food2_Count) + Number(snap.Food3_Count) === 0 : false,
  );


  let registry = $state<ContentRegistry | null>(null);
  let contentError = $state('');
  let selectedMonsterId = $state(0);
  // Which region's rules are open (0 = none), and which bosses' Challenges +
  // Ascension line is unfolded. Screen state only: a fold is not a setting.
  let rulesOpen = $state(0);
  let extrasOpen = $state<number[]>([]);
  let dropPreview = $state<MonsterLootEntry[]>([]);
  let dropPreviewFor = $state(0);

  onMount(async () => {
    try {
      registry = await loadContent();
    } catch (err) {
      contentError = err instanceof Error ? err.message : String(err);
    }
  });

  // Modul: this screen used to carry its OWN copy of the halt-reason strings,
  // which had already drifted from lib/ui/slots.ts by two entries. One table,
  // imported - the header badge and this panel must never disagree about why a
  // character stopped.


  const visual = $derived($visualState);
  const activeMonster = $derived(
    bars.monsterId > 0 ? (registry?.monsters.get(bars.monsterId) ?? null) : null,
  );
  const haltMessage = $derived(chosen ? (HALT_REASONS[chosen.halt] ?? '') : '');

  // Modul: THE CAILLEACH WEARS A REGION BOSS'S STATS. An armed seasonal boss
  // tier is slot 1 fighting that region's boss at its wall, so the fight is
  // the region boss's - only the name and the face are hers. The server
  // disarms the tier on any change of activity, so an armed tier on slot 1 IS
  // this fight.
  const seasonalTier = $derived(Number($playerState?.SeasonalBossTier ?? 0));
  const cailleach = $derived(seasonalTier > 0 && chosen?.slot === 1 && activeMonster !== null);

  // Modul: the painted region behind the monster. Regions are five monsters
  // each from FIRST_CANONICAL_MONSTER_ID (victories.ts); the seasonal boss is
  // fought on its own art, so it gets no region painting.
  const sceneBackground = $derived.by(() => {
    if (!activeMonster || cailleach) return null;
    const offset = activeMonster.Id - FIRST_CANONICAL_MONSTER_ID;
    if (offset < 0) return null;
    return locationBackground(Math.floor(offset / MONSTERS_PER_REGION) + 1);
  });
  const fightName = $derived(
    cailleach ? `The Cailleach · winter ${['', 'I', 'II', 'III', 'IV', 'V', 'VI'][seasonalTier]}` : (activeMonster?.Name ?? ''),
  );

  // Modul: DEPLOYED IS NOT THE SAME AS FIGHTING, and conflating them made a
  // real fault look like a no-op button.
  //
  // ActiveActivityId is what the player asked for; CurrentMonsterId is what
  // the simulation is actually doing. They diverge whenever a tick cannot run
  // - a full backpack returns from ProcessSubTick before anything spawns - and
  // this screen only ever read the second one. So clicking Fight set the
  // activity server-side, no monster appeared, and the screen said "Not in
  // combat", which reads as "the button did nothing".
  //
  // Named as its own state so the player is told they ARE deployed and what is
  // blocking them, instead of being shown the idle screen.
  const deployedTo = $derived(
    chosen && chosen.activity > 0 ? (registry?.monsters.get(chosen.activity) ?? null) : null,
  );
  const stalled = $derived(deployedTo !== null && activeMonster === null);

  // Modul: a brief shake on the monster portrait when a hit lands, and a flare
  // on the panel when a level is gained.
  //
  // Driven off the damage feed and the level number rather than off a timer,
  // so they fire exactly when the thing they describe happened. Both are keyed
  // to a counter that the CSS animation restarts from, which is how you replay
  // an animation in Svelte without removing and re-adding the node.
  let hitPulse = $state(0);
  let levelPulse = $state(0);
  let lastSeenLevel = 0;

  // Modul: keyed on the newest event ID, not on the array being non-empty.
  //
  // `damageEvents` is rewritten by the render loop every time it prunes an
  // expired number - roughly sixty times a second - so "the array has items"
  // fires continuously. An earlier version incremented on that, which
  // re-created the portrait node sixty times a second and starved the main
  // thread badly enough that the rest of the app stopped responding: the
  // health bar froze and every other screen failed to load. The symptom
  // looked nothing like an animation bug.
  //
  // The highest id only moves when a hit actually lands, which is the event
  // this is meant to reflect.
  let lastHitId = 0;

  $effect(() => {
    const events = $damageEvents;
    const newest = events.length > 0 ? events[events.length - 1].id : 0;
    if (newest > lastHitId) {
      lastHitId = newest;
      hitPulse++;
    }
  });

  $effect(() => {
    const level = snap?.CurrentLevel ?? 0;
    if (lastSeenLevel > 0 && level > lastSeenLevel) levelPulse++;
    lastSeenLevel = level;
  });

  // Modul: THE HUNTING ADVISOR (task 78). A row used to say HP and XP, and
  // because XP and gold both scale with HP, the row could not answer the
  // question it is read for: which of these can I farm, and how fast. The
  // server projects each fight from the live payload (HuntingProjection,
  // held to the real tick by HuntingProjectionTests) and caches it for a
  // minute. This side only words it. A 409 (no session) shows nothing.
  const projection = createQuery(() => ({
    // The picked character's fight - its gear and health, not slot 1's.
    queryKey: queryKeys.combatProjection(chosenSlot - 1),
    queryFn: () => fetchCombatProjection(chosenSlot - 1),
    enabled: $connectionStatus.phase === 'live',
    staleTime: 60_000,
    refetchInterval: 60_000,
    retry: false,
  }));
  const estimates = $derived(
    new Map<number, HuntingEstimate>((projection.data?.Monsters ?? []).map((e) => [e.MonsterId, e])),
  );
  // Modul: a second tap on the open card closes it - the drop table now opens
  // INSIDE the list, so a card that could only open would leave a long table
  // standing between the player and every monster below it.
  function toggleMonster(monster: MonsterDefinition) {
    if (selectedMonsterId === monster.Id) {
      selectedMonsterId = 0;
      return;
    }
    void selectMonster(monster);
  }

  async function selectMonster(monster: MonsterDefinition) {
    selectedMonsterId = monster.Id;
    if (dropPreviewFor !== monster.Id) {
      try {
        dropPreview = await authedGet<MonsterLootEntry[]>(
          `/api/v1/monsters/loot?monsterId=${monster.Id}`,
        );
        dropPreviewFor = monster.Id;
      } catch {
        dropPreview = [];
      }
    }
  }

  const activeCharacterId = $derived(chosen?.id ?? EMPTY_GUID);

  // Modul: Fight no longer opens the drop table. With the table inline under
  // its card, opening it on Fight pushed every row below down by its height at
  // the moment the player's thumb was over the list.
  function fight(monster: MonsterDefinition) {
    // One fighter per account (CharacterSlotEngine's kind-of-work rule):
    // say who is fighting instead of letting the server refuse it.
    const clash = kindTakenBy(workers, chosen?.slot, monster.Id);
    if (clash) return pushLocalNotice(kindTakenMessage(clash, nameById, monster.Id), 'error');
    // See Gathering.svelte: a bare TargetId does not persist.
    const outcome = assignCharacterActivity(activeCharacterId, monster.Id);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    writePref(PREF_LAST_MONSTER, String(monster.Id));
    lastMonsterId = monster.Id;
  }

  // Modul: TASK 72 - "NOT IN COMBAT" OFFERS THE WAY BACK. The idle state was a
  // sentence and nothing to press, so going back to the fight you had meant
  // finding the monster again in a list of twenty-five. The last monster the
  // main character fought is remembered on this device (prefs.ts) and offered
  // - only while its region is still open, which a season reset can change.
  let lastMonsterId = $state(Number(readPref(PREF_LAST_MONSTER) ?? 0));
  const lastMonster = $derived.by((): MonsterDefinition | null => {
    if (!registry || lastMonsterId <= 0) return null;
    for (let index = 0; index < registry.regions.length; index++) {
      const found = registry.regions[index].find((m) => m.Id === lastMonsterId);
      if (found) return index + 1 <= unlockedRegion ? found : null;
    }
    return null;
  });

  // Modul: THE RECOMMENDED FIGHT (design wave 2) - the open monster with the
  // most XP an hour that this character farms without food (bestPick). It is
  // the one Fight button drawn filled, and the idle strip offers it when there
  // is no last fight to go back to.
  const bestMonster = $derived.by((): MonsterDefinition | null => {
    if (!registry) return null;
    const open = new Map<number, MonsterDefinition>();
    registry.regions.forEach((region, index) => {
      if (index + 1 <= unlockedRegion) for (const m of region) open.set(m.Id, m);
    });
    const id = bestPick(estimates.values(), (monsterId) => open.has(monsterId), !larderEmpty);
    return id === null ? null : (open.get(id) ?? null);
  });

  // Modul: FIVE FOLDING REGIONS, NOT ONE LONG LIST (owner, 2026-10-28). Every
  // region's five monsters were one scroll, so reaching region 5 meant
  // scrolling past twenty rows every time. Each region folds; any number can
  // be open, and which are open is remembered on this device. A player who has
  // never chosen sees the region they are fighting in (or the best pick's, or
  // the newest one open) - never a closed list with nothing to press.
  function regionOfMonster(monsterId: number): number {
    const offset = monsterId - FIRST_CANONICAL_MONSTER_ID;
    return offset < 0 ? 0 : Math.floor(offset / MONSTERS_PER_REGION) + 1;
  }

  const storedRegions = readPref(PREF_OPEN_REGIONS);
  let openRegions = $state<number[] | null>(
    storedRegions === null ? null : storedRegions.split(',').map(Number).filter((n) => n > 0),
  );
  const defaultRegion = $derived(
    (activeMonster && regionOfMonster(activeMonster.Id)) ||
      (bestMonster && regionOfMonster(bestMonster.Id)) ||
      unlockedRegion,
  );
  const shownRegions = $derived(openRegions ?? [defaultRegion]);

  function setOpenRegions(next: number[]) {
    openRegions = next;
    writePref(PREF_OPEN_REGIONS, next.join(','));
  }

  function toggleRegion(regionNo: number) {
    setOpenRegions(
      shownRegions.includes(regionNo) ? shownRegions.filter((r) => r !== regionNo) : [...shownRegions, regionNo],
    );
  }

  const allRegionsOpen = $derived(
    Array.from({ length: lastListedRegion }, (_, i) => i + 1).every((r) => shownRegions.includes(r)),
  );

  // Task 72: what the next region's boss asks for, against what the main
  // character wears. Read from /player/worn (at most eleven rows), refreshed
  // while the screen is open so wearing a piece moves the count.
  const worn = createQuery(() => ({
    queryKey: [...queryKeys.worn, chosen?.id ?? ''],
    queryFn: () => fetchWorn(chosen?.id ?? ''),
    refetchInterval: 30_000,
  }));
  const wallProgress = $derived.by(() => {
    if (!registry || !worn.data) return null;
    const content = registry;
    return bossGearProgress(unlockedRegion, worn.data.Pieces, (id) => content.itemsByBaseId.get(id)?.RegionTier ?? 1);
  });

  // The first-clear multipliers as a range, for the rules paragraph - read
  // from the mirrored tables (serverMirrors.test.ts) rather than written in
  // words that can go stale, which is what "five times its health and twice
  // its damage" did when the wall became per-region.
  const hpRange = `${Math.min(...FIRST_CLEAR_HP_MULTIPLIERS)}x to ${Math.max(...FIRST_CLEAR_HP_MULTIPLIERS)}x`;
  const attackRange = `${Math.min(...FIRST_CLEAR_ATTACK_MULTIPLIERS)}x to ${Math.max(...FIRST_CLEAR_ATTACK_MULTIPLIERS)}x`;

  function stop() {
    const outcome = assignCharacterActivity(activeCharacterId, 0);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
  }

  // BaseItemId is the reliable identifier on a drop-preview row. Falls back to
  // the registry only when the row carries no BaseItemId at all.
  function dropEntryName(entry: MonsterLootEntry): string {
    return entry.BaseItemId ? prettifyBaseId(entry.BaseItemId) : itemName(registry, entry.ItemId);
  }

  // Modul: two lists, because they are two different questions. Materials are
  // "what am I farming here", equipment is "which monster has the helmet I am
  // missing" - and the second only became a real question when each monster got
  // its own gear table instead of every monster in a region sharing one pool.
  // The boss of this region has fallen at least once. The ladder's own flag
  // first (it is what the dev-tools restore and the ladder agree on), and the
  // wire's defeated-boss mask for the moment before the ladder query answers.
  function bossBeaten(region: number, ladderSaysBeaten: boolean | undefined): boolean {
    return Boolean(ladderSaysBeaten) || (defeatedMask & (1 << (region - 1))) !== 0;
  }

  function toggleExtras(region: number) {
    extrasOpen = extrasOpen.includes(region) ? extrasOpen.filter((r) => r !== region) : [...extrasOpen, region];
  }

  // Within-level XP (the server subtracts each level's cost as it is paid) and
  // what the current level costs - see levelCurve.ts.
  const currentXp = $derived(Math.floor(visual?.CurrentXp ?? snap?.CurrentXp ?? 0));
  const xpNeeded = $derived(xpToNextLevel(snap?.CurrentLevel ?? 0));

  const materialDrops = $derived(dropPreview.filter((entry) => !entry.IsEquipment));
  const equipmentDrops = $derived(dropPreview.filter((entry) => entry.IsEquipment));
</script>

<!-- Modul: TASK 98 - THE ACTION BEFORE THE PROSE.
     At 390px the first Fight sat at about y 915 (y 1060 for a guest), under a
     "Not in combat." panel, an empty loot panel and a six-line rules
     paragraph - one column means DOM order is reading order, and all three
     came first. Now: a compact status strip that stays pinned while the list
     scrolls, a one-line loot summary, and the monsters. The rules sit behind
     an (i) per region, the drop table opens under the card that was tapped,
     and a boss's Challenges and Ascension fold into one line that only exists
     once that boss has fallen.

     On a wide screen the strip and the fight log are a sticky LEFT column and
     the loot and monsters share the right. The loot stays to the right of the
     fight log on purpose: asked for directly ("under the monster only the
     course of the fight, the loot drops window on the right"), and
     exercise.mjs asserts it. -->
<div class="layout">
  <!-- Modul: `.side` is display: contents on a phone, so the strip is a child
       of `.layout` itself. A sticky box only travels inside its containing
       block: inside a short panel - or inside a one-column GRID, where each
       item's block is its own row track - it "sticks" across nothing, which
       is how the Wiki sidebar came to be declared sticky and never stick.
       `.layout` is a flex column below 64rem for the same reason. -->
  <!-- The level-up flare goes on the whole strip rather than on the number,
       because the number is small and the moment is not. -->
  {#key levelPulse}
  <aside class="side" aria-label="Your fight">
    <section class="strip" class:level-flare={levelPulse > 0} data-testid="combat-status">
      <h2 class="sr-only">Combat</h2>

      {#if contentError}
        <p class="error">Content failed to load: {contentError}</p>
      {/if}

      {#if $connectionStatus.phase !== 'live'}
        <p class="status">
          {$connectionStatus.phase}
          {#if $connectionStatus.detail}- {$connectionStatus.detail}{/if}
        </p>
      {/if}

      {#if snap}
        <div class="levelrow">
          <!-- Modul: the level number itself catches light when it changes.
               Marked where the number IS, rather than as a banner somewhere
               else - the eye is already on this figure when it moves. -->
          <span class="levelcell">
            <span class="dim">Lv</span>
            <strong>{snap.CurrentLevel}</strong>
            {#if $levelUpPulse > 0}
              {#key $levelUpPulse}
                <span class="levelfx folk-sweep"></span>
                <span class="levelburst"><Burst count={14} reach={2.8} /></span>
              {/key}
            {/if}
          </span>
          <!-- Modul: "XP 0" at level 40 said nothing - no denominator, so a
               level might be a minute or a week away. The cost is the
               server's own curve (levelCurve.ts, pinned by
               serverMirrors.test.ts). Deliberately NOT inside an .hpblock:
               exercise.mjs reads every bar there as a health bar. -->
          <div class="xp" title={numberTitle(currentXp)}>
            <Bar
              value={currentXp}
              max={Math.max(1, xpNeeded)}
              color="var(--accent)"
              label={xpNeeded > 0 ? `${formatNumber(currentXp)} / ${formatNumber(xpNeeded)} XP` : `${formatNumber(currentXp)} XP`}
            />
          </div>
        </div>

        <WorkerPicker
          {workers}
          names={nameById}
          selected={chosenSlot}
          describe={jobOf}
          onpick={(slot) => selectedCharacterSlot.set(slot)}
          label="Who fights"
        />

        <div class="hpblock">
          <span class="sr-only">Your health</span>
          <Bar
            value={shownPlayerHp}
            max={playerMaxHp}
            color="var(--good)"
            label={`HP ${formatNumber(Math.round(shownPlayerHp))} / ${formatNumber(playerMaxHp)}`}
          />
        </div>

        {#if activeMonster}
          <!-- The fight itself is the scene below the strip (owner, 2026-10-28). -->
        {:else if stalled}
          <!-- Deployed, but the simulation is not running. Saying "not in
               combat" here is what made the Fight button look broken. -->
          <!-- Modul: DO NOT PROMISE A REASON THAT IS NOT THERE.
               This said "See below for why" unconditionally, and the reason below
               only renders when the server sent one. When it did not - which is
               every case where the tick is not running this player at all - the
               screen pointed at an empty space, which is worse than saying
               nothing: it tells the player the answer exists and they have
               missed it. -->
          <div class="idle">
            <p class="stalled">
              Deployed to {deployedTo?.Name ?? `activity ${chosen?.activity ?? 0}`}, but nothing is
              happening.{haltMessage ? ' See below for why.' : ''}
            </p>
            <button class="standdown" onclick={stop}>Stand down</button>
          </div>
          {#if !haltMessage}
            <p class="dim small">
              The server has not said why. Standing down and deploying again
              usually clears it; if it keeps happening, a reload will.
            </p>
          {/if}
        {:else}
          <!-- Modul: idle OFFERS something. "Not in combat." was a sentence with
               nothing to press when there was no last monster; now it either
               points at the list or goes straight back to the last fight. -->
          <div class="idle">
            {#if lastMonster}
              <span class="dim">Not fighting.</span>
              <button class="continue" data-testid="combat-continue" onclick={() => fight(lastMonster)}>
                Continue: {lastMonster.Name}
              </button>
            {:else if bestMonster}
              <span class="dim">Not fighting.</span>
              <button class="continue primary" data-testid="combat-best" onclick={() => fight(bestMonster)}>
                Fight {bestMonster.Name}
              </button>
            {:else}
              <span class="dim">Not fighting - pick a monster below.</span>
            {/if}
          </div>
        {/if}
      {:else}
        <p class="dim">Waiting for the first state snapshot...</p>
      {/if}
    </section>

    <!-- Modul: THE FIGHT IS A SCENE, NOT A ROW (owner, 2026-10-28). The
         monster was a thumbnail beside its health bar inside the pinned strip;
         it is the width of the screen now, standing in front of its own
         region's painting, with the numbers and the blade marks over it.
         It sits OUTSIDE the sticky strip on purpose: pinned at this size it
         would cover the monster list it is chosen from.
         Only the monster shakes when hit - the scene, the bar and the button
         stay still, which is what makes the blow read as landing ON it. -->
    {#if snap && activeMonster}
      <div
        class="scene fighting"
        class:boss-scene={cailleach}
        style={sceneBackground ? `--scene-bg: url('${sceneBackground}')` : ''}
      >
        <div class="scene-head">
          <span class="target" data-testid="fight-target">Fighting {fightName}</span>
        </div>
        <div class="stage">
          <!-- Keyed on the pulse counter so the animation restarts on every
               hit; without the key Svelte reuses the node and the animation
               only ever plays once. -->
          {#key hitPulse}
            <span class="monster-hit">
              <span class="struckwrap" class:struck class:dying>
                <MonsterPortrait
                  monsterId={activeMonster.Id}
                  name={fightName}
                  size="scene"
                  src={cailleach ? spriteUrl('Events/samhain/boss/cailleach.webp') : null}
                />
              </span>
            </span>
          {/key}
          <!-- The mark the blow leaves, drawn over the monster it landed on.
               Shape by weapon family; a crit crosses it, a Double Strike
               flashes. Outside the shake, so the mark stays where it hit. -->
          <HitSpark />
          <span class="floatwrap"><FloatingDamage /></span>
        </div>
        <div class="scene-foot">
          <div class="hpblock grow">
            <Bar
              value={shownMonsterHp}
              max={activeMaxHp(activeMonster)}
              color="var(--danger)"
              label={`${formatNumber(Math.round(shownMonsterHp))} / ${formatNumber(activeMaxHp(activeMonster))}`}
            />
          </div>
          <button class="standdown" onclick={stop}>Stand down</button>
        </div>
      </div>
    {/if}

    {#if snap && haltMessage}
      <p class="halt">{haltMessage}</p>
    {/if}

    <!-- Modul: the fight log, under the monster's picture and health bar
         exactly where it was asked for - and ONLY the fight.
         Loot briefly lived here too and was moved out: asked for directly,
         "under the monster there should be only the course of the fight,
         and the loot drops window on the right". They were right, and for
         a reason bigger than taste - see SessionLoot on how material
         volume was evicting every piece of equipment.
         {#if} rather than a <details>: a closed <details> whose child
         carries an author display rule keeps its content live and
         clickable on top of whatever is below it, which this project has
         already shipped once. -->
    {#if activeMonster && $combatLog.length > 0}
      <ol class="fightlog" aria-label="Fight log">
        {#each $combatLog as line (line.id)}
          <li class:crit={(line.flags & CombatEventFlag.Crit) !== 0}
              class:miss={line.kind === CombatEventKind.PlayerMiss || line.kind === CombatEventKind.MonsterMiss}
              class:kill={line.kind === CombatEventKind.Kill}
              class:heal={line.kind === CombatEventKind.Lifesteal}
              class:incoming={line.kind === CombatEventKind.MonsterHit}>
            {describeCombatLine(line, monsterName(registry, line.monsterId))}
          </li>
        {/each}
      </ol>
    {/if}
  </aside>
  {/key}

  <div class="main">
    <!-- Modul: SECOND, DIRECTLY UNDER THE FIGHT - and on a phone that is the
         whole difference between a working screen and a frozen-looking one.
         Reported from a phone as "combat is frozen, nothing is added to loot
         drops": at level 87 an early monster dies BETWEEN TWO SNAPSHOTS, so
         the loot landing is the only evidence anything is happening. On a
         phone it is one line now (count and best piece), which still says
         that, and opens in place. -->
    <section class="panel lootpanel">
      <SessionLoot {registry} compact />
    </section>

    <section class="panel monsterpanel">
      <div class="monsterhead">
        <h2>Monsters</h2>
        <button
          class="btn-sm ghost"
          data-testid="regions-all"
          onclick={() =>
            setOpenRegions(allRegionsOpen ? [] : Array.from({ length: lastListedRegion }, (_, i) => i + 1))}
        >
          {allRegionsOpen ? 'Close all' : 'Open all'}
        </button>
      </div>
      {#if larderEmpty}
        <div class="larder-warning" role="note">
          <p>Your larder is empty. Without food a fight is usually lost.</p>
          <button onclick={() => requestScreen('larder')}>Stock the larder</button>
        </div>
      {/if}
      {#if registry}
        {#each registry.regions as region, index}
          {#if index + 1 <= lastListedRegion}
            {@const regionNo = index + 1}
            {@const challengeRow = (challenges.data ?? []).find((c) => c.Region === regionNo)}
            {@const ascensionRow = (ascension.data ?? []).find((b) => b.Region === regionNo)}
            <!-- Modul: each location gets its painted scene as a banner. The art
                 existed and nothing referenced it; a list of five identical
                 headings is a much weaker sense of place than the thing the
                 painting is of. -->
            {@const regionOpen = shownRegions.includes(regionNo)}
            {@const holdsBest = bestMonster !== null && regionOfMonster(bestMonster.Id) === regionNo}
            {@const holdsFight = activeMonster !== null && regionOfMonster(activeMonster.Id) === regionNo}
            <div
              class="place"
              class:folded={!regionOpen}
              style={locationBackground(regionNo)
                ? `background-image: linear-gradient(rgba(0,0,0,0.5), rgba(0,0,0,0.78)), url('${locationBackground(regionNo)}')`
                : ''}
            >
              <button
                class="place-toggle"
                aria-expanded={regionOpen}
                data-testid="region-toggle-{regionNo}"
                onclick={() => toggleRegion(regionNo)}
              >
                <span class="chev" aria-hidden="true">{regionOpen ? '▾' : '▸'}</span>
                <h3>
                  {locationName(regionNo)}
                  {#if regionNo > unlockedRegion}
                    <span class="locked-tag">Locked — defeat the {locationName(index)} boss</span>
                  {/if}
                </h3>
                {#if !regionOpen}
                  <span class="place-tags">
                    {#if holdsFight}<span class="chip fighting-tag">fighting</span>{/if}
                    {#if holdsBest}<span class="chip bestpick">best XP/h</span>{/if}
                  </span>
                {/if}
              </button>
              <button
                class="info"
                aria-expanded={rulesOpen === regionNo}
                aria-label="How {locationName(regionNo)} works"
                data-testid="region-rules-{regionNo}"
                onclick={() => (rulesOpen = rulesOpen === regionNo ? 0 : regionNo)}>i</button
              >
            </div>
            {#if regionOpen}
            {#if rulesOpen === regionNo}
              <!-- Modul: THE RULES OF THIS SCREEN, on request. A new player
                   meets a list of monsters, some locked, some lethal, and these
                   facts are all enforced by the server - they were simply a
                   six-line paragraph ABOVE the first Fight. Per region now, with
                   that region's own first-clear figures. -->
              <p class="dim small ruleset" data-testid="region-rules-text-{regionNo}">
                Four monsters and a boss, harder top to bottom. The next region
                opens when this boss falls. A boss you have never beaten is
                <strong>far stronger for that first kill</strong> - here
                {firstClearHpMultiplier(regionNo)}x its listed health and
                {firstClearAttackMultiplier(regionNo)}x its damage ({hpRange} and
                {attackRange} across the map) - and after it falls once it can be
                farmed at its normal stats. Dying stops combat but never gathering.
              </p>
            {/if}
            {#if regionNo > unlockedRegion && regionNo === unlockedRegion + 1}
              <!-- Task 72: the next region says what opens it, in numbers the
                   player can act on, instead of only naming the boss. -->
              <p class="wall" data-testid="region-wall">
                {describeBossGearRequirement(unlockedRegion)}
                {#if wallProgress}
                  <strong>You wear {wallProgress.meets} of {wallProgress.of}</strong>
                  at region {unlockedRegion} {rarityName(wallProgress.tier)} or better{#if wallProgress.affixRarity > 1 && wallProgress.affixesOf > 0},
                    and <strong data-testid="region-wall-affixes">{wallProgress.affixesMeet} of {wallProgress.affixesOf} affixes</strong>
                    are {AFFIX_RARITY_NAMES[wallProgress.affixRarity]} or better{/if}.
                {/if}
              </p>
            {/if}
            <ul class="monsters" class:locked={regionNo > unlockedRegion}>
              {#each region as monster}
                {@const bossRegion = bossRegionOf(monster.Id)}
                {@const firstClear = isFirstClearPending(monster.Id)}
                {@const est = regionNo <= unlockedRegion ? estimates.get(monster.Id) : undefined}
                {@const verdict = est ? (est.CanDamage ? safety(est) : { tone: 'danger', text: 'cannot hurt it' }) : null}
                {@const open = selectedMonsterId === monster.Id}
                {@const best = bestMonster?.Id === monster.Id}
                <li class:selected={open} class:best class:quiet-fight={bestMonster !== null && !best}>
                  <!-- Modul: TWO LINES, AND THE FIGHT BUTTON IS THE BIG THING.
                       One flex-wrapped line left the verdict ("you would die")
                       as an orphaned last token, and Fight was styled like the
                       card - so the card, the larger target, read as the
                       action. Name and verdict first, numbers second; Fight
                       filled. -->
                  <button
                    class="row"
                    aria-expanded={open}
                    aria-label="{monster.Name}: show drops"
                    onclick={() => toggleMonster(monster)}
                  >
                    <MonsterPortrait monsterId={monster.Id} name={monster.Name} size="sm" />
                    <span class="rowtext">
                      <span class="line1">
                        <span class="name">{monster.Name}</span>
                        {#if bossRegion > 0}<span class="chip boss">Boss</span>{/if}
                        {#if firstClear}<span class="chip firstclear">first clear</span>{/if}
                        {#if verdict}<span class="chip verdict {verdict.tone}">{verdict.text}</span>{/if}
                        {#if best}<span class="chip bestpick" data-testid="combat-best-chip">best XP/h</span>{/if}
                      </span>
                      <!-- Modul: WHAT IT PAYS IS THE LOUD HALF (design wave 2).
                           The row was one grey line of eight figures, and the
                           two that answer "should I fight this" - XP and gold an
                           hour - weighed the same as its health. They are a line
                           of their own at body weight now; what it takes (health,
                           XP a kill, time a kill) stays quiet beside them. -->
                      <span class="line2">
                        {#if est && est.CanDamage}
                          <span class="estimate yield" data-testid="hunting-estimate" title={estimateLine(est)}>
                            <span class="xph">{formatNumber(est.XpPerHour)} <span class="per">XP/h</span></span>
                            · <span class="goldph"><Money amount={est.GoldPerHour} /><span class="per">/h</span></span>
                          </span>
                        {/if}
                        <span class="dim">
                          <span class:firstclear-num={firstClear}>{formatNumber(shownMaxHp(monster))} HP</span>
                          · {formatNumber(monster.BaseXpReward)} XP{#if est && est.CanDamage}
                            · {killTimeText(est)} a kill{/if}
                        </span>
                        {#if est && !est.CanDamage}
                          <span class="estimate dim" data-testid="hunting-estimate" title={estimateLine(est)}>
                            you cannot hurt it yet
                          </span>
                        {/if}
                      </span>
                    </span>
                  </button>
                  <button
                    class="fight"
                    disabled={$connectionStatus.phase !== 'live' || regionNo > unlockedRegion}
                    onclick={() => fight(monster)}
                  >
                    Fight
                  </button>
                  {#if open}
                    <!-- Modul: THE DROP TABLE OPENS WHERE THE TAP WAS. It used
                         to render in its own panel ABOVE the monster list, so on
                         one column the only visible answer to a tap was a
                         border changing colour. -->
                    <div class="detail" data-testid="monster-drops-{monster.Id}">
                      {#if firstClear}
                        <!-- Modul: WHAT "FIRST CLEAR" MEANS, said on tap - a
                             title never shows on a phone. -->
                        <p class="dim tiny" data-testid="first-clear-detail">
                          Never beaten: {firstClearHpMultiplier(bossRegion)}x health and
                          {firstClearAttackMultiplier(bossRegion)}x damage until it falls once,
                          then it drops to its normal stats for good. {describeBossGearRequirement(bossRegion)}
                        </p>
                      {/if}
                      {#if dropPreviewFor !== monster.Id}
                        <p class="dim tiny">Loading drops...</p>
                      {:else if dropPreview.length === 0}
                        <p class="dim tiny">No drop data.</p>
                      {:else}
                        {#if materialDrops.length > 0}
                          <h4>Materials</h4>
                          <ul class="drops">
                            {#each materialDrops as entry}
                              <li>
                                <span class="drop-name">
                                  <ItemIcon baseItemId={entry.BaseItemId} name={dropEntryName(entry)} size="sm" />
                                  {dropEntryName(entry)}
                                </span>
                                <span class="dim">
                                  {entry.ChancePct.toFixed(2)}% &middot; {entry.MinQuantity}-{entry.MaxQuantity}
                                </span>
                              </li>
                            {/each}
                          </ul>
                        {/if}
                        {#if equipmentDrops.length > 0}
                          <h4>Equipment</h4>
                          <ul class="drops">
                            {#each equipmentDrops as entry}
                              <li>
                                <span class="drop-name">
                                  <ItemIcon baseItemId={entry.BaseItemId} name={dropEntryName(entry)} size="sm" />
                                  {dropEntryName(entry)}
                                </span>
                                <span class="dim">{entry.ChancePct.toFixed(2)}%</span>
                              </li>
                            {/each}
                          </ul>
                        {/if}
                      {/if}
                    </div>
                  {/if}
                </li>
              {/each}
            </ul>
            <!-- Modul: CHALLENGES AND ASCENSION WERE ABOUT 560px PER REGION,
                 including a "beat this boss once" placeholder under every boss
                 nobody had beaten. Both are post-clear content (the ladder
                 cannot start before a clear), so they fold into one line under
                 the boss and that line only exists once the boss has fallen. -->
            {#if regionNo <= unlockedRegion && (challengeRow || ascensionRow) && bossBeaten(regionNo, ascensionRow?.BossDefeated)}
              {@const done = challengeRow ? challengeRow.Challenges.filter((c) => c.Completed).length : 0}
              <button
                class="extras-toggle"
                aria-expanded={extrasOpen.includes(regionNo)}
                data-testid="boss-extras-{regionNo}"
                onclick={() => toggleExtras(regionNo)}
              >
                {#if challengeRow}<span>Challenges {done}/{challengeRow.Challenges.length}</span>{/if}
                {#if challengeRow && ascensionRow}<span class="dim">·</span>{/if}
                {#if ascensionRow}<span>Ascension {ascensionRow.HighestStep}/{ascensionRow.Steps.length}</span>{/if}
                <span class="chev" aria-hidden="true">{extrasOpen.includes(regionNo) ? '▾' : '▸'}</span>
              </button>
              {#if extrasOpen.includes(regionNo)}
                {#if challengeRow}<BossChallenges row={challengeRow} />{/if}
                {#if ascensionRow}<BossAscension boss={ascensionRow} />{/if}
              {/if}
            {/if}
            {/if}
          {/if}
        {/each}
        {#if registry.regions.length > lastListedRegion}
          {@const hidden = registry.regions.length - lastListedRegion}
          <p class="dim beyond">
            {hidden === 1 ? 'One more region lies' : `${hidden} more regions lie`} beyond. Each opens
            when the boss of the one before it falls.
          </p>
        {/if}
      {:else if !contentError}
        <p class="dim">Loading content...</p>
      {/if}
    </section>
  </div>
</div>

<style>
  .wall {
    font-size: 0.82rem;
    margin: 0.3rem 0 0.5rem;
    padding: 0.45rem 0.6rem;
    border-left: 2px solid var(--border);
  }

  .continue,
  .standdown {
    flex-shrink: 0;
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    padding: 0;
    overflow: hidden;
    clip: rect(0 0 0 0);
    white-space: nowrap;
    border: 0;
  }

  .larder-warning {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.8rem;
    padding: 0.4rem 0.6rem;
    margin: 0 0 0.5rem;
    font-size: 0.85rem;
    border: 1px solid var(--danger);
    border-radius: var(--radius);
  }

  .larder-warning p {
    margin: 0;
    flex: 1 1 12rem;
  }

  .larder-warning button {
    flex-shrink: 0;
  }

  .beyond {
    margin: 0.6rem 0 0;
    font-size: 0.85rem;
  }

  .levelcell {
    position: relative;
    border-radius: var(--radius);
  }

  .levelfx {
    position: absolute;
    inset: -0.2rem;
    border-radius: var(--radius);
    pointer-events: none;
  }

  .levelburst {
    position: absolute;
    left: 50%;
    top: 50%;
    width: 0;
    height: 0;
    pointer-events: none;
  }

  /* Modul: TASK 108 - will-change ONLY WHILE IT CHANGES. It was permanent,
     which kept the sprite on its own compositor layer (with a filter hint) for
     the whole time the screen was open, between hits as well as during them.
     The hint now rides on the two classes that animate. */
  .struckwrap {
    display: inline-block;
    /* The spark layer positions itself against this, so the arc and the burst
       land on the monster rather than in the corner of the panel. */
    position: relative;
  }

  .struckwrap.struck {
    will-change: transform, filter;
    animation: folk-struck 120ms ease-out;
  }

  @keyframes folk-struck {
    0% {
      transform: translateX(0);
      filter: brightness(2.1) saturate(0.4);
    }
    35% {
      transform: translateX(-3px) rotate(-1.5deg);
    }
    70% {
      transform: translateX(2px) rotate(1deg);
    }
    100% {
      transform: none;
      filter: none;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .struckwrap.struck {
      animation: none;
    }
  }

  .ruleset {
    margin: 0 0 0.5rem;
    max-width: 60ch;
  }

  /* Chips on the first line of a monster card: what kind of fight, and the
     server's verdict on it. A chip rather than a trailing word, so the verdict
     cannot be the orphaned last token of a wrapped line. */
  .chip {
    flex: none;
    font-size: 0.7rem;
    line-height: 1.4;
    border: 1px solid currentColor;
    border-radius: 999px;
    padding: 0 0.4rem;
    white-space: nowrap;
  }

  .chip.boss {
    color: var(--accent);
  }

  .firstclear,
  .firstclear-num {
    color: var(--warn);
  }

  /* Modul: the banner title was --text-dim on a darkened painting, which is
     low contrast twice over. The gradient already guarantees a dark ground,
     so the title is light on purpose rather than following the theme. */
  .place {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    background-color: #2a231b;
    background-size: cover;
    background-position: center;
    border-radius: var(--radius-sm);
    padding: 0.15rem 0.15rem 0.15rem 0.8rem;
    margin: 0.9rem 0 0.4rem;
  }

  .place-toggle {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-height: 2.75rem;
    padding: 0.2rem 0;
    border: none;
    background: none;
    box-shadow: none;
    text-align: left;
    color: #f4ead8;
  }

  .place-toggle .chev {
    flex: none;
    width: 0.8rem;
    font-size: 0.85rem;
  }

  .place-tags {
    margin-left: auto;
    display: inline-flex;
    gap: 0.3rem;
    flex-wrap: wrap;
    justify-content: flex-end;
  }

  .chip.fighting-tag {
    background: color-mix(in srgb, var(--sec-combat) 30%, transparent);
    border-color: var(--sec-combat);
    color: #fff;
  }

  .place.folded {
    margin-bottom: 0;
  }

  .monsterhead {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .place h3 {
    margin: 0;
    color: #f4ead8;
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.9);
    letter-spacing: 0.04em;
  }

  .place .info {
    flex-shrink: 0;
    /* 44px: the touch floor, measured by check:touch. */
    width: 2.75rem;
    height: 2.75rem;
    padding: 0;
    border-radius: 50%;
    font-family: var(--font-display);
    font-style: italic;
    font-weight: 700;
    color: #f4ead8;
    background: rgba(0, 0, 0, 0.35);
    border-color: rgba(244, 234, 216, 0.5);
  }

  .place .info[aria-expanded='true'] {
    background: var(--accent);
    color: var(--on-accent);
  }

  /* Modul: THE PAGE JITTERED THROUGHOUT EVERY FIGHT.
     `auto-fit` sizes the tracks from their content, and this screen's content
     is gold and XP counting up ten times a second. Every digit that changed
     width re-measured the whole grid and slid the monster list - and its Fight
     buttons - sideways. It reads as a wobble, and it is why an automated click
     on a Fight button could never land: the element genuinely never stopped
     moving.
     Fixed fractions, and tabular numerals so a digit is always the same
     width. */
  /* Task 98: a flex COLUMN on a phone, so the status strip (a child of this
     box while .side is display: contents) can stick across the whole screen.
     See the comment on .side in the markup. */
  .layout {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    padding: 0.75rem;
  }

  .side {
    display: contents;
  }

  .main {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    min-width: 0;
  }

  /* Modul: THE STRIP. Level, XP to the next level, health, the target and
     the way out - the four things a glance at Combat is for - pinned while
     the monster list scrolls under it. Opaque, because the rows scroll
     beneath. The offset is the status bar plus --sticky-header-h, which a
     sticky app header (task 95) can set; without one it is 0. */
  .strip {
    position: sticky;
    top: calc(var(--sa-top) + var(--sticky-header-h, 0px));
    z-index: var(--z-sticky);
    display: grid;
    gap: 0.4rem;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.6rem 0.75rem;
    box-shadow: 0 6px 14px rgba(0, 0, 0, 0.25);
  }

  .strip p {
    margin: 0;
  }

  .levelrow {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .levelrow .xp {
    flex: 1;
    min-width: 0;
  }

  /* The XP bar is the slow reward of every fight, so it is the strip's
     loudest meter: taller, its label larger. */
  .levelrow .xp :global(.bar) {
    height: 1.6rem;
  }

  .levelrow .xp :global(.bar-label) {
    font-size: 0.82rem;
  }

  /* The level as a badge in the section's colour, not loose text. */
  .levelcell {
    flex: none;
    display: inline-flex;
    align-items: baseline;
    gap: 0.25rem;
    font-size: 0.8rem;
    padding: 0.15rem 0.6rem;
    border: 1px solid var(--section);
    border-radius: 999px;
    background-image: linear-gradient(
      180deg,
      color-mix(in srgb, var(--section) 30%, transparent),
      color-mix(in srgb, var(--section) 8%, transparent)
    );
  }

  .levelcell strong {
    font-size: 1.15rem;
  }

  .idle {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.4rem 0.6rem;
    font-size: 0.85rem;
  }

  .target {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .floatwrap {
    position: absolute;
    inset: 0;
    pointer-events: none;
    z-index: 1;
  }

  /* Wide: a sticky left column (the strip and the fight log) and the loot and
     a wide monster table on the right, instead of three equal columns with
     the 25-monster list squeezed into one of them. */
  @media (min-width: 64rem) {
    .layout {
      display: grid;
      grid-template-columns: minmax(20rem, 26rem) minmax(0, 1fr);
      align-items: start;
      gap: 1rem;
      padding: 1rem;
    }

    .side {
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
      position: sticky;
      top: calc(var(--sa-top) + var(--sticky-header-h, 0px) + 0.5rem);
    }

    .strip {
      position: static;
      box-shadow: none;
    }
  }

  .layout strong {
    font-variant-numeric: tabular-nums;
  }

  h2 {
    margin: 0 0 0.75rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1rem 0 0.35rem;
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.75rem;
  }

  .monsterpanel h2 {
    margin-bottom: 0.4rem;
  }

  .hpblock {
    display: grid;
    gap: 0.2rem;
    font-size: 0.8rem;
  }

  .monsters,
  .drops {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  /* Modul: a locked region stays READABLE. Dimmed, not hidden - knowing what
     is behind the boss is the reason to go and fight it, and a region that
     simply is not drawn reads as content that does not exist yet. */
  .monsters.locked {
    opacity: 0.45;
  }

  .locked-tag {
    display: block;
    font-size: 0.75rem;
    font-weight: 400;
    text-transform: none;
    letter-spacing: 0.02em;
    opacity: 0.9;
  }

  /* Modul: ONE FRAME PER MONSTER, FIGHT INSIDE IT (owner, 2026-10-28). The
     card used to be the left button alone with Fight floating beside it,
     centred on the row's height - which read as two objects. The li is the
     card now; the info button inside it is transparent, a hairline divides it
     from Fight, and Fight sits at the bottom of its own column. */
  .monsters li {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: stretch;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background-color: var(--bg-raised);
    background-image: linear-gradient(180deg, rgba(255, 255, 255, 0.035), rgba(0, 0, 0, 0.12));
    overflow: hidden;
    gap: 0.35rem;
  }

  .monsters li.selected {
    border-color: var(--accent);
  }

  /* Modul: FIGHT IS THE ACTION, SO IT LOOKS LIKE ONE. It was styled like the
     card beside it, and the card was the bigger target. */
  /* Owner, 2026-10-28: one height for every Fight, whatever the row beside it
     says - it stretched to each row's height and read as oversized. 2.75rem
     is the 44px touch floor. */
  .fight {
    flex-shrink: 0;
    min-width: 4.5rem;
    height: 2.75rem;
    padding-block: 0;
    align-self: end;
    margin: 0.4rem 0.45rem;
    /* In Combat's ember, lit at the top (sections.ts / app.css). */
    background-color: var(--sec-combat-deep);
    background-image: linear-gradient(180deg, color-mix(in srgb, var(--sec-combat) 88%, #fff), var(--sec-combat) 45%, var(--sec-combat-deep));
    border-color: color-mix(in srgb, var(--sec-combat) 70%, #fff);
    color: #fff4ef;
    text-shadow: 0 1px 1px rgba(0, 0, 0, 0.35);
    font-weight: 700;
  }

  .fight:disabled {
    background: var(--bg-raised);
    border-color: var(--border);
    color: var(--text-dim);
    font-weight: 400;
  }

  .detail {
    grid-column: 1 / -1;
    margin: 0 0.45rem 0.45rem;
    padding: 0.45rem 0.6rem;
    border-left: 2px solid var(--accent);
    background: var(--bg-sunken);
    border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
  }

  .detail p {
    margin: 0 0 0.3rem;
  }

  .detail h4 {
    margin: 0.3rem 0 0.2rem;
    font-size: 0.78rem;
  }

  .extras-toggle {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.25rem 0.5rem;
    width: 100%;
    margin: 0.35rem 0 0.25rem;
    font-size: 0.82rem;
    text-align: left;
  }

  .extras-toggle .chev {
    margin-left: auto;
  }

  /* Modul: THE NAME MUST NOT BE THE ONLY THING THAT GIVES WAY.
     The grid this replaced let the 1fr name track shrink to nothing, so boss
     rows showed a portrait, a health figure and NO NAME. The card is two
     lines now - name and chips, then the numbers - and only the numbers
     wrap; `.name` ellipsises but is the last thing to give. On a wide screen
     the two lines sit side by side, which is the "wide monster table". */
  .row {
    display: flex;
    gap: 0.6rem;
    align-items: center;
    text-align: left;
    font-size: 0.85rem;
    min-width: 0;
    padding: 0.35rem 0.55rem;
    border: none;
    border-radius: 0;
    background: none;
    box-shadow: none;
    border-right: 1px solid color-mix(in srgb, var(--text-dim) 16%, transparent);
  }

  /* The portrait keeps its size; `.panel *`'s min-width: 0 would otherwise
     let flex squeeze it. */
  .row > :global(:first-child) {
    flex: none;
  }

  .rowtext {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    gap: 0.1rem;
  }

  /* Modul: THE LINE WRAPS. "first clear needs food (~59/h)" is a chip with
     nowrap and flex: none, so beside a boss name on a 390px phone it ran 41px
     past the card with nothing to show it. Wrapping drops the verdict to its
     own line under the name; max-width keeps one chip inside the card even
     when it is longer than the card is wide. */
  .line1 {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.1rem 0.35rem;
    min-width: 0;
  }

  .line1 .chip {
    max-width: 100%;
    white-space: normal;
    overflow-wrap: anywhere;
    border-radius: 0.8rem;
  }

  .line2 {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0 0.6rem;
    font-size: var(--fs-note);
    overflow-wrap: anywhere;
  }

  .yield {
    font-size: var(--fs-base);
    font-weight: 600;
    color: var(--text);
  }

  /* Modul: WHAT A MONSTER PAYS, EACH IN ITS OWN COLOUR (owner, 2026-10-28).
     XP is a cool spirit-blue - it sits apart from gold and from the ember of
     the Fight button - and the "/h" after each figure wears its figure's
     colour at the same size as gold's "g", instead of a white tail. */
  .xph {
    color: #7cc6f2;
  }

  .goldph {
    display: inline-flex;
    align-items: center;
    color: var(--gold);
  }

  .per {
    font-size: 0.85em;
    opacity: 0.85;
  }

  :global(:where(:root[data-scheme='light'])) .xph {
    color: #1f6fa6;
  }

  .chip.bestpick {
    color: #1d1408;
    background: linear-gradient(180deg, #ffe08a, #d4a12c);
    border-color: #d4a12c;
  }

  .monsters li.best {
    border-color: #d4a12c;
    box-shadow: inset 3px 0 0 #d4a12c;
  }

  /* Modul: ONE FILLED FIGHT. While a recommendation exists every other Fight
     is outlined in the accent - still plainly a button, and still the thing
     to press on its own row (the reason it was filled in the first place),
     but no longer fifteen equal answers. With no recommendation they all
     stay filled, as before. */
  .monsters li.quiet-fight .fight:not(:disabled) {
    background: transparent;
    background-image: none;
    border-color: color-mix(in srgb, var(--sec-combat) 75%, transparent);
    color: var(--sec-combat);
    text-shadow: none;
  }

  @media (min-width: 64rem) {
    .rowtext {
      grid-template-columns: minmax(12rem, 20rem) minmax(0, 1fr);
      align-items: center;
      gap: 0.8rem;
    }
  }

  .verdict.safe {
    color: var(--good);
  }

  .verdict.food {
    color: var(--warn);
  }

  .verdict.danger {
    color: var(--danger);
  }
  /* The portrait sits beside the health bar rather than above it, so the
     fight reads as one thing at a glance. */
  /* Modul: a two-column grid, portrait spanning both rows. The portrait is
     large now (size xl), and with Stand down as a third flex item beside it a
     390px row left the health bar about 60px wide. Stacking the bar over the
     button keeps both in the column the portrait leaves, so nothing wraps and
     the strip grows only to the portrait's own height. */
  /* Modul: THE SCENE. The region's painting fills the box (cover, anchored
     low so the ground the monster stands on stays in frame), darkened toward
     the top and bottom so the name and the health bar read over any art. The
     monster is sized off the scene's width, so a phone gets a monster that is
     most of the screen rather than a thumbnail. */
  .scene {
    position: relative;
    display: flex;
    flex-direction: column;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
    background:
      linear-gradient(180deg, rgba(0, 0, 0, 0.55) 0%, rgba(0, 0, 0, 0) 22%, rgba(0, 0, 0, 0) 62%, rgba(0, 0, 0, 0.8) 100%),
      var(--scene-bg, radial-gradient(ellipse at 50% 70%, #3a2c1e, #120e0a)) center 70% / cover no-repeat,
      #1a140f;
    box-shadow: inset 0 0 40px rgba(0, 0, 0, 0.55);
    min-height: clamp(17rem, 78vw, 22rem);
  }

  .scene.boss-scene {
    border-color: var(--rarity-10);
  }

  .scene-head {
    padding: 0.5rem 0.75rem 0;
    text-align: center;
    color: #fff;
    font-weight: 700;
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.9);
    z-index: 2;
  }

  .stage {
    position: relative;
    flex: 1;
    display: grid;
    place-items: end center;
    min-height: 0;
  }

  .scene-foot {
    position: relative;
    z-index: 2;
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.5rem 0.75rem 0.65rem;
  }

  .scene-foot .standdown {
    flex: none;
  }

  /* Over a dark scene the bar's own dim label was dark text on dark red. */
  .scene-foot :global(.bar) {
    height: 1.35rem;
    border-color: rgba(0, 0, 0, 0.6);
    background: rgba(0, 0, 0, 0.55);
  }

  .scene-foot :global(.bar-label) {
    color: #fff;
    font-weight: 700;
    text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
  }

  /* Only the monster moves. A heavier shake than the strip's old 2px one,
     because the sprite is three times the size and 2px on it is invisible. */
  .monster-hit {
    display: inline-block;
    animation: monster-hit 200ms ease-out;
  }

  @keyframes monster-hit {
    0%,
    100% {
      transform: none;
    }
    20% {
      transform: translate(-6px, 1px) rotate(-1.5deg);
    }
    45% {
      transform: translate(5px, -1px) rotate(1deg);
    }
    70% {
      transform: translate(-3px, 0);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .monster-hit {
      animation: none;
    }
  }

  .grow {
    flex: 1;
    min-width: 0;
  }

  /* Modul: the death. DISINTEGRATION, not a fall.
     Asked for directly - "something like when Thanos snaps" - and the falling,
     rotating, greying version that shipped first was wrong for a reason worth
     keeping: at this cadence a monster dies every second or so, and a body
     toppling over reads as heavy. A dissolve is over before it can feel slow.

     Built from a MASK rather than particles. A particle system would need a
     canvas, a per-frame loop and a pool, for an effect that lasts 420ms on a
     sprite the size of a thumbnail - and this codebase has already paid for
     keying a per-frame effect to combat (it starved the main thread). Two
     radial gradients scrolling across an alpha mask eat the sprite from one
     corner, which is the same trick the film used and costs one compositor
     layer.

     `forwards` so it holds the final frame rather than snapping back for the
     few hundred milliseconds before the replacement lands. */
  .dying {
    will-change: transform, filter, opacity;
    animation: monster-disintegrate 420ms cubic-bezier(0.4, 0, 0.9, 0.6) forwards;
    /* The mask is animated by moving its position, which the compositor can do
       without repainting the sprite. */
    -webkit-mask-image: radial-gradient(circle at 12% 88%, transparent 0 34%, #000 62%);
    mask-image: radial-gradient(circle at 12% 88%, transparent 0 34%, #000 62%);
    -webkit-mask-size: 300% 300%;
    mask-size: 300% 300%;
  }

  @keyframes monster-disintegrate {
    0% {
      -webkit-mask-position: 100% 0%;
      mask-position: 100% 0%;
      filter: brightness(1.9) saturate(0.4);
      transform: scale(1.05);
      opacity: 1;
    }
    /* A single bright frame first: the blow lands, THEN it comes apart. Without
       it the dissolve starts before the hit has registered and reads as the
       sprite failing to load. */
    12% {
      -webkit-mask-position: 92% 8%;
      mask-position: 92% 8%;
      filter: brightness(2.4) saturate(0.2);
      transform: scale(1.07);
      opacity: 1;
    }
    100% {
      -webkit-mask-position: 0% 100%;
      mask-position: 0% 100%;
      filter: brightness(1.1) saturate(0.9);
      /* Drifts up and slightly apart rather than falling - ash goes the other
         way from a body. */
      transform: scale(1.14) translateY(-10px);
      opacity: 0;
    }
  }

  /* A player who does not want the motion should not be given it. The mask has
     to be cleared too, or the sprite keeps whatever slice of itself the static
     gradient happens to cover. */
  @media (prefers-reduced-motion: reduce) {
    .dying {
      animation: none;
      -webkit-mask-image: none;
      mask-image: none;
      opacity: 0.35;
      filter: grayscale(1);
    }
  }

  /* Modul: the fight log. Fixed height with its own scroll, so a log that
     fills cannot push the Stop fighting button off the screen - and so it
     occupies the same space whether it holds two lines or fifty. Newest at
     the top, because that is where the eye already is. */
  .fightlog {
    list-style: none;
    margin: 0.6rem 0 0;
    padding: 0.4rem 0.6rem;
    max-height: 11rem;
    overflow-y: auto;
    overflow-anchor: none;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-sunken);
    font-size: 0.82rem;
    line-height: 1.5;
    font-variant-numeric: tabular-nums;
  }

  /* A phone keeps the course of the fight to a few lines, so it cannot stand
     between the strip and the monster list. */
  @media (max-width: 63.99rem) {
    .fightlog {
      max-height: 5.2rem;
    }
  }

  .fightlog li {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    opacity: 0.9;
  }

  .fightlog li.crit {
    color: var(--warn);
    font-weight: 600;
  }

  .fightlog li.miss {
    opacity: 0.5;
    font-style: italic;
  }

  .fightlog li.incoming {
    color: var(--danger);
  }

  .fightlog li.heal {
    color: var(--good);
  }

  .fightlog li.kill {
    color: var(--accent);
    font-weight: 600;
  }

  .name {
    /* The chips beside it are short and nowrap; the name is the one thing on
       the line allowed to ellipsise, and only once they have taken their room. */
    flex: 0 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .drops li {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.75rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.25rem;
  }

  .drop-name {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    min-width: 0;
  }

  .halt {
    margin: 0;
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
  }

  /* Warn, not danger: the player did the right thing and something is in the
     way, which is a different message from "this failed". */
  .stalled {
    margin: 0;
    color: var(--warn);
    font-size: 0.88rem;
  }

  .error {
    color: var(--danger);
  }

  .status {
    color: var(--text-dim);
    font-size: 0.85rem;
  }
</style>
