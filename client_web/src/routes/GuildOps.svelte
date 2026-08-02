<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    queryKeys,
    fetchGuildRoster,
    fetchPlayerNames,
    fetchStatistics,
    fetchGuildLogistics,
    fetchInventory,
  } from '../lib/net/rest';
  import {
    contributeToWarSupply,
    launchGuildRaid,
    contributeGuildGold,
    establishMentorship,
    terminateMentorship,
    depositGuildMaterial,
    contributeToGuildStock,
    registerGuildDefense,
    executeCombatTurn,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { loadContent, prettifyBaseId, type ContentRegistry } from '../lib/net/content';
  import Bar from '../lib/ui/Bar.svelte';

  const client = useQueryClient();
  const roster = createQuery(() => ({ queryKey: queryKeys.guildRoster, queryFn: fetchGuildRoster }));
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const snap = $derived($playerState);
  const hasGuild = $derived((statistics.data?.GuildName ?? '') !== '');
  const warId = $derived(snap ? Number(snap.ActiveGuildWarId) : 0);

  const rosterIds = $derived((roster.data ?? []).map((m) => m.PlayerId).sort());
  const names = createQuery(() => ({
    queryKey: queryKeys.playerNames(rosterIds),
    queryFn: () => fetchPlayerNames(rosterIds),
    enabled: rosterIds.length > 0,
    staleTime: 10 * 60_000,
  }));
  const nameById = $derived(new Map((names.data ?? []).map((n) => [n.PlayerId, n.Username])));

  function refresh() {
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.guildRoster }), 600);
  }

  // --- war ------------------------------------------------------------------
  // Modul: the three war axes are mirrored for both sides on the hot path, so
  // this is a live scoreboard rather than a REST snapshot.
  const warAxes = $derived(
    snap
      ? [
          { label: 'Combat vanguard', ours: snap.GuildCombatVanguardPoints, theirs: snap.EnemyCombatVanguardPoints },
          { label: 'Production logistics', ours: snap.GuildProductionLogisticsPoints, theirs: snap.EnemyProductionLogisticsPoints },
          { label: 'Gathering supply', ours: snap.GuildGatheringSupplyChainPoints, theirs: snap.EnemyGatheringSupplyChainPoints },
        ]
      : [],
  );

  let warCommodity = $state(1);
  let warQuantity = $state(10);

  function contributeWar() {
    const outcome = contributeToWarSupply(warCommodity, warQuantity, warId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  // --- raid -----------------------------------------------------------------
  function raid() {
    const outcome = launchGuildRaid(hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    // Leader-only is enforced server-side against the locked membership row,
    // and a non-leader's request simply rolls back with no message at all -
    // so promising success here would be a lie.
    pushLocalNotice('Raid requested. Only a guild leader can actually start one.', 'info');
  }

  // --- treasury -------------------------------------------------------------
  let treasuryGold = $state(1000);

  function giveGold() {
    const outcome = contributeGuildGold(treasuryGold, hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  // --- mentorship -----------------------------------------------------------
  const mentorId = $derived(snap ? Number(snap.ActiveMentorPlayerId) : 0);
  const mentorBonus = $derived(
    snap && typeof snap.MentorshipExpBonusMultiplier === 'number'
      ? snap.MentorshipExpBonusMultiplier
      : 1,
  );

  let mentorTarget = $state(0);

  function establish() {
    const outcome = establishMentorship(mentorTarget);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    pushLocalNotice('Mentorship requested.', 'info');
  }

  function terminate() {
    const outcome = terminateMentorship(mentorId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    pushLocalNotice('Mentorship ended.', 'info');
  }

  const otherMembers = $derived(
    (roster.data ?? []).filter((m) => m.PlayerId !== connection.currentPlayerId),
  );

  // --- depot ----------------------------------------------------------------
  // Modul: the depot is a SEPARATE system from the logistics chain, despite
  // both being "put materials into the guild". DepositGuildMaterial addresses
  // the depot through MaterialId/DepositQuantity; ContributeToGuild addresses
  // the chain through TargetId/LimitPrice. Different validators, different
  // engines, different tables. Wiring one to the other's fields disconnects.

  const logistics = createQuery(() => ({
    queryKey: queryKeys.guildLogistics,
    queryFn: fetchGuildLogistics,
    // NEVER add a query string to this - the handler treats any query as
    // tampering and force-disconnects the player's WebSocket session.
    enabled: hasGuild,
  }));

  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  let registry = $state<ContentRegistry | null>(null);
  $effect(() => {
    void loadContent().then((loaded) => (registry = loaded));
  });

  const itemDefinitionCount = $derived(registry?.items.size ?? 0);

  /** Stackable materials actually in the backpack, with their numeric ids. */
  const depositable = $derived.by(() => {
    if (!registry) return [];
    return (inventory.data?.Stacks ?? [])
      .filter((stack) => stack.BackpackQuantity > 0)
      .map((stack) => ({
        definition: registry!.itemsByBaseId.get(stack.ItemId),
        baseId: stack.ItemId,
        quantity: stack.BackpackQuantity,
      }))
      .filter((row) => row.definition !== undefined)
      .sort((a, b) => a.baseId.localeCompare(b.baseId));
  });

  let depotMaterial = $state(0);
  let depotQuantity = $state(1);

  const depotMax = $derived(
    depositable.find((row) => row.definition!.Id === depotMaterial)?.quantity ?? 0,
  );

  function refreshDepot() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.guildLogistics });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 700);
  }

  function deposit() {
    const outcome = depositGuildMaterial(
      depotMaterial,
      Math.min(depotQuantity, depotMax),
      hasGuild,
      itemDefinitionCount,
    );
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refreshDepot();
  }

  function contributeStock() {
    const outcome = contributeToGuildStock(
      depotMaterial,
      Math.min(depotQuantity, depotMax),
      hasGuild,
    );
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refreshDepot();
  }

  function materialName(materialId: number): string {
    const definition = registry?.items.get(materialId);
    return definition ? prettifyBaseId(definition.BaseId) : `Material #${materialId}`;
  }

  // --- cross-shard war ------------------------------------------------------
  const quarantined = $derived((snap?.Quarantine_Active ?? 0) !== 0);

  function defend() {
    const outcome = registerGuildDefense(hasGuild, quarantined);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    pushLocalNotice('Your roster is registered as the guild defence.', 'info');
  }

  // --- guild battle turns ---------------------------------------------------
  // CombatSimulationMatchId is the live match this player is in. It goes to
  // zero when the match ends, and submitting a turn against a finished match
  // FORCE-DISCONNECTS after the fact - the packet is well formed, the server
  // just refuses it destructively - so the button follows the field exactly.
  const matchId = $derived(snap?.CombatSimulationMatchId ?? 0);
  const turnCounter = $derived(snap?.CombatSimulationTurnCounter ?? 0);
  const damageDelta = $derived(snap?.CombatSimulationDamageDelta ?? 0);

  function takeTurn() {
    const outcome = executeCombatTurn(matchId, turnCounter, hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
  }
</script>

{#if !snap}
  <p class="dim pad">Waiting for state...</p>
{:else}
  <div class="grid">
    <section class="panel">
      <h2>Guild war</h2>

      {#if warId <= 0}
        <p class="dim">
          No war is active. The scoreboard below appears once your guild is
          matched.
        </p>
      {:else}
        <p class="dim small">
          War #{warId} &middot; multiplier
          {typeof snap.CachedWarMultiplier === 'number'
            ? snap.CachedWarMultiplier.toFixed(2)
            : snap.CachedWarMultiplier}x
        </p>

        {#each warAxes as axis}
          {@const total = Math.max(1, axis.ours + axis.theirs)}
          <div class="axis">
            <span class="dim tiny">{axis.label}</span>
            <Bar
              value={axis.ours}
              max={total}
              color={axis.ours >= axis.theirs ? 'var(--good)' : 'var(--danger)'}
              label={`${axis.ours.toLocaleString()} vs ${axis.theirs.toLocaleString()}`}
            />
          </div>
        {/each}

        <h3>Contribute supply</h3>
        <div class="row">
          <input type="number" min="1" bind:value={warCommodity} title="Commodity id" />
          <input type="number" min="1" bind:value={warQuantity} title="Quantity" />
          <button onclick={contributeWar}>Burn</button>
        </div>
        <p class="dim tiny">
          Contributions are burned into the war effort. The wire takes a numeric
          commodity id here - there is no picker endpoint for it.
        </p>
      {/if}
    </section>

    <section class="panel">
      <h2>Raid</h2>

      {#if snap.GuildRaidBossMaxHp > 0}
        <p class="dim small">Tier {snap.GuildRaidTier}</p>
        <Bar
          value={Number(snap.GuildRaidBossCurrentHp)}
          max={Number(snap.GuildRaidBossMaxHp)}
          color="var(--danger)"
          label={`${Number(snap.GuildRaidBossCurrentHp).toLocaleString()} / ${Number(snap.GuildRaidBossMaxHp).toLocaleString()}`}
        />
      {:else}
        <p class="dim">No raid boss active.</p>
      {/if}

      <button disabled={!hasGuild} onclick={raid}>Launch raid</button>

      <h3>Treasury</h3>
      <div class="row">
        <input type="number" min="1" step="100" bind:value={treasuryGold} />
        <button disabled={!hasGuild || treasuryGold < 1} onclick={giveGold}>Contribute gold</button>
      </div>
      <p class="dim tiny">
        Raises the guild's tier and your own contribution ranking on the roster.
      </p>

      <h3>Logistics</h3>
      <div class="axis">
        <span class="dim tiny">Depot level {snap.GuildLogisticsLevel}</span>
        <Bar
          value={Number(snap.GuildLogisticsCurrentStock)}
          max={Math.max(1, Number(snap.GuildLogisticsTargetRequirement))}
          color="var(--accent)"
          label={`${Number(snap.GuildLogisticsCurrentStock).toLocaleString()} / ${Number(snap.GuildLogisticsTargetRequirement).toLocaleString()}`}
        />
      </div>
    </section>

    <section class="panel">
      <h2>Depot</h2>

      {#if !hasGuild}
        <p class="dim">Join a guild to use its depot.</p>
      {:else}
        <p class="dim small">
          Per-material stock against what the guild needs. Depositing moves the
          material out of your backpack permanently.
        </p>

        {#if logistics.isPending}
          <p class="dim">Loading...</p>
        {:else if (logistics.data ?? []).length === 0}
          <p class="dim">The depot has no requirements set.</p>
        {:else}
          <ul class="depot">
            {#each logistics.data ?? [] as row (row.MaterialId)}
              {@const required = Math.max(1, Number(row.TargetRequirement))}
              {@const stock = Number(row.CurrentStock)}
              {@const met = stock >= Number(row.TargetRequirement)}
              <li>
                <span class="mat">{materialName(row.MaterialId)}</span>
                <Bar
                  value={stock}
                  max={required}
                  color={met ? 'var(--good)' : 'var(--accent)'}
                  label={`${stock.toLocaleString()} / ${Number(row.TargetRequirement).toLocaleString()}`}
                />
              </li>
            {/each}
          </ul>
        {/if}

        <h3>Deposit</h3>
        <label>
          Material
          <select bind:value={depotMaterial}>
            <option value={0}>Choose...</option>
            {#each depositable as row (row.definition!.Id)}
              <option value={row.definition!.Id}>
                {prettifyBaseId(row.baseId)} (x{row.quantity})
              </option>
            {/each}
          </select>
        </label>

        <div class="row">
          <input type="number" min="1" max={depotMax || 1} bind:value={depotQuantity} />
          <button disabled={depotMaterial === 0 || depotMax === 0} onclick={deposit}>
            To depot
          </button>
          <button disabled={depotMaterial === 0 || depotMax === 0} onclick={contributeStock}>
            To chain
          </button>
        </div>

        <!-- These two buttons look interchangeable and are not. Saying so is
             cheaper than a player wondering why one number moved and not the
             other. -->
        <p class="dim tiny">
          <strong>To depot</strong> fills the requirements above.
          <strong>To chain</strong> feeds the logistics production bar instead.
          They are separate systems that happen to take the same materials.
        </p>

        {#if depositable.length === 0}
          <p class="dim tiny">You are not carrying any stackable materials.</p>
        {/if}
      {/if}
    </section>

    <section class="panel">
      <h2>Cross-shard war</h2>

      {#if !hasGuild}
        <p class="dim">Join a guild first.</p>
      {:else}
        <h3>Defence</h3>
        <p class="dim small">
          Volunteers your roster as the guild's defending side. It carries no
          target and no amount - the server reads everything from your guild.
        </p>
        <button disabled={quarantined} onclick={defend}>Register as defender</button>

        <h3>Battle</h3>
        {#if matchId > 0}
          <dl class="stats">
            <div><dt>Match</dt><dd>#{matchId}</dd></div>
            <div><dt>Turn</dt><dd>{turnCounter}</dd></div>
          </dl>
          <p class="dim small">
            Damage swing last turn:
            <span class={damageDelta >= 0 ? 'good-text' : 'bad-text'}>
              {damageDelta >= 0 ? '+' : ''}{damageDelta.toLocaleString()}
            </span>
          </p>
          <button onclick={takeTurn}>Take a turn</button>
        {:else}
          <p class="dim">
            No battle running. This appears when your guild is matched against
            another.
          </p>
        {/if}

        <h3>Attacking another shard</h3>
        <!-- An honest gap rather than a dangerous button. See below. -->
        <p class="dim small">
          Not available from this client. The server refuses an attack aimed at
          any match other than the one you are already committed to - and it
          refuses it by <em>disconnecting you</em> - but the match id it checks
          against lives only in the server's own tick state and is not carried
          on any packet or endpoint this client can read. Shipping the button
          would mean guessing, and a wrong guess ends the session.
        </p>
      {/if}
    </section>

    <section class="panel">
      <h2>Mentorship</h2>

      <dl class="stats">
        <div><dt>Mentors held</dt><dd>{snap.CachedMentorCount}</dd></div>
        <div><dt>XP bonus</dt><dd>{(mentorBonus * 100).toFixed(0)}%</dd></div>
      </dl>

      {#if mentorId > 0}
        <p class="active">
          Mentored by {nameById.get(mentorId) ?? `Player #${mentorId}`}.
        </p>
        <button onclick={terminate}>End mentorship</button>
      {:else}
        <p class="dim small">
          A mentor lends you their character's experience bonus. You cannot
          mentor yourself.
        </p>

        <label>
          Guild member
          <select bind:value={mentorTarget}>
            <option value={0}>Choose...</option>
            {#each otherMembers as member (member.PlayerId)}
              <option value={member.PlayerId}>
                {nameById.get(member.PlayerId) ?? `Player #${member.PlayerId}`}
              </option>
            {/each}
          </select>
        </label>

        <button disabled={mentorTarget === 0} onclick={establish}>Request mentorship</button>

        {#if otherMembers.length === 0}
          <p class="dim tiny">No other guild members to ask.</p>
        {/if}
      {/if}
    </section>
  </div>
{/if}

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .tiny {
    font-size: 0.72rem;
    margin: 0.35rem 0 0;
  }
  .pad {
    padding: 1rem;
  }

  .active {
    color: var(--good);
    font-size: 0.88rem;
    margin: 0 0 0.6rem;
  }

  .axis {
    display: grid;
    gap: 0.2rem;
    margin-bottom: 0.5rem;
  }

  .row {
    display: grid;
    grid-template-columns: 1fr 1fr auto;
    gap: 0.4rem;
  }

  .depot {
    list-style: none;
    margin: 0 0 0.5rem;
    padding: 0;
    display: grid;
    gap: 0.45rem;
  }

  .depot li {
    display: grid;
    gap: 0.15rem;
  }

  .mat {
    font-size: 0.78rem;
    color: var(--text-dim);
  }

  .good-text {
    color: var(--good);
    font-variant-numeric: tabular-nums;
  }

  .bad-text {
    color: var(--danger);
    font-variant-numeric: tabular-nums;
  }

  input,
  select {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  label {
    display: grid;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    margin-bottom: 0.6rem;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.7rem;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }
</style>
