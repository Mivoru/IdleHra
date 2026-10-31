<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { fetchDungeon, runDungeon, dungeonKeys, type DungeonRunAnswer, type DungeonEvent } from '../lib/net/dungeon';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import { prettifyBaseId } from '../lib/net/content';
  import { pushLocalNotice } from '../lib/stores/game';
  import { requestScreen } from '../lib/stores/navigation';
  import { play } from '../lib/ui/audio';
  import QueryError from '../lib/ui/QueryError.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  // Modul: THE EQUIPMENT DUNGEON (owner D1-D6, 2026-10-31). A run is a
  // decision (which stage) and a result, not a reflex test: the server
  // simulates five waves and Baba Yaga with the live character and answers
  // with what happened, which this plays back line by line. The client never
  // says who won - it only shows the server's answer.
  const client = useQueryClient();
  const view = createQuery(() => ({ queryKey: dungeonKeys.view, queryFn: fetchDungeon }));
  const data = $derived(view.data ?? null);

  let chosen = $state(1);
  $effect(() => {
    // Start on the highest stage the player may enter.
    if (data && chosen === 1) {
      const open = data.Stages.filter((s) => s.LockedReason === '').map((s) => s.Stage);
      if (open.length > 0) chosen = Math.max(...open);
    }
  });
  const stage = $derived(data?.Stages.find((s) => s.Stage === chosen) ?? null);

  let running = $state(false);
  let answer = $state<DungeonRunAnswer | null>(null);
  let shown = $state<DungeonEvent[]>([]);
  let playback: ReturnType<typeof setInterval> | null = null;
  const done = $derived(answer?.Result != null && shown.length === answer.Result.Events.length);

  function stopPlayback() {
    if (playback) clearInterval(playback);
    playback = null;
  }

  async function enter() {
    if (!stage || running) return;
    running = true;
    stopPlayback();
    shown = [];
    answer = null;
    try {
      const reply = await runDungeon(stage.Stage);
      if (!reply) return;
      if (reply.Outcome !== 'Ok' || !reply.Result) {
        pushLocalNotice(reply.Reason || 'The run could not start.', 'error');
        client.invalidateQueries({ queryKey: dungeonKeys.view });
        return;
      }
      answer = reply;
      const events = reply.Result.Events;
      // Playback: a line every 350 ms, so a run reads like a fight, not a log.
      playback = setInterval(() => {
        if (shown.length >= events.length) {
          stopPlayback();
          play(reply.Result!.Won ? 'chestOpenLegendary' : 'error');
          return;
        }
        shown = [...shown, events[shown.length]];
      }, 350);
      client.invalidateQueries({ queryKey: dungeonKeys.view });
      if (reply.Reward) invalidateOwnedItems(client);
    } catch (e) {
      const status = (e as { status?: number }).status;
      pushLocalNotice(status === 409 ? 'Your character is not online - reconnect and try again.' : 'The run could not start. Nothing was spent.', 'error');
    } finally {
      running = false;
    }
  }

  function skip() {
    if (!answer?.Result) return;
    stopPlayback();
    shown = answer.Result.Events;
  }

  $effect(() => () => stopPlayback());
</script>

<div class="wrap">
  <section class="panel lobby" data-testid="dungeon">
    <header>
      <div>
        <h2>The Equipment Dungeon</h2>
        <p class="dim small">
          Five waves and Baba Yaga, one after another - your health carries from fight to fight, and the
          larder gives at most {data?.MealCap ?? 5} meals a run. Win and she drops a piece of gear and a
          loot chest.
        </p>
      </div>
      {#if data}
        <span class="tickets" data-testid="dungeon-tickets" title="{data.TicketsPerDay} a day, at most {data.TicketCap}">
          {data.Tickets} / {data.TicketCap} tickets
        </span>
      {/if}
    </header>

    {#if view.isError && !data}
      <QueryError query={view} what="the dungeon" />
    {:else if !data}
      <Skeleton rows={4} />
    {:else if !data.Open}
      <p class="locked" data-testid="dungeon-locked">{data.LockedReason}</p>
      <button onclick={() => requestScreen('combat')}>Go fight</button>
    {:else}
      <div class="stages" role="radiogroup" aria-label="Stage">
        {#each data.Stages as s (s.Stage)}
          <button
            role="radio"
            aria-checked={chosen === s.Stage}
            class="stage"
            class:on={chosen === s.Stage}
            class:cleared={s.Cleared}
            data-band={s.Band}
            data-testid="dungeon-stage-{s.Stage}"
            disabled={s.LockedReason !== ''}
            title={s.LockedReason || `${s.Band} band`}
            onclick={() => (chosen = s.Stage)}
          >{s.Stage}</button>
        {/each}
      </div>

      {#if stage}
        <div class="detail">
          <p class="band dim tiny">{stage.Band} band &middot; stage {stage.Stage}{stage.Cleared ? ' · cleared' : ''}</p>
          <p class="small">{stage.Reward}{stage.FirstClearPaid ? ' (first clear already paid)' : ''}</p>
          <h3>{data.Boss} at this stage</h3>
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
            disabled={running || data.Tickets <= 0 || stage.LockedReason !== ''}
            onclick={enter}
          >{running ? 'Fighting...' : data.Tickets > 0 ? `Enter stage ${stage.Stage} (1 ticket)` : 'No tickets until midnight UTC'}</button>
        </div>
      {/if}
    {/if}
  </section>

  {#if answer?.Result}
    {@const r = answer.Result}
    <section class="panel run" data-testid="dungeon-run">
      <h2>Stage {r.Stage}</h2>
      <ol class="feed">
        {#each shown as e, i (i)}
          <li data-kind={e.Kind}><span class="t dim">{e.AtSeconds.toFixed(0)}s</span> {e.Text}</li>
        {/each}
      </ol>
      {#if !done}
        <button class="quiet" onclick={skip}>Skip to the end</button>
      {:else}
        <div class="result" class:won={r.Won} data-testid="dungeon-result" data-piece-id={answer.Reward?.PieceId ?? 0}>
          <strong>{r.Won ? 'Victory' : 'Defeat'}</strong>
          <span class="dim small">
            {r.Won ? `Baba Yaga falls after ${r.Seconds.toFixed(0)} s` : r.ReachedBoss ? `Baba Yaga had ${r.BossHpLeftPct}% left` : `Fell in wave ${r.WavesCleared + 1}`}
            &middot; {r.MealsEaten} meal{r.MealsEaten === 1 ? '' : 's'} eaten
          </span>
          {#if answer.Reward}
            <ul class="reward">
              {#if answer.Reward.PieceBaseId}<li>{answer.Reward.PieceRarity} {prettifyBaseId(answer.Reward.PieceBaseId)}</li>{/if}
              {#if answer.Reward.Chest}<li>A {answer.Reward.Chest} loot chest</li>{/if}
              {#if answer.Reward.Diamonds > 0}<li>+{answer.Reward.Diamonds} diamonds for the first clear</li>{/if}
            </ul>
            <div class="acts">
              {#if answer.Reward.PieceId}<button onclick={() => requestScreen('chest')}>See the piece</button>{/if}
              {#if answer.Reward.ChestId}<button onclick={() => requestScreen('chest')}>Open the chest</button>{/if}
            </div>
          {:else if !r.Won}
            <p class="dim small">Nothing this time - a lost run pays a Bronze chest one time in four.</p>
          {/if}
        </div>
      {/if}
    </section>
  {/if}
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    align-items: start;
  }
  header {
    display: flex;
    justify-content: space-between;
    gap: 0.75rem;
    align-items: flex-start;
  }
  h2 {
    margin: 0 0 0.4rem;
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
    font-size: 0.72rem;
  }
  .tickets {
    flex-shrink: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    border: 1px solid var(--border);
    border-radius: 999px;
    padding: 0.2rem 0.6rem;
    font-size: 0.8rem;
  }
  .locked {
    font-weight: 600;
  }
  .stages {
    display: grid;
    grid-template-columns: repeat(5, minmax(2.75rem, 1fr));
    gap: 0.35rem;
    margin: 0.4rem 0 0.8rem;
  }
  .stage {
    min-height: 44px;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }
  .stage[data-band='Ascension'] {
    border-color: var(--brass);
  }
  .stage[data-band='Beyond'] {
    border-color: var(--danger, #b3261e);
  }
  .stage.cleared::after {
    content: ' ✓';
    font-weight: 400;
  }
  .stage.on {
    background: var(--accent);
    color: var(--bg-panel);
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
  }
  .feed {
    list-style: none;
    margin: 0 0 0.6rem;
    padding: 0;
    display: grid;
    gap: 0.25rem;
    font-size: 0.82rem;
    max-height: 22rem;
    overflow-y: auto;
  }
  .feed li[data-kind='ability'] {
    color: var(--accent);
  }
  .feed li[data-kind='defeat'],
  .feed li[data-kind='timeout'] {
    color: var(--danger, #b3261e);
    font-weight: 700;
  }
  .feed li[data-kind='victory'],
  .feed li[data-kind='boss'] {
    font-weight: 700;
  }
  .t {
    display: inline-block;
    min-width: 2.5rem;
    font-variant-numeric: tabular-nums;
  }
  .result {
    display: grid;
    gap: 0.35rem;
    border-top: 1px solid var(--border);
    padding-top: 0.6rem;
  }
  .result strong {
    font-size: 1.1rem;
  }
  .result.won strong {
    color: var(--accent);
  }
  .reward {
    margin: 0;
    padding-left: 1.1rem;
    font-size: 0.85rem;
  }
  .acts {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
  }
</style>
