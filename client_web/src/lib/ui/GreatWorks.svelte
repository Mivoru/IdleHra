<script lang="ts">
  // Task 84: the Great Works panel (Village). Five solo monuments, one per
  // region: each stage eats that region's own log and ore and pays a small
  // permanent bonus that survives a rebirth. Every word and number is the
  // server's (GreatWorksRegistry) - names, costs, per-stage bonuses and the two
  // ceilings arrive with the answer, and this file keeps no copy.
  import { createQuery } from '@tanstack/svelte-query';
  import { formatNumber } from './format';
  import { prettifyBaseId } from '../net/content';
  import { depositGreatWork } from '../net/commands';
  import { fetchGreatWorks, greatWorksKeys, isComplete, stageFraction, type GreatWork } from '../net/greatWorks';
  import { connectionStatus, pushLocalNotice } from '../stores/game';
  import MonumentGlyph from './MonumentGlyph.svelte';
  import DisabledReason from './DisabledReason.svelte';

  // Any command result invalidates every query (processCommandResults), so the
  // panel refetches by itself the moment a deposit is answered.
  const works = createQuery(() => ({
    queryKey: greatWorksKeys.all,
    queryFn: fetchGreatWorks,
    refetchInterval: 30_000,
  }));

  const live = $derived($connectionStatus.phase === 'live');

  function deposit(work: GreatWork, material: 0 | 1) {
    const outcome = depositGreatWork(work.Region, material, 0);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
  }

  /** Why a deposit button is off, in words - never a silent grey. */
  function blockedReason(work: GreatWork, held: number): string | null {
    if (isComplete(work)) return 'This Great Work is complete.';
    if (held <= 0) return 'You hold none of that material.';
    if (!live) return 'Waiting for the connection.';
    return null;
  }
</script>

<section class="panel great-works" data-testid="great-works">
  <div class="head">
    <h2>Great Works</h2>
    {#if works.data}
      <span class="dim tiny">permanent - they survive a rebirth</span>
    {/if}
  </div>

  {#if works.data}
    <p class="totals tiny" data-testid="great-works-totals">
      Now paying <strong>+{works.data.YieldPct}%</strong> gathering yield
      <span class="dim">(up to +{works.data.MaxYieldPct}%)</span> and
      <strong>+{works.data.OfflineMinutes} min</strong> offline limit
      <span class="dim">(up to +{works.data.MaxOfflineMinutes} min)</span>.
    </p>

    <ul class="works">
      {#each works.data.Works as work (work.Region)}
        {@const complete = isComplete(work)}
        {@const nextStage = work.Stages[work.Stage]}
        <li class="work" class:complete data-testid="great-work-{work.Region}">
          <MonumentGlyph stage={work.Stage} ghost size={44} label="{work.Name}, stage {work.Stage} of {work.Stages.length}" />
          <div class="body">
            <p class="title">
              <strong>{work.Name}</strong>
              <span class="dim tiny" data-testid="great-work-stage-{work.Region}">
                {complete ? 'complete' : `stage ${work.Stage} of ${work.Stages.length} built`}
              </span>
            </p>
            <p class="tiny dim">Each stage: {work.BonusPerStage}.</p>
            <p class="tiny" class:dim={!work.FrameOwned} data-testid="great-work-completion-{work.Region}">
              {work.FrameOwned ? 'Earned' : 'On completion'}: {work.CompletionReward}.
            </p>

            {#if !complete && nextStage}
              <p class="tiny">
                Next: <strong>{nextStage.Name}</strong> -
                <span data-testid="great-work-progress-{work.Region}">
                  {formatNumber(work.Progress)} / {formatNumber(work.NextCost)}
                </span>
                {prettifyBaseId(work.LogItem)} or {prettifyBaseId(work.OreItem)}
              </p>
              <div
                class="bar"
                role="progressbar"
                aria-label="{work.Name} stage progress"
                aria-valuemin="0"
                aria-valuemax="100"
                aria-valuenow={Math.round(stageFraction(work) * 100)}
              >
                <div class="fill" style="width: {stageFraction(work) * 100}%"></div>
              </div>
              <div class="actions">
                {#each [{ kind: 0 as const, item: work.LogItem, held: work.HeldLog, id: 'log' }, { kind: 1 as const, item: work.OreItem, held: work.HeldOre, id: 'ore' }] as opt (opt.id)}
                  {@const why = blockedReason(work, opt.held)}
                  <button
                    type="button"
                    class="deposit"
                    data-testid="great-work-deposit-{work.Region}-{opt.id}"
                    disabled={why !== null}
                    title={why ?? `Deposit ${prettifyBaseId(opt.item)}, as much as the stage still needs`}
                    onclick={() => deposit(work, opt.kind)}
                  >
                    Deposit {prettifyBaseId(opt.item)}
                    <span class="dim tiny">({formatNumber(opt.held)} held)</span>
                    <!-- Modul: the reason was only this button's title, and a
                         touch screen never shows the title of a DISABLED
                         button. "0 held" already says itself; the others
                         are printed in the button. -->
                    <DisabledReason text={why !== null && opt.held > 0 ? why : null} />
                  </button>
                {/each}
              </div>
            {/if}

            <ol class="stages" aria-label="Stages of {work.Name}">
              {#each work.Stages as stage (stage.Stage)}
                <li class:built={stage.Built} class:next={!stage.Built && stage.Stage === work.Stage + 1}>
                  <span class="tiny">{stage.Built ? '✓' : stage.Stage}</span>
                  <span class="tiny">{stage.Name}</span>
                  <span class="tiny dim">{formatNumber(stage.Cost)}</span>
                </li>
              {/each}
            </ol>
          </div>
        </li>
      {/each}
    </ul>
  {:else if works.isError}
    <p class="dim tiny">The Great Works could not be loaded just now.</p>
  {:else}
    <p class="dim tiny">Loading...</p>
  {/if}
</section>

<style>
  .head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.25rem 0.6rem;
  }

  .totals {
    margin: 0.4rem 0 0.7rem;
  }

  .works {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.9rem;
  }

  .work {
    display: flex;
    gap: 0.7rem;
    align-items: flex-start;
    padding-left: 0.5rem;
    border-left: 2px solid var(--border);
  }

  .work.complete {
    border-left-color: var(--good);
  }

  .body {
    min-width: 0;
    flex: 1 1 auto;
    display: grid;
    gap: 0.25rem;
  }

  .body p {
    margin: 0;
  }

  .title {
    display: flex;
    flex-wrap: wrap;
    gap: 0.1rem 0.6rem;
    align-items: baseline;
  }

  .bar {
    height: 6px;
    border-radius: 3px;
    background: var(--bg-sunken);
    overflow: hidden;
  }

  .fill {
    height: 100%;
    background: var(--accent);
    transition: width 400ms ease;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin-top: 0.15rem;
  }

  .deposit {
    /* A phone thumb: 44px, and free to wrap onto two lines instead of forcing
       the row wider than the panel. */
    min-height: 2.75rem;
    flex: 1 1 10rem;
    min-width: 0;
    flex-shrink: 0;
    text-align: left;
    overflow-wrap: anywhere;
  }

  .stages {
    list-style: none;
    margin: 0.2rem 0 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.8rem;
  }

  .stages li {
    display: flex;
    gap: 0.3rem;
    color: var(--text-dim);
  }

  .stages li.built {
    color: var(--good);
  }

  .stages li.next {
    color: var(--text);
  }

  @media (prefers-reduced-motion: reduce) {
    .fill {
      transition: none;
    }
  }
</style>
