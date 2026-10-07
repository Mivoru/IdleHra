<script lang="ts">
  // Modul: THE QUEST LINE (owner, 2026-10-07) - ten acts a player is MADE to
  // try once. A friend played for weeks without learning that fusion or affix
  // reroll existed, and the tiers that already teach (tutorialSteps.ts,
  // tutorialDiscoveries.ts, tutorialObjectives.ts) all fire on being READ, so a
  // feature can be explained to a player who never tries it.
  //
  // This panel is the server's quest list, drawn. It decides nothing:
  //   - which steps exist, their order, words, target screens and `data-guide`
  //     targets come from GET /api/v1/quests (QuestLineRegistry);
  //   - a step is "done" only when the server saw the act HAPPEN, from durable
  //     facts - so a veteran's old fusions tick themselves off;
  //   - "Claim" sends a step id and nothing else; the server re-checks the step,
  //     pays gold and the region's own materials once, and answers with the new
  //     list. A double press pays once.
  //
  // PERSISTENT AND SKIPPABLE: the panel folds to its header and the fold is
  // remembered, but folding it skips nothing - a step the player walks past
  // stays in the list until it is done, and the panel retires itself only when
  // every step has been claimed.
  //
  // TIER ONE STILL WINS. While the first three instructions are outstanding
  // (a brand-new account) the panel shows its header and a line saying when it
  // opens, instead of a second set of instructions over the guided first
  // minute. The quest line also REPLACED three tier-three objectives that
  // pointed at the same acts - see tutorialObjectives.ts.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { claimQuest, fetchQuests, questKeys, type QuestLine, type QuestStep } from '../net/quests';
  import { invalidateOwnedItems } from '../net/queryClient';
  import { prettifyBaseId } from '../net/content';
  import { pushLocalNotice } from '../stores/game';
  import { tutorialPrompt } from '../stores/tutorial';
  import { questSpotlight, showMe, clearSpotlight } from '../stores/questSpotlight';
  import { claimableSteps, describeReward, nextQuest, questLineFinished, questSummary } from './questLine';
  import Bar from './Bar.svelte';

  const client = useQueryClient();

  const quests = createQuery(() => ({
    queryKey: questKeys.all,
    queryFn: fetchQuests,
    refetchInterval: 20_000,
    // Modul: a step is finished on another screen and the next place the player
    // looks is Home; the client-wide staleTime would keep the old answer across
    // that step (the Great Works card on the map says the same).
    refetchOnMount: 'always',
  }));

  const line = $derived(quests.data ?? null);
  const finished = $derived(questLineFinished(line));
  const tierOneOutstanding = $derived($tutorialPrompt !== null);
  const next = $derived(nextQuest(line));
  const waiting = $derived(claimableSteps(line).length);

  const FOLD_KEY = 'folkidle.questPanelFolded';
  function readFolded(): boolean {
    try {
      return localStorage.getItem(FOLD_KEY) === '1';
    } catch {
      return false;
    }
  }
  let folded = $state(readFolded());
  let showAll = $state(false);
  let claiming = $state('');

  function toggleFold() {
    folded = !folded;
    try {
      localStorage.setItem(FOLD_KEY, folded ? '1' : '0');
    } catch {
      // Storage refused: the fold simply does not outlive the session.
    }
  }

  const FAILURE_TEXT: Record<string, string> = {
    Locked: 'That step is not open yet.',
    NotDone: 'Do the step first, then claim it.',
    AlreadyClaimed: 'That reward was already claimed.',
    Restricted: 'This account cannot claim rewards right now.',
    UnknownStep: 'That step no longer exists.',
    PlayerNotFound: 'Could not find your account.',
  };

  async function claim(step: QuestStep) {
    if (claiming) return;
    claiming = step.Id;
    try {
      const answer: QuestLine = await claimQuest(step.Id);
      client.setQueryData(questKeys.all, answer);
      if (answer.Result === 'Ok') {
        invalidateOwnedItems(client);
        pushLocalNotice(`Quest reward: ${describeReward(answer.Reward, prettifyBaseId)}.`, 'info');
      } else {
        pushLocalNotice(FAILURE_TEXT[answer.Result ?? ''] ?? 'The reward was not paid.', 'error');
      }
    } catch {
      pushLocalNotice('The reward could not be claimed. Nothing was lost - try again.', 'error');
    } finally {
      claiming = '';
    }
  }

  function mark(state: QuestStep['State']): string {
    return state === 'claimed' ? '✓' : state === 'done' ? '!' : state === 'locked' ? '•' : '▸';
  }

  function stateText(step: QuestStep): string {
    if (step.State === 'claimed') return 'Claimed';
    if (step.State === 'done') return 'Done - reward waiting';
    if (step.State === 'locked') return `Locked: ${step.UnlockHint}`;
    return 'To do';
  }
</script>

{#if quests.isError && !line}
  <section class="panel quests" data-testid="quest-panel-error">
    <strong>Quest line</strong>
    <p class="dim small">The quest line could not be loaded.</p>
    <button class="quiet" onclick={() => quests.refetch()}>Try again</button>
  </section>
{:else if line && !finished}
  <section class="panel quests" data-testid="quest-panel" aria-label="Quest line">
    <button
      class="head"
      aria-expanded={!folded}
      title={folded ? 'Show the quest line' : 'Hide the quest line'}
      data-testid="quest-fold"
      onclick={toggleFold}
    >
      <strong>Quest line</strong>
      <span class="dim tiny" data-testid="quest-summary">{questSummary(line)}</span>
      {#if folded && waiting > 0}<span class="badge" data-testid="quest-badge">{waiting}</span>{/if}
      <svg class="caret" class:up={folded} viewBox="0 0 12 12" aria-hidden="true">
        <path d="M2 4.5 L6 8.5 L10 4.5" fill="none" stroke="currentColor" stroke-width="1.8"
              stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <Bar value={line.Done} max={line.Total} size="sm" tone="brass" ariaLabel="Quest line progress" />

    {#if !folded}
      {#if tierOneOutstanding}
        <p class="dim small" data-testid="quest-waiting">
          The quest line opens once you have finished the first steps above - a fed larder, a worn weapon and your first fight.
        </p>
      {:else}
        {#if next}
          <div class="next" data-testid="quest-next" data-step={next.Id} data-state={next.State}>
            <div class="what">
              <strong>{next.Title}</strong>
              <p class="dim small">{next.Explanation}</p>
              {#if next.State === 'locked'}
                <p class="hint small" data-testid="quest-unlock-hint">Opens when: {next.UnlockHint}</p>
              {:else if next.State === 'done'}
                <p class="reward small">Reward: {describeReward(line.Reward, prettifyBaseId)}</p>
              {/if}
            </div>
            <div class="acts">
              {#if next.State === 'done'}
                <button class="primary" data-testid="quest-claim" disabled={claiming !== ''} onclick={() => claim(next)}>Claim</button>
              {:else if next.State === 'available'}
                <button class="primary" data-testid="quest-show" onclick={() => showMe(next)}>Show me</button>
              {/if}
              {#if $questSpotlight}
                <button class="quiet" onclick={clearSpotlight}>Stop highlighting</button>
              {/if}
            </div>
          </div>
        {/if}

        <button class="linkish" aria-expanded={showAll} data-testid="quest-all-toggle" onclick={() => (showAll = !showAll)}>
          {showAll ? 'Hide all steps' : `All ${line.Total} steps`}
        </button>

        {#if showAll}
          <ol class="steps" data-testid="quest-steps">
            {#each line.Steps as step (step.Id)}
              <li class="step {step.State}" data-testid="quest-step-{step.Id}" data-state={step.State}>
                <span class="mark" aria-hidden="true">{mark(step.State)}</span>
                <div class="body">
                  <strong>{step.Order}. {step.Title}</strong>
                  <span class="dim tiny">{stateText(step)}</span>
                </div>
                {#if step.State === 'available'}
                  <button data-testid="quest-show-{step.Id}" onclick={() => showMe(step)}>Show me</button>
                {:else if step.State === 'done'}
                  <button class="primary" data-testid="quest-claim-{step.Id}" disabled={claiming !== ''} onclick={() => claim(step)}>Claim</button>
                {/if}
              </li>
            {/each}
          </ol>
        {/if}
      {/if}
    {/if}
  </section>
{/if}

<style>
  .quests {
    display: grid;
    gap: 0.5rem;
    max-width: 40rem;
    margin: 0 auto 1rem;
  }

  /* The whole header is the toggle, one target (the coach panel's lesson). */
  .head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
    width: 100%;
    text-align: left;
    background: none;
    border: none;
    box-shadow: none;
    padding: 0;
    color: inherit;
  }

  .badge {
    min-width: 1.25rem;
    padding: 0 0.35rem;
    text-align: center;
    border-radius: 999px;
    background: var(--accent);
    color: var(--bg);
    font-size: var(--fs-xs);
    font-weight: var(--fw-medium);
  }

  .caret {
    width: 12px;
    height: 12px;
    margin-left: auto;
    transition: rotate 140ms ease;
  }

  .caret.up {
    rotate: 180deg;
  }

  .next {
    display: flex;
    gap: 0.75rem;
    align-items: flex-start;
    justify-content: space-between;
    flex-wrap: wrap;
  }

  .what {
    flex: 1 1 14rem;
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .what p {
    margin: 0.2rem 0 0;
  }

  .acts {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .hint {
    color: var(--warn);
  }

  .reward {
    color: var(--gold);
  }

  .linkish {
    justify-self: start;
    background: none;
    border: none;
    box-shadow: none;
    padding: 0.2rem 0;
    color: var(--accent);
    text-decoration: underline;
  }

  .steps {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }

  .step {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  .step .body {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    overflow-wrap: anywhere;
  }

  .step .mark {
    width: 1.2rem;
    text-align: center;
    color: var(--text-dim);
  }

  .step.claimed .mark {
    color: var(--good);
  }

  .step.done .mark {
    color: var(--gold);
    font-weight: var(--fw-medium);
  }

  .step.locked {
    opacity: 0.7;
  }
</style>
