<script lang="ts">
  import ScreenBanner from '../lib/ui/ScreenBanner.svelte';
  import { formatNumber } from '../lib/ui/format';
  // Modul: THE HALL OF ANCESTORS - the roster that outlives a run.
  //
  // Levels, gear, gold and the village's gene pool reset at a rebirth, which
  // the player now triggers themselves (task 88) instead of a 90-day date. What
  // survives is a handful of people and the aptitudes bred into them, and a cap
  // is what turns that into a choice: without one, a season accumulates every
  // child ever born and its last week is worth as much as its first.
  //
  // Three jobs, and all three were missing once:
  //   - field a member (nothing could change a SlotIndex, so a bred child was
  //     unplayable forever),
  //   - mark who carries through the rollover,
  //   - read the pedigree.
  //
  // Modul: CARRIED AND LOST, NOT ONE LIST BY GENERATION (task 104). The page
  // was 37,468px tall on a phone: every member of the line, grouped by
  // generation, each row carrying "Field into [1][2][3] [Keep]" - about 200
  // rows, most of them people the next rebirth deletes, drawn at 45% opacity
  // with a "Kept" button that looked like safety. The question a player brings
  // here is "who survives", so that is the cut: the carried few, open, in the
  // order the cull ranks them; the lost, collapsed and paged. Every row shows
  // ONE state and a pin; the rest - traits, parents, fielding - opens in a
  // sheet. The split itself is the server's WouldCarry, never recomputed here
  // (see hallSections.ts).
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchAncestorsHall, fetchTraits, fetchExpeditions, type HallMember } from '../lib/net/rest';
  import { purchaseAncestorSlot, setAncestorKept, assignCharacterSlot, APTITUDE_MAX } from '../lib/net/commands';
  import { raceName } from '../lib/ui/races';
  import { traitsOf } from '../lib/ui/traits';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import TraitBadge from '../lib/ui/TraitBadge.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { requestScreen } from '../lib/stores/navigation';
  import ConfirmButton from '../lib/ui/ConfirmButton.svelte';
  import DisabledReason from '../lib/ui/DisabledReason.svelte';
  import DetailSheet from '../lib/ui/DetailSheet.svelte';
  import { commandInFlight } from '../lib/ui/commandInFlight';
  import { APTITUDES } from '../lib/ui/aptitudes';
  import { splitHall, claimedCount, hallBadge, aptitudeTotal } from '../lib/ui/hallSections';

  const client = useQueryClient();
  const hall = createQuery(() => ({ queryKey: queryKeys.ancestorsHall, queryFn: fetchAncestorsHall }));
  // Expeditions (2026-11-13): the Hall is where the idle bench is seen, so it
  // says when expedition slots stand empty while grown ancestors sit here.
  const expeditions = createQuery(() => ({ queryKey: queryKeys.expeditions, queryFn: fetchExpeditions }));
  const freeExpeditionSlots = $derived(
    expeditions.data ? Math.max(0, expeditions.data.Slots - expeditions.data.Active.length) : 0,
  );
  const idleBench = $derived(expeditions.data?.Candidates.length ?? 0);
  const traitCatalogue = createQuery(() => ({ queryKey: queryKeys.traits, queryFn: fetchTraits, staleTime: Infinity }));

  const data = $derived(hall.data);
  const members = $derived(data?.Members ?? []);
  const sections = $derived(splitHall(members));
  const claimed = $derived(claimedCount(members));

  // Modul: YOUR BEST BLOOD ON THE BENCH (2026-11-13). Aptitudes pay only on
  // the character who plays, and nothing said so: the dev fixture plays its
  // 4/4/4/4 founder beside bred adults of 40+. (The production endgame
  // accounts were checked afterwards and do field bred characters - this is
  // for whoever does not.) The nudge names the strongest grown member when it
  // clearly beats whoever stands in slot 1 (8+ points of total).
  const BENCH_NUDGE_GAP = 8;
  const slotOne = $derived(members.find((m) => m.PlayableSlot === 0));
  const bestAdult = $derived(
    members
      .filter((m) => m.AgePhase >= 1)
      .reduce<HallMember | undefined>((best, m) => (!best || aptitudeTotal(m) > aptitudeTotal(best) ? m : best), undefined),
  );
  const benchNudge = $derived(
    slotOne && bestAdult && bestAdult.CharacterId !== slotOne.CharacterId
      && aptitudeTotal(bestAdult) - aptitudeTotal(slotOne) >= BENCH_NUDGE_GAP
      ? { best: bestAdult, playing: slotOne }
      : null,
  );

  // Modul: BETTER BLOOD THAN WHAT IS MARKED (2026-11-13). Marked members carry
  // first, so a better child bred after the marks were set is cut at the
  // rebirth. On production the owner's account had a 75 outside a marked ten
  // whose weakest was 46. Named here, where the marks are set (the Rebirth
  // screen repeats it); WouldCarry is the server's answer.
  const lostNudge = $derived.by(() => {
    const marked = members.filter((m) => m.WouldCarry && m.IsKept && !m.IsMainCharacter);
    const left = members.filter((m) => !m.WouldCarry);
    if (marked.length === 0 || left.length === 0) return null;
    const weakest = marked.reduce((a, b) => (aptitudeTotal(b) < aptitudeTotal(a) ? b : a));
    const best = left.reduce((a, b) => (aptitudeTotal(b) > aptitudeTotal(a) ? b : a));
    return aptitudeTotal(best) > aptitudeTotal(weakest) ? { best, weakest } : null;
  });

  // The lost list is collapsed and paged: it grows with every breeding, and it
  // is the part of the page a player reads least.
  const LOST_PAGE = 20;
  let carriedOpen = $state(true);
  let lostOpen = $state(false);
  let lostShown = $state(LOST_PAGE);
  let rulesOpen = $state(false);

  // The sheet is keyed by id and re-reads the member from the live query, so a
  // mark or a fielding that moves the row between sections leaves it open on
  // the same person with their new state.
  let sheetId = $state<string | null>(null);
  const sheetMember = $derived(sheetId === null ? null : (members.find((m) => m.CharacterId === sheetId) ?? null));

  function refresh() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
      client.invalidateQueries({ queryKey: queryKeys.breedingRoster });
    }, 900);
  }

  function mark(m: HallMember) {
    const outcome = setAncestorKept(m.CharacterId, !m.IsKept);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    refresh();
  }

  // One tap for the fix the lost-blood nudge names: unmark the weaker, mark
  // the better. Two ordinary keep commands, in that order.
  function swapMarks(weaker: HallMember, better: HallMember) {
    const off = setAncestorKept(weaker.CharacterId, false);
    if (!off.ok) return pushLocalNotice(off.reason, 'error');
    const on = setAncestorKept(better.CharacterId, true);
    if (!on.ok) return pushLocalNotice(on.reason, 'error');
    pushLocalNotice(`${nameOf(better)} will carry instead of ${nameOf(weaker)}.`, 'info');
    refresh();
  }

  function field(m: HallMember, slotIndex: number) {
    const outcome = assignCharacterSlot(m.CharacterId, slotIndex);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    refresh();
  }

  function buySlot() {
    // Modul: diamonds, so two taps and one command - see ConfirmButton.
    const outcome = commandInFlight.run('ancestor-slot', () => purchaseAncestorSlot());
    if (outcome === null) return;
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    refresh();
  }

  function nameOf(m: HallMember): string {
    return m.Name || `${raceName(m.RaceId)} ${m.IsFemale ? 'woman' : 'man'}`;
  }

  function generationTag(m: HallMember): string {
    return m.GenerationIndex === 0 ? 'founder' : `gen ${m.GenerationIndex}`;
  }

  // The pedigree, as the data actually supports it: who each member came from.
  // A villager parent is not a character and is deliberately not stored, so
  // half a parentage is the honest answer rather than an invented name.
  //
  // Modul: BY NAME. This printed eight hex digits of each parent's Guid -
  // "b6b704ca x 0214b4e9" - a day after characters were given names. A parent
  // id with no name is somebody the cull has since let go.
  function parentName(id: string, name: string): string {
    if (!id) return '';
    return name || 'an ancestor since let go';
  }

  function parentage(m: HallMember): string {
    const father = parentName(m.ParentPaternalId, m.ParentPaternalName);
    const mother = parentName(m.ParentMaternalId, m.ParentMaternalName);

    if (!father && !mother) return 'a founder of the line';
    if (father && mother) return `child of ${father} and ${mother}`;
    return `child of ${father || mother} and somebody from the village`;
  }

  /** What the badge means, in a sentence, for the sheet. */
  function fateLine(m: HallMember): string {
    if (m.IsMainCharacter) return 'Your first character always carries.';
    if (m.IsKept && !m.WouldCarry) {
      return `Marked to keep, but more are marked than there are slots (${claimed} for ${data?.Cap ?? 0}). The stronger marks carry; this one is lost at rebirth unless you buy a slot or unmark somebody.`;
    }
    if (m.IsKept) return 'Marked to keep - carries through your next rebirth.';
    if (m.WouldCarry) return 'Carries on the strength of their blood. Unmarked, so a stronger child can still push them out.';
    return 'Lost at rebirth. Mark them to keep, and they outrank everybody unmarked.';
  }
</script>

{#snippet row(m: HallMember)}
  {@const badge = hallBadge(m)}
  <li data-character-id={m.CharacterId} data-would-carry={m.WouldCarry}>
    <button type="button" class="open" onclick={() => (sheetId = m.CharacterId)} aria-label="{nameOf(m)}: details">
      <RaceIcon raceId={m.RaceId} />
      <span class="who">
        <span class="name">
          {nameOf(m)}
          {#if m.IsEpicMutation}<span class="epic" title="Epic mutation">&#9733;</span>{/if}
        </span>
        <span class="tags">
          <span class="gen">{generationTag(m)}</span>
          <span class="badge {badge.kind}">{badge.text}</span>
          <!-- Children grow up on their own within an hour of birth since
               2026-11-13 (ChildGrowth); "Field to raise" no longer applies. -->
          {#if m.AgePhase === 0}<span class="badge raise" data-testid="hall-raise">Growing up</span>{/if}
          {#if m.IsAway}<span class="badge raise">On expedition</span>{/if}
        </span>
      </span>
      <!-- Modul: labelled once, by the column header above the list, and per
           number for a screen reader - the names were once only a `title`,
           which a phone never shows. -->
      <span class="apts">
        {#each APTITUDES as apt (apt.short)}
          <span aria-label="{apt.name} {apt.of(m)}">{apt.of(m)}</span>
        {/each}
        <span class="sum" aria-label="Total {aptitudeTotal(m)} of {APTITUDE_MAX * 4}">{aptitudeTotal(m)}</span>
      </span>
    </button>
    <!-- The main character's id IS the account's own id, so they can never be
         the one let go. A toggle that could not be turned off would be a lie,
         so there is none. -->
    {#if m.IsMainCharacter}
      <span class="pin always" title="Your first character always carries" aria-label="Always carries">
        <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M5 1h6l-1 5 3 3H9l-1 6-1-6H3l3-3z" fill="currentColor" /></svg>
      </span>
    {:else}
      <button
        type="button"
        class="keep"
        class:on={m.IsKept}
        class:over={m.IsKept && !m.WouldCarry}
        aria-pressed={m.IsKept}
        aria-label={m.IsKept ? `Unmark ${nameOf(m)}` : `Keep ${nameOf(m)} through the rebirth`}
        title={m.IsKept ? 'Marked to keep - tap to unmark' : 'Mark to keep through the rebirth'}
        onclick={() => mark(m)}
      >
        <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
          <path d="M5 1h6l-1 5 3 3H9l-1 6-1-6H3l3-3z" fill={m.IsKept ? 'currentColor' : 'none'} stroke="currentColor" stroke-width="1.3" stroke-linejoin="round" />
        </svg>
      </button>
    {/if}
  </li>
{/snippet}

{#snippet columns()}
  <div class="cols" aria-hidden="true">
    <span class="cols-who"></span>
    <span class="apts">
      <!-- Two letters, not one: Strength and Skill both began with S. -->
      {#each APTITUDES as apt (apt.short)}<span title={apt.name}>{apt.short.slice(0, 2)}</span>{/each}
      <span class="sum" title="Total, of {APTITUDE_MAX * 4}">Total</span>
    </span>
    <span class="cols-pin">Keep</span>
  </div>
{/snippet}

<div class="wrap">
  <ScreenBanner art="banner_hall" line="Ten carry through. The rest are remembered." />
  <section class="panel hall">
    <header>
      <div>
        <h2>Hall of Ancestors</h2>
        <p class="dim small">
          A rebirth takes back your levels, your gear and your gold. It does
          not take these. When you are reborn, only
          <strong>{data?.Cap ?? 10}</strong> of them carry.
        </p>
      </div>
      {#if data}
        <div class="counters">
          <span class="tally" class:full={sections.carried.length >= data.Cap} data-testid="hall-carried">
            Carried {sections.carried.length} / {data.Cap}
          </span>
          <!-- Modul: the count of claims, so "more marked than slots" is a
               number before it is a surprise. -->
          <span class="tally kept" class:over={claimed > data.Cap} data-testid="hall-kept">
            Kept {claimed} / {data.Cap}
          </span>
          {#if data.NextSlotCostDiamonds > 0}
            <!-- Modul: beside the counter it raises, and no longer the biggest
                 control on the page. Still two taps (task 92): it spends
                 diamonds. -->
            <div class="buy">
              <ConfirmButton
                small
                danger={false}
                label="+ slot ({formatNumber(data.NextSlotCostDiamonds)} diamonds)"
                confirmLabel="Spend {formatNumber(data.NextSlotCostDiamonds)} diamonds?"
                disabled={data.Diamonds < data.NextSlotCostDiamonds || $commandInFlight.has('ancestor-slot')}
                onConfirm={buySlot}
              />
              <DisabledReason
                text={data.Diamonds < data.NextSlotCostDiamonds
                  ? `Not enough diamonds - you have ${formatNumber(data.Diamonds)}.`
                  : null}
              />
            </div>
          {/if}
        </div>
      {/if}
    </header>

    {#if benchNudge}
      <p class="bench-nudge" data-testid="hall-bench-nudge">
        <strong>Your best blood is on the bench.</strong>
        {nameOf(benchNudge.best)} has an aptitude total of <strong>{aptitudeTotal(benchNudge.best)}</strong>;
        {nameOf(benchNudge.playing)}, playing in slot 1, has {aptitudeTotal(benchNudge.playing)}. Aptitudes only
        count on the character who plays - tap
        <button type="button" class="inline-link" onclick={() => (sheetId = benchNudge.best.CharacterId)}>{nameOf(benchNudge.best)}</button>
        to field them.
      </p>
    {/if}

    {#if freeExpeditionSlots > 0 && idleBench > 0}
      <p class="bench-nudge" data-testid="hall-expedition-nudge">
        <strong>{freeExpeditionSlots} expedition {freeExpeditionSlots === 1 ? 'slot stands' : 'slots stand'} empty</strong>
        while grown ancestors wait on the bench. Each trip brings back a loot chest.
        <button type="button" class="inline-link" onclick={() => requestScreen('expeditions')}>Send someone</button>
      </p>
    {/if}

    {#if lostNudge}
      <p class="bench-nudge lost" data-testid="hall-lost-nudge">
        <strong>Better blood than what you marked would be lost.</strong>
        {nameOf(lostNudge.best)} ({aptitudeTotal(lostNudge.best)}) does not carry through a rebirth, while
        {nameOf(lostNudge.weakest)} ({aptitudeTotal(lostNudge.weakest)}) is marked to keep. Unmark
        <button type="button" class="inline-link" onclick={() => (sheetId = lostNudge.weakest.CharacterId)}>{nameOf(lostNudge.weakest)}</button>
        and mark
        <button type="button" class="inline-link" onclick={() => (sheetId = lostNudge.best.CharacterId)}>{nameOf(lostNudge.best)}</button>,
        or
        <button type="button" class="swap" data-testid="hall-swap-marks" onclick={() => swapMarks(lostNudge.weakest, lostNudge.best)}>Swap the marks</button>
      </p>
    {/if}

    <!-- Modul: WHAT THE ROLLOVER ACTUALLY DOES, one tap away rather than three
         paragraphs above the list. A disclosure built on {#if}, not <details>
         (client_web/CLAUDE.md: a closed <details> does not reliably hide its
         content). Prose, not a list: exercise.mjs counts `.panel li` as roster
         rows. Vocabulary is docs/breeding_model.md section 0. -->
    <button
      type="button"
      class="disclose"
      aria-expanded={rulesOpen}
      data-testid="hall-rules-toggle"
      onclick={() => (rulesOpen = !rulesOpen)}
    >
      <span class="caret" aria-hidden="true">{rulesOpen ? '▾' : '▸'}</span> What survives a rebirth
    </button>
    {#if rulesOpen}
      <div class="rollover" data-testid="hall-rules">
        <p class="dim tiny">
          <strong>What carries:</strong> these ancestors &mdash; their aptitudes,
          genes, generation and epic mark &mdash; plus your village
          <em>buildings</em>, race masteries, diamonds and everything diamonds
          bought.
        </p>
        <p class="dim tiny">
          <strong>What a rebirth takes:</strong> every level (all back to 1, all
          adult), every piece of gear you do not put in the Vault, all gold and
          materials, the skill tree, placed attribute points, and the whole
          village gene pool &mdash; newcomers and elders alike. The next run's Inn
          deals a new hand. Pets, avatars and titles always stay. You choose when;
          the Rebirth screen shows what each rebirth pays and the exact terms first.
        </p>
        <p class="dim tiny">
          <strong>What the cull deletes:</strong> anybody past the
          {data?.Cap ?? 10} slots, permanently. Your first character always stays;
          then whoever you marked <em>Keep</em>; then the highest aptitude total,
          epic before ordinary, later generation before earlier. Marking more than
          the cap is allowed &mdash; the same ranking settles it, and a mark that
          loses says <em>Kept - over the cap</em>.
        </p>
        <!-- Modul: CHILDREN GROW UP ON THEIR OWN since 2026-11-13
             (ChildGrowth). Fielding used to be the only way a newborn aged. -->
        <p class="dim tiny">
          <strong>Fielding</strong> (tap a row) puts an ancestor to work in one of
          your slots. A bred child grows up on its own within an hour of birth,
          online or away, and can then be a parent itself. Adults age only while
          they work. How many slots you may use is set by the Town Hall.
        </p>
        {#if data}
          <p class="dim tiny">
            {#if data.NextSlotCostDiamonds > 0}
              {data.SlotsPurchased} of {data.MaxCap - (data.Cap - data.SlotsPurchased)} extra slots bought.
            {:else}
              Every extra slot bought.
            {/if}
            Slots survive a rebirth, like everything else diamonds buy.
            {#if data.GreatWorkSlots > 0}
              <span data-testid="hall-great-work-slot">+{data.GreatWorkSlots} slot from The Ebon Crown, above the diamond ceiling.</span>
            {/if}
          </p>
        {/if}
      </div>
    {/if}

    {#if hall.isPending}
      <Skeleton rows={4} />
    {:else if hall.isError}
      <p class="warn">The Hall could not be loaded.</p>
    {:else if data}
      <button
        type="button"
        class="section-head"
        aria-expanded={carriedOpen}
        data-testid="hall-carried-toggle"
        onclick={() => (carriedOpen = !carriedOpen)}
      >
        <span class="caret" aria-hidden="true">{carriedOpen ? '▾' : '▸'}</span>
        Carried through a rebirth ({sections.carried.length}/{data.Cap})
      </button>
      {#if carriedOpen}
        {@render columns()}
        <ul data-testid="hall-carried-list">
          {#each sections.carried as m (m.CharacterId)}{@render row(m)}{/each}
        </ul>
      {/if}

      {#if sections.lost.length > 0}
        <button
          type="button"
          class="section-head lost"
          aria-expanded={lostOpen}
          data-testid="hall-lost-toggle"
          onclick={() => (lostOpen = !lostOpen)}
        >
          <span class="caret" aria-hidden="true">{lostOpen ? '▾' : '▸'}</span>
          Lost at rebirth ({sections.lost.length})
          <span class="dim tiny">strongest first</span>
        </button>
        {#if lostOpen}
          {@render columns()}
          <ul data-testid="hall-lost-list">
            {#each sections.lost.slice(0, lostShown) as m (m.CharacterId)}{@render row(m)}{/each}
          </ul>
          {#if sections.lost.length > lostShown}
            <button
              type="button"
              class="more"
              data-testid="hall-lost-more"
              onclick={() => (lostShown += LOST_PAGE)}
            >
              Show {Math.min(LOST_PAGE, sections.lost.length - lostShown)} more
              <span class="dim tiny">({sections.lost.length - lostShown} hidden)</span>
            </button>
          {/if}
        {/if}
      {/if}
    {/if}
  </section>

  <!-- Rebirth moved to its own screen (owner, 2026-10-11). -->
  <section class="panel rebirth-link" data-testid="rebirth-link">
    <h2>Rebirth</h2>
    <p class="dim small">
      Start again at level 1, stronger: a higher bloodline ceiling, Legacy Shards for the Ancestral Boons, a
      Rebirth Trial if you want one, and skill points, avatars and the Phoenix along the way. The Vault keeps the
      gear you choose.
    </p>
    <button type="button" onclick={() => requestScreen('rebirth')}>Open Rebirth</button>
  </section>
</div>

{#if sheetMember && data}
  {@const m = sheetMember}
  {@const badge = hallBadge(m)}
  <DetailSheet title={nameOf(m)} onClose={() => (sheetId = null)} testid="hall-sheet">
    <div class="sheet-head">
      <RaceIcon raceId={m.RaceId} />
      <div class="who">
        <span>
          {raceName(m.RaceId)} {m.IsFemale ? 'woman' : 'man'} &middot; {generationTag(m)}
          {#if m.IsEpicMutation} &middot; <span class="epic">&#9733; epic mutation</span>{/if}
          {#if m.IsInbred} &middot; <span class="risk">inbred</span>{/if}
        </span>
        <span class="dim small">{parentage(m)}</span>
      </div>
    </div>

    <p class="fate {badge.kind}" data-testid="hall-sheet-fate">
      <span class="badge {badge.kind}">{badge.text}</span>
      {fateLine(m)}
    </p>

    <div class="sheet-apts">
      {#each APTITUDES as apt (apt.short)}
        <span><small>{apt.name}</small>{apt.of(m)}</span>
      {/each}
      <span class="sum"><small>Total</small>{aptitudeTotal(m)} / {APTITUDE_MAX * 4}</span>
    </div>

    {#if m.TraitMask > 0 && traitCatalogue.data}
      <div class="traits">
        {#each traitsOf(m.TraitMask, traitCatalogue.data) as trait (trait.Id)}<TraitBadge {trait} />{/each}
      </div>
    {:else}
      <p class="dim small">No traits.</p>
    {/if}

    <!-- Fielding. The whole point of breeding a child at the end of a season is
         to begin the next one as them.

         Modul: BUTTONS, not a <select>. At most three slots, and a native select
         on Android is a dialog that loses its choice when the list re-renders
         under it - the same defect that made the Breeding pickers unreliable in
         the APK. -->
    <div class="sheet-actions">
      {#if m.PlayableSlot >= 0}
        <span class="fielded">In slot {m.PlayableSlot + 1}</span>
      {:else if m.IsAway}
        <span class="dim small" data-testid="hall-away">Away on an expedition - back when it returns.</span>
      {:else}
        <span class="field" role="group" aria-label="Field {nameOf(m)} into a slot">
          <span class="dim small">Field into slot</span>
          {#each Array(data.PlayableSlots) as _, slot (slot)}
            <button type="button" class="field-slot" onclick={() => field(m, slot)}>{slot + 1}</button>
          {/each}
        </span>
      {/if}

      {#if m.IsMainCharacter}
        <span class="dim small">Always carries</span>
      {:else}
        <button type="button" class="keep sheet-keep" class:on={m.IsKept} aria-pressed={m.IsKept} onclick={() => mark(m)}>
          {m.IsKept ? 'Unmark' : 'Keep through rebirth'}
        </button>
      {/if}
    </div>
  </DetailSheet>
{/if}

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
  }

  /* Modul: bottom padding past the panel's corner bracket (an 18px background
     layer at 4px in, app.css) so the last row or button never sits on it. */
  .panel {
    padding: 1rem 1rem 1.6rem;
  }

  header {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.6rem;
  }

  header > div:first-child {
    flex: 1 1 16rem;
    min-width: 0;
  }

  h2 {
    margin: 0 0 0.2rem;
    font-size: 1.05rem;
  }

  header p {
    margin: 0;
    max-width: 52ch;
  }

  .counters {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: flex-end;
    gap: 0.35rem;
  }

  .tally {
    flex: none;
    padding: 0.15rem 0.5rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    color: var(--brass-lit);
    font-variant-numeric: tabular-nums;
    font-size: 0.85rem;
  }

  .tally.full,
  .tally.over {
    border-color: var(--warn);
    color: var(--warn);
  }

  .tally.kept {
    border-color: var(--border);
    color: var(--text-dim);
  }

  .tally.kept.over {
    border-color: var(--warn);
    color: var(--warn);
  }

  .buy {
    display: grid;
    gap: 0.15rem;
    justify-items: end;
  }

  /* :global - the slot purchase is a ConfirmButton, whose button lives in that
     component and is out of reach of a plain scoped selector. */
  .buy :global(button) {
    flex-shrink: 0;
    font-size: 0.8rem;
  }

  .disclose,
  .section-head,
  .more {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    width: 100%;
    margin-top: 0.6rem;
    padding: 0.35rem 0.5rem;
    font: inherit;
    font-size: 0.85rem;
    text-align: left;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background-image: none;
    box-shadow: none;
  }

  .section-head {
    margin-top: 0.9rem;
    font-weight: 700;
  }

  .section-head .dim {
    margin-left: auto;
    font-weight: 400;
  }

  .more {
    justify-content: center;
  }

  .caret {
    width: 0.8rem;
    color: var(--text-dim);
  }

  .rollover {
    display: grid;
    gap: 0.3rem;
    margin-top: 0.4rem;
    padding: 0.5rem 0.6rem;
    background: var(--bg);
    border-radius: var(--radius);
  }

  .rollover p {
    margin: 0;
    line-height: 1.4;
    max-width: 78ch;
    overflow-wrap: anywhere;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
  }

  li {
    display: flex;
    align-items: stretch;
    gap: 0.3rem;
    min-width: 0;
  }

  /* The row is one button (opens the sheet) and the pin beside it - a button
     cannot hold a button. */
  .open {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.3rem 0.5rem;
    font: inherit;
    text-align: left;
    color: inherit;
    background: var(--bg);
    background-image: none;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: none;
  }

  li[data-would-carry='false'] .open {
    border-style: dashed;
  }

  .who {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
    flex: 1 1 auto;
  }

  .name {
    font-weight: 700;
    font-size: 0.88rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .tags {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
  }

  .gen,
  .badge {
    font-size: 0.68rem;
    line-height: 1.3;
    padding: 0 0.3rem;
    border-radius: 3px;
    border: 1px solid var(--border);
    color: var(--text-dim);
    white-space: nowrap;
  }

  .badge.slot,
  .badge.first {
    border-color: var(--brass);
    color: var(--brass-lit);
  }

  /* A child on the bench: the next thing to do, not a warning. */
  .badge.raise {
    border-color: var(--good);
    color: var(--good);
  }

  /* The one state that must never read as safe. Words AND colour. */
  .badge.over-cap {
    border-color: var(--warn);
    color: var(--warn);
    font-weight: 700;
  }

  .epic {
    color: var(--brass-lit);
  }

  .risk {
    color: var(--danger);
  }

  /* Fixed-width numeric columns, so the header above lines up with every row. */
  .apts {
    display: flex;
    gap: 0.15rem;
    flex: none;
    font-variant-numeric: tabular-nums;
    font-size: 0.82rem;
  }

  .apts > span {
    width: 1.55rem;
    text-align: center;
    color: var(--brass-lit);
  }

  .apts > .sum {
    width: 2.4rem;
    color: var(--text);
    font-weight: 700;
  }

  .cols {
    display: flex;
    align-items: center;
    gap: 0.3rem;
    margin: 0.4rem 0 0.15rem;
    font-size: 0.66rem;
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  /* The header is laid out like a row: the open button's padding and border
     (0.5rem + 1px) on the right, then the pin column. */
  .cols-who {
    flex: 1 1 auto;
  }

  .cols .apts {
    font-size: inherit;
    margin-right: calc(0.5rem + 1px);
  }

  .cols .apts > span,
  .cols .apts > .sum {
    color: var(--text-dim);
    font-weight: 400;
  }

  .cols-pin {
    width: 2.2rem;
    text-align: center;
    flex: none;
  }

  .keep,
  .pin {
    flex: none;
    width: 2.2rem;
    display: grid;
    place-items: center;
    padding: 0;
    color: var(--text-dim);
    background: var(--bg);
    background-image: none;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: none;
  }

  .keep.on {
    color: var(--brass-lit);
    border-color: var(--brass);
  }

  .keep.over {
    color: var(--warn);
    border-color: var(--warn);
  }

  .pin.always {
    color: var(--brass-lit);
    border-style: dashed;
  }

  /* --- the sheet ----------------------------------------------------------- */

  .sheet-head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .sheet-head .who {
    font-size: 0.85rem;
  }

  .fate {
    margin: 0;
    font-size: 0.82rem;
    line-height: 1.4;
    display: grid;
    gap: 0.25rem;
    justify-items: start;
  }

  .fate.over-cap {
    color: var(--warn);
  }

  .sheet-apts {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    font-variant-numeric: tabular-nums;
  }

  .sheet-apts span {
    display: grid;
    min-width: 3.4rem;
    padding: 0.2rem 0.4rem;
    text-align: center;
    background: var(--bg);
    border-radius: 3px;
    color: var(--brass-lit);
  }

  .sheet-apts small {
    font-size: 0.6rem;
    color: var(--text-dim);
  }

  .traits {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem;
  }

  .sheet-actions {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .field {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
  }

  /* 44px both ways: a slot number is a one-character button, and the phone
     block in app.css only floors HEIGHT. */
  .field-slot {
    min-width: 44px;
    flex-shrink: 0;
    font-variant-numeric: tabular-nums;
  }

  .sheet-keep {
    width: auto;
    padding: 0.3rem 0.7rem;
    flex-shrink: 0;
  }

  .fielded {
    font-size: 0.8rem;
    color: var(--brass-lit);
    padding: 0.15rem 0.4rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
  }

  .small {
    font-size: 0.85rem;
  }

  .warn {
    color: var(--warn);
  }

  /* A phone row: portrait, a name, five numbers and the pin. The name column
     is the one that gives way (ellipsis), never the numbers. The portrait goes
     first - it is decoration beside a name that already says who. */
  @media (max-width: 40rem) {
    .open :global(.race) {
      display: none;
    }

    .apts > span {
      width: 1.35rem;
    }
  }

  .bench-nudge {
    margin: 0.25rem 0 0.5rem;
    padding: 0.5rem 0.75rem;
    border-left: 3px solid var(--accent, #d4a64a);
    background: color-mix(in srgb, var(--accent, #d4a64a) 10%, transparent);
    border-radius: 6px;
  }
  /* A link inside a sentence still needs a thumb-sized target (check:touch,
     390px): 44px tall via inline-flex, with the extra height taken back by
     negative margins so the line of text does not grow. */
  .inline-link {
    display: inline-flex;
    align-items: center;
    min-height: 44px;
    margin: -12px 0;
    padding: 0 2px;
    background: none;
    background-image: none;
    box-shadow: none;
    border: none;
    color: inherit;
    font: inherit;
    font-weight: 600;
    text-decoration: underline;
    cursor: pointer;
  }

  .swap {
    min-height: 44px;
    margin-left: 0.25rem;
  }
</style>
