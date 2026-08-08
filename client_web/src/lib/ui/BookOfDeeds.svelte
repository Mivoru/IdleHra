<script lang="ts">
  // Modul: Chapter I of the Book of Deeds, drawn.
  //
  // This is the onboarding. It replaces a three-step tooltip run that a player
  // could dismiss and never find again with something that stays put, keeps
  // count, and is still there tomorrow. See LONG_GAME_SPEC part 2.
  //
  // The list is always fully visible - finished deeds included. A checklist
  // that hides what you have done is a progress bar with extra steps, and
  // seeing four ticks above the one you are on is most of why a checklist
  // works at all.
  import { playerState } from '../stores/game';
  import { chapterOneState, nextDeed, CHAPTER_ONE } from '../stores/chapterOne';
  import { requestScreen } from '../stores/navigation';

  const deeds = $derived(chapterOneState($playerState));
  const upNext = $derived(nextDeed($playerState));
  const done = $derived(deeds.filter((d) => d.done).length);
  const complete = $derived(done === CHAPTER_ONE.length);
</script>

<section class="panel deeds">
  <header>
    <div>
      <h3>The Village Road</h3>
      <p class="dim small">Chapter I of the Book of Deeds</p>
    </div>
    <span class="tally" class:complete>{done} / {CHAPTER_ONE.length}</span>
  </header>

  {#if complete}
    <p class="finished">
      Every deed of the first chapter is done. You have touched every loop the
      game has - the rest is depth.
    </p>
  {/if}

  <ol>
    {#each deeds as deed (deed.id)}
      {@const isNext = upNext?.id === deed.id}
      <li class:done={deed.done} class:next={isNext}>
        <span class="mark" aria-hidden="true">{deed.done ? '✓' : ''}</span>

        <div class="text">
          <strong>{deed.title}</strong>
          <!-- The instruction is only useful while it is the thing to do. Once
               done it is noise, and six paragraphs of finished advice would
               bury the one line that still matters. -->
          {#if !deed.done}
            <p class="body">{deed.body}</p>
          {/if}

          {#if deed.target > 1 && !deed.done}
            <div class="meter" role="img" aria-label={`${deed.current} of ${deed.target}`}>
              <span style={`width: ${(deed.current / deed.target) * 100}%`}></span>
            </div>
            <span class="count dim tiny">{deed.current} / {deed.target}</span>
          {/if}
        </div>

        {#if !deed.done}
          <button class="go" onclick={() => requestScreen(deed.screen)}>Go</button>
        {/if}
      </li>
    {/each}
  </ol>
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
    margin: 0;
  }

  header p {
    margin: 0;
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

  .tally.complete {
    background: rgba(216, 180, 90, 0.16);
  }

  .finished {
    margin: 0;
    padding: 0.5rem 0.6rem;
    border-left: 2px solid var(--brass);
    background: rgba(216, 180, 90, 0.08);
    font-size: 0.87rem;
  }

  ol {
    display: grid;
    gap: 0.35rem;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    display: flex;
    align-items: flex-start;
    gap: 0.55rem;
    padding: 0.5rem 0.55rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    /* min-width:0 so a long body wraps instead of pushing the Go button off a
       narrow screen - 1fr and flex both refuse to shrink below content. */
    min-width: 0;
  }

  /* The one to do next is the only one that draws the eye. */
  li.next {
    border-color: var(--brass);
    background: rgba(216, 180, 90, 0.07);
  }

  li.done {
    opacity: 0.62;
  }

  .mark {
    flex: none;
    display: grid;
    place-items: center;
    width: 1.3rem;
    height: 1.3rem;
    border: 1px solid var(--border);
    border-radius: 50%;
    font-size: 0.8rem;
    line-height: 1;
  }

  li.done .mark {
    border-color: var(--brass);
    background: var(--brass);
    color: #21180a;
  }

  .text {
    display: grid;
    gap: 0.2rem;
    min-width: 0;
  }

  .body {
    margin: 0;
    font-size: 0.83rem;
    color: var(--text-dim);
  }

  .meter {
    height: 5px;
    border-radius: 3px;
    background: var(--bg);
    overflow: hidden;
  }

  .meter span {
    display: block;
    height: 100%;
    background: var(--brass-lit);
  }

  .count {
    font-variant-numeric: tabular-nums;
  }

  .go {
    flex: none;
    align-self: center;
    padding: 0.25rem 0.6rem;
    font-size: 0.8rem;
  }
</style>
