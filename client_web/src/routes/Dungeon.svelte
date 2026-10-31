<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { fetchDungeon, runDungeon, dungeonKeys, type DungeonRunAnswer, type DungeonInfo, type DungeonKind } from '../lib/net/dungeon';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import { pushLocalNotice } from '../lib/stores/game';
  import { requestScreen } from '../lib/stores/navigation';
  import { play } from '../lib/ui/audio';
  import QueryError from '../lib/ui/QueryError.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import DungeonRun from '../lib/ui/DungeonRun.svelte';

  // Modul: THREE GATES (owner, 2026-10-31). XP, Gold and Equipment side by
  // side, each with its own stage field under it; a stage opens when the one
  // before it is beaten. The server decides every blow and every reward - this
  // only asks which gate and which stage, then plays the answer back.
  const client = useQueryClient();
  const view = createQuery(() => ({ queryKey: dungeonKeys.view, queryFn: fetchDungeon }));
  const data = $derived(view.data ?? null);

  // Art and colour per gate, by the server's stable key.
  const GATE: Record<DungeonInfo['Key'], { art: string; tone: string; pays: string }> = {
    Xp: { art: '/dungeon/gate-xp.webp', tone: 'xp', pays: 'Experience' },
    Gold: { art: '/dungeon/gate-gold.webp', tone: 'gold', pays: 'Gold' },
    Equipment: { art: '/dungeon/gate-equipment.webp', tone: 'equipment', pays: 'Gear and a chest' },
  };

  /** The highest stage the player may enter in a dungeon. */
  function highestOpen(d: DungeonInfo): number {
    const open = d.Stages.filter((s) => s.LockedReason === '').map((s) => s.Stage);
    return open.length > 0 ? Math.max(...open) : 1;
  }

  // The chosen stage per gate, starting on the highest open one.
  let stageOf = $state<Record<number, number>>({});
  $effect(() => {
    if (!data) return;
    for (const d of data.Dungeons) if (stageOf[d.Kind] === undefined) stageOf[d.Kind] = highestOpen(d);
  });
  let selected = $state<DungeonKind>(1);
  const dungeon = $derived(data?.Dungeons.find((d) => d.Kind === selected) ?? null);
  const stage = $derived(dungeon?.Stages.find((s) => s.Stage === (stageOf[selected] ?? 1)) ?? null);

  function step(d: DungeonInfo, by: number) {
    const next = Math.min(highestOpen(d), Math.max(1, (stageOf[d.Kind] ?? 1) + by));
    stageOf[d.Kind] = next;
    selected = d.Kind;
  }

  let running = $state(false);
  let answer = $state<DungeonRunAnswer | null>(null);
  let runOf = $state<DungeonInfo | null>(null);

  async function enter() {
    if (!dungeon || !stage || running) return;
    running = true;
    answer = null;
    try {
      const reply = await runDungeon(dungeon.Kind, stage.Stage);
      if (!reply) return;
      if (reply.Outcome !== 'Ok' || !reply.Result) {
        pushLocalNotice(reply.Reason || 'The run could not start.', 'error');
        client.invalidateQueries({ queryKey: dungeonKeys.view });
        return;
      }
      play('delveDoor');
      runOf = dungeon;
      answer = reply;
      client.invalidateQueries({ queryKey: dungeonKeys.view });
      if (reply.Reward?.PieceId || reply.Reward?.ChestId) invalidateOwnedItems(client);
    } catch (e) {
      const status = (e as { status?: number }).status;
      pushLocalNotice(status === 409 ? 'Your character is not online - reconnect and try again.' : 'The run could not start. Nothing was spent.', 'error');
    } finally {
      running = false;
    }
  }
</script>

<div class="wrap">
  {#if answer?.Result && runOf}
    <DungeonRun {answer} dungeon={runOf} onclose={() => (answer = null)} />
  {:else}
  <section class="panel lobby" data-testid="dungeon">
    <header>
      <h2>Dungeons</h2>
      <p class="dim small">
        Five waves and a boss, one after another. Your health carries from fight to fight and the larder gives at most
        {data?.MealCap ?? 5} meals a run. {data ? `${data.TicketsPerDay} tickets a day for each gate, at most ${data.TicketCap}.` : ''}
      </p>
    </header>

    {#if view.isError && !data}
      <QueryError query={view} what="the dungeons" />
    {:else if !data}
      <Skeleton rows={4} />
    {:else if !data.Open}
      <p class="locked" data-testid="dungeon-locked">{data.LockedReason}</p>
      <button onclick={() => requestScreen('combat')}>Go fight</button>
    {:else}
      <p class="pick dim tiny">Pick a gate, then its stage underneath.</p>
      <div class="gates" role="radiogroup" aria-label="Dungeon">
        {#each data.Dungeons as d (d.Kind)}
          {@const g = GATE[d.Key]}
          {@const at = stageOf[d.Kind] ?? 1}
          <div class="gate-col" data-tone={g.tone}>
            <button
              class="gate"
              class:on={selected === d.Kind}
              role="radio"
              aria-checked={selected === d.Kind}
              data-testid="dungeon-gate-{d.Key.toLowerCase()}"
              style="background-image: url({g.art})"
              onclick={() => (selected = d.Kind)}
            >
              <span class="gate-name">{d.Name}</span>
              <span class="gate-pays">{g.pays}</span>
              <span class="gate-tickets" title="{d.Tickets} of {data.TicketCap} tickets" data-testid="dungeon-tickets-{d.Key.toLowerCase()}">
                {#each Array(data.TicketCap) as _, i (i)}<i class:full={i < d.Tickets}></i>{/each}
              </span>
            </button>
            <div class="stage-field" aria-label="{d.Name} stage">
              <button class="arrow" aria-label="Lower stage" disabled={at <= 1} onclick={() => step(d, -1)}>&lsaquo;</button>
              <button class="stage-num" data-testid="dungeon-stage-{d.Key.toLowerCase()}" data-band={d.Stages[at - 1]?.Band}
                aria-label="Stage {at}" onclick={() => (selected = d.Kind)}>
                <strong>{at}</strong>
              </button>
              <button class="arrow" aria-label="Higher stage" disabled={at >= highestOpen(d)} onclick={() => step(d, 1)}>&rsaquo;</button>
            </div>
          </div>
        {/each}
      </div>

      {#if dungeon && stage}
        <div class="detail" data-tone={GATE[dungeon.Key].tone}>
          <p class="band dim tiny">
            {dungeon.Name} &middot; {stage.Band} band &middot; stage {stage.Stage}{stage.Cleared ? ' · cleared' : ''} &middot; best {dungeon.BestStage}
          </p>
          <p class="small">{stage.Reward}{stage.FirstClearPaid ? ' (first clear already paid)' : ''}</p>
          <h3>{dungeon.Boss} at this stage</h3>
          <ul class="abilities">
            {#each stage.Abilities as a (a.Id)}
              <li class:inactive={!a.Active}>
                <strong>{a.Name}</strong>
                {#if a.Active}<span>{a.Line}</span>{:else}<span class="dim">From stage {a.FromStage}.</span>{/if}
              </li>
            {/each}
          </ul>
          <button
            class="primary enter"
            data-testid="dungeon-enter"
            disabled={running || (dungeon.Tickets <= 0 && !data.FreeRuns) || stage.LockedReason !== ''}
            onclick={enter}
          >{running ? 'Entering...' : data.FreeRuns ? `Enter stage ${stage.Stage} (admin test, free)` : dungeon.Tickets > 0 ? `Enter stage ${stage.Stage} (1 ticket)` : 'No tickets until midnight UTC'}</button>
        </div>
      {/if}
    {/if}
  </section>
  {/if}
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
    max-width: 60rem;
    margin: 0 auto;
  }
  h2 {
    margin: 0 0 0.3rem;
    font-size: 1.1rem;
  }
  h3 {
    margin: 0.8rem 0 0.3rem;
    font-size: 0.85rem;
  }
  .small {
    font-size: 0.82rem;
    margin: 0 0 0.6rem;
  }
  .tiny {
    font-size: 0.68rem;
  }
  .locked {
    font-weight: 600;
  }
  .pick {
    margin: 0.4rem 0 0;
  }

  /* Tones: one colour per gate, matching its art. */
  [data-tone='xp'] {
    --tone: #4fd1a5;
  }
  [data-tone='gold'] {
    --tone: #f0a640;
  }
  [data-tone='equipment'] {
    --tone: #b685f0;
  }

  .gates {
    display: grid;
    /* Three equal gates; on a wide screen they stop at a size that still
       reads as a doorway rather than a poster. */
    grid-template-columns: repeat(3, minmax(0, 14rem));
    justify-content: center;
    gap: 0.6rem;
    margin: 0.6rem 0 0.8rem;
  }
  .gate-col {
    display: grid;
    gap: 0.4rem;
    min-width: 0;
  }
  .gate {
    position: relative;
    aspect-ratio: 2 / 3;
    width: 100%;
    padding: 0;
    border: 2px solid var(--border);
    border-radius: 10px;
    overflow: hidden;
    background-size: cover;
    background-position: center;
    display: grid;
    grid-template-rows: auto 1fr auto auto;
    text-align: center;
    color: #fff;
    transition: transform 0.15s ease, border-color 0.15s ease, box-shadow 0.15s ease;
  }
  .gate::before {
    content: '';
    position: absolute;
    inset: 0;
    background: linear-gradient(to bottom, rgb(0 0 0 / 0.7), transparent 30%, transparent 62%, rgb(0 0 0 / 0.85));
    pointer-events: none;
  }
  .gate > * {
    position: relative;
  }
  .gate.on {
    border-color: var(--tone);
    box-shadow: 0 0 18px -2px var(--tone);
    transform: translateY(-2px);
  }
  .gate:not(.on) {
    filter: saturate(0.7) brightness(0.85);
  }
  .gate-name {
    padding: 0.45rem 0.3rem 0;
    font-weight: 800;
    font-size: clamp(0.72rem, 2.6vw, 0.95rem);
    line-height: 1.15;
    text-shadow: 0 1px 3px #000;
  }
  .gate-pays {
    grid-row: 3;
    font-size: clamp(0.62rem, 2.2vw, 0.78rem);
    color: var(--tone);
    font-weight: 700;
    text-shadow: 0 1px 3px #000;
  }
  .gate-tickets {
    grid-row: 4;
    display: flex;
    justify-content: center;
    gap: 3px;
    padding: 0.25rem 0 0.45rem;
  }
  .gate-tickets i {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    border: 1px solid var(--tone);
  }
  .gate-tickets i.full {
    background: var(--tone);
  }

  .stage-field {
    display: grid;
    grid-template-columns: 44px minmax(0, 1fr) 44px;
    align-items: stretch;
    border: 1px solid var(--border);
    border-radius: 8px;
    overflow: hidden;
  }
  .stage-field .arrow {
    min-height: 44px;
    border: 0;
    border-radius: 0;
    font-size: 1.3rem;
    padding: 0;
  }
  .stage-num {
    border: 0;
    border-radius: 0;
    padding: 0;
    display: grid;
    place-items: center;
    line-height: 1;
    min-height: 44px;
    min-width: 0;
  }
  .stage-num strong {
    font-size: 1.15rem;
    font-variant-numeric: tabular-nums;
  }
  .stage-num[data-band='Ascension'] strong {
    color: var(--accent);
  }
  .stage-num[data-band='Beyond'] strong {
    color: var(--danger);
  }

  .detail {
    border-top: 2px solid var(--tone);
    padding-top: 0.6rem;
  }
  .abilities {
    list-style: none;
    margin: 0 0 0.8rem;
    padding: 0;
    display: grid;
    gap: 0.35rem;
    font-size: 0.82rem;
  }
  .abilities li {
    display: grid;
    gap: 0.1rem;
  }
  .abilities li.inactive {
    opacity: 0.6;
  }
  .enter {
    width: 100%;
    min-height: 44px;
  }
</style>
