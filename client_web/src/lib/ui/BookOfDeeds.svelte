<script lang="ts">
  // Modul: THE BOOK OF DEEDS, all five chapters.
  //
  // Chapter I is the onboarding: it replaces a three-step tooltip run that a
  // player could dismiss and never find again with something that stays put,
  // keeps count, and is still there tomorrow. Chapters II-V are what that
  // teaches into - the Forge, the hunt, the village, and the things only a
  // second season can reach.
  //
  // SERVER-DRIVEN, unlike the first pass. Completing a chapter awards a Seal
  // and a Seal grants +2 permanent skill points every season, forever, so the
  // deed definitions and the thresholds live on the server; this renders an
  // answer rather than computing one. See LONG_GAME_SPEC part 2.
  //
  // The list is always fully visible - finished deeds included. A checklist
  // that hides what you have done is a progress bar with extra steps, and
  // seeing four ticks above the one you are on is most of why a checklist
  // works at all.
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { queryKeys, fetchDeeds, type DeedEntry } from '../net/rest';
  import { requestScreen } from '../stores/navigation';
  import Skeleton from './Skeleton.svelte';

  const book = createQuery(() => ({
    // Refetched when the snapshot's logic epoch moves, so a deed finished
    // mid-session ticks over without a reload. The server banks Seals on this
    // read, which is also why it must not be cached indefinitely.
    queryKey: queryKeys.deeds,
    queryFn: fetchDeeds,
    staleTime: 20_000,
  }));

  const data = $derived(book.data);
  const chapters = $derived(data?.Chapters ?? []);

  // Modul: a Seal is a permanent, cross-season reward and the moment of
  // earning is most of it. The server reports which chapters were sealed by
  // THIS request, so the notice fires once rather than on every poll.
  let announced = $state(0);
  $effect(() => {
    const mask = data?.NewlySealedMask ?? 0;
    if (mask === 0 || mask === announced) return;
    announced = mask;

    for (const chapter of chapters) {
      if ((mask & (1 << (chapter.Index - 1))) === 0) continue;
      pushLocalNotice(
        `Seal earned - ${chapter.Title}. +${data?.SkillPointsPerSeal ?? 2} skill points, every season from now on.`,
      );
    }
  });

  // The one deed to point at: the first unfinished one of the first open
  // chapter. The ORDER is the teaching, so sending a player to "gather 100
  // wood" before they have won a fight would undo the point of having one.
  const upNext = $derived(
    chapters
      .filter((c) => c.IsOpen && !c.IsComplete)
      .flatMap((c) => c.Deeds)
      .find((d) => !d.Done) ?? null,
  );

  function doneCount(deeds: DeedEntry[]): number {
    return deeds.filter((d) => d.Done).length;
  }

  // The snapshot is what most deeds move with, so a change to it is the cue to
  // ask again. Cheap: one request per twenty seconds at worst.
  const epoch = $derived($playerState ? Number($playerState.LogicEpochCounter) : 0);
  $effect(() => {
    void epoch;
    book.refetch?.();
  });
</script>

<section class="panel deeds">
  <header>
    <div>
      <h3>The Book of Deeds</h3>
      <p class="dim small">
        {#if data && data.SealCount > 0}
          {data.SealCount} {data.SealCount === 1 ? 'Seal' : 'Seals'} &middot;
          <strong>+{data.SkillPointsFromSeals} skill points every season</strong>
        {:else}
          Each chapter you finish is a Seal, and every Seal is
          +{data?.SkillPointsPerSeal ?? 2} skill points a season, forever.
        {/if}
      </p>
    </div>
  </header>

  {#if book.isPending}
    <Skeleton rows={5} />
  {:else if book.isError}
    <p class="warn-line">The Book could not be read.</p>
  {:else}
    {#each chapters as chapter (chapter.Index)}
      <div class="chapter" class:locked={!chapter.IsOpen}>
        <div class="chead">
          <strong>{chapter.Title}</strong>
          <span class="dim tiny">
            {#if chapter.HasSeal}
              sealed &middot; {chapter.Reward}
            {:else if !chapter.IsOpen}
              opens when the chapter above is finished
            {:else}
              {chapter.Reward}
            {/if}
          </span>
          <span class="tally" class:complete={chapter.IsComplete}>
            {doneCount(chapter.Deeds)} / {chapter.Deeds.length}
          </span>
        </div>

        {#if chapter.IsOpen}
          <ol>
            {#each chapter.Deeds as deed (deed.Id)}
              {@const isNext = upNext?.Id === deed.Id}
              <li class:done={deed.Done} class:next={isNext}>
                <span class="mark" aria-hidden="true">{deed.Done ? '✓' : ''}</span>

                <div class="text">
                  <strong>{deed.Title}</strong>
                  <!-- The instruction is only useful while it is the thing to
                       do. Once done it is noise, and thirty paragraphs of
                       finished advice would bury the one line that matters. -->
                  {#if !deed.Done}
                    <p class="body">{deed.Body}</p>
                  {/if}

                  <!-- EVERY DEED SHOWS A NUMBER. The old tiered achievements
                       returned 0 from GetNextTierTarget for most ids and drew
                       "0 / MAX"; a deed without a number does not exist. -->
                  {#if deed.Target > 1 && !deed.Done}
                    <div class="meter" role="img" aria-label={`${deed.Current} of ${deed.Target}`}>
                      <span style={`width: ${(deed.Current / deed.Target) * 100}%`}></span>
                    </div>
                    <span class="count dim tiny">
                      {deed.Current.toLocaleString()} / {deed.Target.toLocaleString()}
                    </span>
                  {/if}
                </div>

                {#if !deed.Done}
                  <button class="go" onclick={() => requestScreen(deed.Screen)}>Go</button>
                {/if}
              </li>
            {/each}
          </ol>
        {/if}
      </div>
    {/each}
  {/if}
</section>

<style>
  .deeds {
    display: grid;
    gap: 0.6rem;
  }

  header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.6rem;
  }

  h3 {
    margin: 0 0 0.1rem;
  }

  header p {
    margin: 0;
    max-width: 46ch;
  }

  .chapter {
    border-top: 1px solid var(--border);
    padding-top: 0.5rem;
  }

  /* A locked chapter still shows its name and its price - knowing what is
     ahead is the point of a book with chapters. */
  .chapter.locked {
    opacity: 0.55;
  }

  .chead {
    display: grid;
    grid-template-columns: 1fr auto;
    align-items: baseline;
    gap: 0.2rem 0.5rem;
  }

  .chead .dim {
    grid-column: 1;
  }

  .chead .tally {
    grid-row: 1 / span 2;
    grid-column: 2;
  }

  .tally {
    padding: 0.1rem 0.45rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    font-variant-numeric: tabular-nums;
    font-size: 0.8rem;
    color: var(--text-dim);
    align-self: center;
  }

  .tally.complete {
    border-color: var(--brass);
    color: var(--brass-lit);
  }

  ol {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  li {
    display: flex;
    align-items: flex-start;
    gap: 0.5rem;
    padding: 0.35rem 0.5rem;
    border: 1px solid transparent;
    border-radius: var(--radius);
  }

  li.next {
    border-color: var(--brass);
    background: rgba(0, 0, 0, 0.04);
  }

  li.done .text strong {
    color: var(--text-dim);
    text-decoration: line-through;
  }

  .mark {
    width: 1rem;
    flex: none;
    color: var(--brass-lit);
  }

  .text {
    display: grid;
    gap: 0.15rem;
    min-width: 0;
    flex: 1;
  }

  .body {
    margin: 0;
    font-size: 0.8rem;
    color: var(--text-dim);
  }

  .meter {
    height: 4px;
    background: var(--bg);
    border-radius: 2px;
    overflow: hidden;
  }

  .meter span {
    display: block;
    height: 100%;
    background: var(--brass);
  }

  .count {
    font-variant-numeric: tabular-nums;
  }

  .go {
    flex: none;
    font: inherit;
    font-size: 0.75rem;
    padding: 0.2rem 0.5rem;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    cursor: pointer;
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

  .warn-line {
    color: var(--warn);
    font-size: 0.85rem;
  }
</style>
