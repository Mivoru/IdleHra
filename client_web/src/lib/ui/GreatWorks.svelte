<script lang="ts">
  // Task 84: the Great Works panel (Village). Five solo monuments, one per
  // region: each stage eats that region's own log and ore and pays a small
  // permanent bonus that survives a rebirth. Every word and number is the
  // server's (GreatWorksRegistry) - names, costs, per-stage bonuses and the two
  // ceilings arrive with the answer, and this file keeps no copy.
  //
  // Modul: ONE ROW PER MONUMENT (task 103). Five tall identical cards, each
  // repeating the whole stage ladder under a 0% bar that read as a rule, came
  // to about 1,500px - and for a new player all ten deposit buttons were
  // disabled "(0 held)". A row is the monument's name, five stage pips, the
  // current stage's "progress / cost" and one Deposit, which opens a sheet with
  // both materials, the ladder and what completion pays. The ladder's COSTS are
  // the same for every monument (GreatWorksRegistry.StageCosts), so they print
  // once above the rows; while nothing is held at all the panel is one line.
  import { createQuery } from '@tanstack/svelte-query';
  import { formatNumber } from './format';
  import { prettifyBaseId } from '../net/content';
  import { depositGreatWork } from '../net/commands';
  import { fetchGreatWorks, greatWorksKeys, isComplete, stageFraction, type GreatWork } from '../net/greatWorks';
  import { connectionStatus, pushLocalNotice } from '../stores/game';
  import MonumentGlyph from './MonumentGlyph.svelte';
  import DisabledReason from './DisabledReason.svelte';
  import DetailSheet from './DetailSheet.svelte';

  // Any command result invalidates every query (processCommandResults), so the
  // panel refetches by itself the moment a deposit is answered.
  const works = createQuery(() => ({
    queryKey: greatWorksKeys.all,
    queryFn: fetchGreatWorks,
    refetchInterval: 30_000,
  }));

  const live = $derived($connectionStatus.phase === 'live');

  // Keyed by region and re-read from the live query, so the sheet shows the
  // new progress the moment a deposit is answered.
  let sheetRegion = $state<number | null>(null);
  const sheetWork = $derived(
    sheetRegion === null ? null : (works.data?.Works.find((w) => w.Region === sheetRegion) ?? null),
  );

  let openAnyway = $state(false);

  /** Nothing to put in and nothing started: the panel is one line until there is. */
  const idle = $derived(
    works.data !== undefined &&
      works.data.Works.every((w) => w.HeldLog + w.HeldOre === 0 && w.Stage === 0 && w.Progress === 0),
  );

  /** The shared ladder, or null if the monuments ever stop sharing one. */
  const sharedLadder = $derived.by(() => {
    const list = works.data?.Works ?? [];
    if (list.length === 0) return null;
    const first = list[0].Stages.map((s) => s.Cost).join(',');
    return list.every((w) => w.Stages.map((s) => s.Cost).join(',') === first) ? list[0].Stages.map((s) => s.Cost) : null;
  });

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
      <span class="tiny" data-testid="great-works-totals">
        <strong>+{works.data.YieldPct}%</strong> yield
        <span class="dim">/ {works.data.MaxYieldPct}%</span> &middot;
        <strong>+{works.data.OfflineMinutes} min</strong> offline
        <span class="dim">/ {works.data.MaxOfflineMinutes}</span>
      </span>
    {/if}
  </div>

  {#if works.data}
    {#if idle && !openAnyway}
      <p class="dim tiny one-line">
        Five permanent monuments that survive a rebirth. You hold none of their
        logs or ore yet.
        <button type="button" class="link" onclick={() => (openAnyway = true)}>Show</button>
      </p>
    {:else}
      <p class="dim tiny ladder">
        Permanent - they survive a rebirth.
        {#if sharedLadder}
          Each has {sharedLadder.length} stages:
          <span class="costs">{sharedLadder.map((c) => formatNumber(c)).join(' · ')}</span>
          logs or ore.
        {/if}
      </p>

      <ul class="works">
        {#each works.data.Works as work (work.Region)}
          {@const complete = isComplete(work)}
          {@const held = work.HeldLog + work.HeldOre}
          <li class="work" class:complete data-testid="great-work-{work.Region}">
            <MonumentGlyph stage={work.Stage} ghost size={28} label="{work.Name}, stage {work.Stage} of {work.Stages.length}" />
            <div class="body">
              <strong class="name">{work.Name}</strong>
              <span class="line">
                <span class="pips" aria-hidden="true">
                  {#each work.Stages as stage (stage.Stage)}
                    <span class="pip" class:built={stage.Built}></span>
                  {/each}
                </span>
                <span class="tiny dim" data-testid="great-work-stage-{work.Region}">
                  {complete ? 'complete' : `stage ${work.Stage} of ${work.Stages.length} built`}
                </span>
              </span>
              {#if !complete}
                <span class="tiny num" data-testid="great-work-progress-{work.Region}">
                  {formatNumber(work.Progress)} / {formatNumber(work.NextCost)}
                </span>
              {/if}
            </div>
            <button
              type="button"
              class="open"
              class:ready={!complete && held > 0}
              data-testid="great-work-open-{work.Region}"
              onclick={() => (sheetRegion = work.Region)}
            >
              {complete ? 'Details' : 'Deposit'}
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  {:else if works.isError}
    <p class="dim tiny">The Great Works could not be loaded just now.</p>
  {:else}
    <p class="dim tiny">Loading...</p>
  {/if}
</section>

{#if sheetWork}
  {@const work = sheetWork}
  {@const complete = isComplete(work)}
  {@const nextStage = work.Stages[work.Stage]}
  <DetailSheet title={work.Name} onClose={() => (sheetRegion = null)} testid="great-work-sheet">
    <p class="tiny dim">Each stage: {work.BonusPerStage}.</p>
    <p class="tiny" class:dim={!work.FrameOwned} data-testid="great-work-completion-{work.Region}">
      {work.FrameOwned ? 'Earned' : 'On completion'}: {work.CompletionReward}.
    </p>

    {#if !complete && nextStage}
      <p class="tiny">
        Next: <strong>{nextStage.Name}</strong> -
        {formatNumber(work.Progress)} / {formatNumber(work.NextCost)}
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
            <!-- Modul: the reason was only this button's title, and a touch
                 screen never shows the title of a DISABLED button. "0 held"
                 already says itself; the others are printed in the button. -->
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
  </DetailSheet>
{/if}

<style>
  /* Modul: its own surface and padding. Village.svelte's `.panel` rule is
     scoped and never reached this component, so the last row sat on the
     corner bracket app.css draws in each corner. The bottom gap clears the
     bracket's 22px, and on a phone (where app.css forces 0.7rem) the last
     child carries it. */
  .great-works {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem 1rem 1.5rem;
  }

  @media (max-width: 40rem) {
    .great-works > :last-child {
      margin-bottom: 0.75rem;
    }
  }

  .head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.25rem 0.6rem;
  }

  h2 {
    margin: 0 0 0.3rem;
    font-size: 1.05rem;
  }

  p {
    margin: 0;
  }

  .one-line {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem;
  }

  .link {
    flex-shrink: 0;
    font-size: 0.8rem;
    padding: 0.15rem 0.6rem;
  }

  .ladder {
    margin: 0.2rem 0 0.5rem;
  }

  .costs {
    font-variant-numeric: tabular-nums;
  }

  .works {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.35rem;
  }

  .work {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    padding: 0.25rem 0 0.25rem 0.5rem;
    border-left: 2px solid var(--border);
  }

  .work.complete {
    border-left-color: var(--good);
  }

  .body {
    min-width: 0;
    flex: 1 1 auto;
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.1rem 0.6rem;
  }

  .name {
    font-size: 0.88rem;
    flex: 1 1 100%;
  }

  .line {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
  }

  .pips {
    display: inline-flex;
    gap: 3px;
  }

  .pip {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    border: 1px solid var(--brass);
  }

  .pip.built {
    background: var(--brass-lit);
  }

  .num {
    font-variant-numeric: tabular-nums;
  }

  .open {
    flex-shrink: 0;
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
  }

  /* Something can actually go in: the row's button says so. */
  .open.ready {
    border-color: var(--brass-lit);
    color: var(--brass-lit);
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
  }

  .deposit {
    /* A phone thumb: 44px, and free to wrap onto two lines instead of forcing
       the row wider than the sheet. */
    min-height: 2.75rem;
    flex: 1 1 10rem;
    min-width: 0;
    flex-shrink: 0;
    text-align: left;
    overflow-wrap: anywhere;
  }

  .stages {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
  }

  .stages li {
    display: grid;
    grid-template-columns: 1.2rem 1fr auto;
    gap: 0.4rem;
    color: var(--text-dim);
  }

  .stages li.built {
    color: var(--good);
  }

  .stages li.next {
    color: var(--text);
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }

  @media (prefers-reduced-motion: reduce) {
    .fill {
      transition: none;
    }
  }
</style>
