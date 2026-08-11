<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice, typicalHit } from '../lib/stores/game';
  import {
    queryKeys,
    fetchGuildRoster,
    fetchPlayerNames,
    fetchStatistics,
    fetchGuildLogistics,
    fetchGuildDepot,
    donateToGuildDepot,
    activateGuildBuff,
    fetchGuildShardMatch,
    fetchInventory,
    kickGuildMember,
    promoteGuildMember,
    demoteGuildMember,
  } from '../lib/net/rest';
  import {
    contributeToWarSupply,
    launchGuildRaid,
    contributeGuildGold,
    depositGuildMaterial,
    contributeToGuildStock,
    registerGuildDefense,
    executeCombatTurn,
    submitShardAttack,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { loadContent, prettifyBaseId, type ContentRegistry } from '../lib/net/content';
  import Bar from '../lib/ui/Bar.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import Money from '../lib/ui/Money.svelte';

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
    setTimeout(() => {
        client.invalidateQueries({ queryKey: queryKeys.guildRoster });
    }, 600);
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

  async function giveGold() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (treasuryGold < 1) return;
    try {
      await donateToGuildDepot('gold', treasuryGold);
      pushLocalNotice('Gold contributed to treasury!', 'info');
      setTimeout(() => {
        client.invalidateQueries({ queryKey: queryKeys.guildDepot });
        client.invalidateQueries({ queryKey: queryKeys.inventory });
      }, 700);
    } catch (e: any) {
      pushLocalNotice(e.message || 'Failed to contribute gold.', 'info');
    }
  }

  const members = $derived(
    [...(roster.data ?? [])].sort((a, b) => a.PlayerId - b.PlayerId),
  );

  let busy = $state(false);
  const myRole = $derived(members.find(m => m.PlayerId === connection.currentPlayerId)?.Role ?? 0);
  const ROLE_NAMES: Record<number, string> = { 0: 'Member', 1: 'Officer', 2: 'Leader' };

  async function handleKick(id: number) {
    if (busy) return;
    busy = true;
    try {
        await kickGuildMember(id);
        pushLocalNotice('Member kicked.');
    } catch (err: any) {
        pushLocalNotice(err.message || 'Failed to kick.');
    } finally {
        busy = false;
        refresh();
    }
  }

  async function handlePromote(id: number) {
    if (busy) return;
    busy = true;
    try {
        await promoteGuildMember(id);
        pushLocalNotice('Member promoted.');
    } catch (err: any) {
        pushLocalNotice(err.message || 'Failed to promote.');
    } finally {
        busy = false;
        refresh();
    }
  }

  async function handleDemote(id: number) {
    if (busy) return;
    busy = true;
    try {
        await demoteGuildMember(id);
        pushLocalNotice('Member demoted.');
    } catch (err: any) {
        pushLocalNotice(err.message || 'Failed to demote.');
    } finally {
        busy = false;
        refresh();
    }
  }

  // --- depot ----------------------------------------------------------------
  const logistics = createQuery(() => ({
    queryKey: queryKeys.guildLogistics,
    queryFn: fetchGuildLogistics,
    fetchGuildDepot,
    donateToGuildDepot,
    activateGuildBuff,
    enabled: hasGuild,
  }));

  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  let registry = $state<ContentRegistry | null>(null);
  $effect(() => {
    void loadContent().then((loaded) => (registry = loaded));
  });

  const itemDefinitionCount = $derived(registry?.items.size ?? 0);

  const depositable = $derived.by(() => {
    if (!registry) return [];
    return (inventory.data?.Stacks ?? [])
      .filter((stack) => stack.Quantity > 0)
      .map((stack) => ({
        definition: registry!.itemsByBaseId.get(stack.ItemId),
        baseId: stack.ItemId,
        quantity: stack.Quantity,
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

  // Modul: the match id SubmitShardAttack has to agree with.
  //
  // The validator refuses an attack aimed at any other match by DISCONNECTING,
  // and this id used to live only in the server's tick state - which is why
  // this screen previously said the button could not be shipped. The
  // /api/v1/guild/shard-match endpoint exists specifically to close that.
  //
  // Refetched on an interval because the match rolls over server-side without
  // anything this client does, and attacking a stale id is the failure this is
  // meant to prevent.
  const shardMatch = createQuery(() => ({
    queryKey: queryKeys.guildShardMatch,
    queryFn: fetchGuildShardMatch,
    enabled: hasGuild,
    refetchInterval: 30_000,
  }));

  const EMPTY_UUID = '00000000-0000-0000-0000-000000000000';
  const matchUuid = $derived(shardMatch.data?.MatchUuid ?? '');

  function attackShard() {
    const outcome = submitShardAttack({
      matchUuid,
      predictedDamage: Math.max($typicalHit ?? 0, 1000),
      hasGuild,
      quarantined,
      // The server compares against its own committed id; passing the same
      // value here means the guard checks exactly what the validator will.
      activeMatchUuid: matchUuid || EMPTY_UUID,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.guildShardMatch }), 900);
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

  // --- guild treasury ---
  const guildDepot = createQuery(() => ({
    queryKey: queryKeys.guildDepot,
    queryFn: fetchGuildDepot,
    enabled: hasGuild,
  }));

  function refreshDepotFull() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.guildDepot });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 700);
  }

  let donateMaterial = $state<string | number>(0);
  let donateQuantity = $state(1);

  const donateMax = $derived(
    donateMaterial === 'gold'
      ? (snap?.Gold ?? 0)
      : (depositable.find((row: any) => row.baseId === donateMaterial)?.quantity ?? 0),
  );


  async function handleDonate() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (donateQuantity < 1) return pushLocalNotice('Quantity must be positive.', 'info');
    
    try {
        await donateToGuildDepot(donateMaterial, Math.min(donateQuantity, donateMax));
        pushLocalNotice('Material donated for Weekly Contribution Points!', 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to donate.', 'info');
    }
  }

  // Only allow buff-related materials: logs and ores from the 5 regions
  const BUFF_MATERIAL_IDS = new Set([
    'birch_log', 'golden_birch_log', 'copper_ore', 'malachite_ore',
    'willow_log', 'golden_willow_log', 'iron_ore', 'hematite_ore',
    'acacia_log', 'golden_acacia_log', 'sulfur_ore', 'obsidian_ore',
    'frostpine_log', 'golden_frostpine_log', 'silver_ore', 'cobalt_ore',
    'ebon_log', 'golden_ebon_log', 'darksteel_ore', 'astralite_ore',
  ]);

  function isDonatableMaterial(baseId: string): boolean {
    return BUFF_MATERIAL_IDS.has(baseId);
  }

  // Buff tier definitions: [commonWood, rareWood, commonOre, rareOre] per tier
  let expandedBuff = $state<string | null>(null);

  function toggleBuff(type: string) {
    expandedBuff = expandedBuff === type ? null : type;
  }

  const BUFF_TIERS = [
    { tier: 1, region: 'Sunlit Plains',       commonWood: 'birch_log',       rareWood: 'golden_birch_log',    commonOre: 'copper_ore',    rareOre: 'malachite_ore'  },
    { tier: 2, region: 'Whispering Woods',    commonWood: 'willow_log',      rareWood: 'golden_willow_log',   commonOre: 'iron_ore',      rareOre: 'hematite_ore'   },
    { tier: 3, region: 'Scorched Wasteland',  commonWood: 'acacia_log',      rareWood: 'golden_acacia_log',   commonOre: 'sulfur_ore',    rareOre: 'obsidian_ore'   },
    { tier: 4, region: 'Frozen Peaks',        commonWood: 'frostpine_log',   rareWood: 'golden_frostpine_log',commonOre: 'silver_ore',    rareOre: 'cobalt_ore'     },
    { tier: 5, region: 'Shadow Citadel',      commonWood: 'ebon_log',        rareWood: 'golden_ebon_log',     commonOre: 'darksteel_ore', rareOre: 'astralite_ore'  },
  ];

  const BUFF_TYPES = [
    { type: 'Exp',      label: 'Experience Boost' },
    { type: 'Gold',     label: 'Gold Gain Boost'  },
    { type: 'DropRate', label: 'Drop Rate Boost'  },
    { type: 'Damage',   label: 'Damage Boost'     },
  ];

  const BUFF_COST_PER_MAT = 25_000; // 25k wood + 25k ore = 50k total

  function getDepotQty(baseId: string): number {
    const depot = guildDepot.data?.DepotByBaseId as Record<string, number> | undefined;
    return depot?.[baseId] ?? 0;
  }

  function canActivateTierPath(tierDef: typeof BUFF_TIERS[0], path: 'common' | 'rare'): boolean {
    const wood = path === 'rare' ? tierDef.rareWood : tierDef.commonWood;
    const ore  = path === 'rare' ? tierDef.rareOre  : tierDef.commonOre;
    return getDepotQty(wood) >= BUFF_COST_PER_MAT && getDepotQty(ore) >= BUFF_COST_PER_MAT;
  }

  async function handleActivateBuff(buffType: string, tier: number, path: 'common' | 'rare') {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (myRole < 1) return pushLocalNotice('Only officers and leaders can activate buffs.', 'info');
    
    try {
        await activateGuildBuff(buffType, tier, path);
        pushLocalNotice(`Buff activated! (Tier ${tier}, ${path})`, 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to activate buff.', 'info');
    }
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

    <section class="panel" style="display:none;">
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
          <Skeleton />
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

        <h3>Contribute Gold</h3>
        <div class="row">
          <input type="number" min="1" step="100" bind:value={treasuryGold} />
          <button disabled={!hasGuild || treasuryGold < 1} onclick={giveGold}>Contribute gold</button>
        </div>
        <p class="dim tiny">
          Raises the guild's tier and your own contribution ranking on the roster.
        </p>

        <h3>Donate Materials</h3>
        <p class="dim tiny">Donate logs and ores to the guild depot. Rarer materials grant more contribution points!</p>
        <label>
          Material
          <select bind:value={depotMaterial}>
            <option value={0}>Choose...</option>
            {#each depositable.filter(r => BUFF_MATERIAL_IDS.has(r.baseId)) as row (row.definition!.Id)}
              <option value={row.baseId}>
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
          <button disabled={depotMaterial === 0 || donateMax === 0} onclick={handleDonate}>
            Donate
          </button>
        </div>

        <p class="dim tiny">
          <strong>To depot</strong> fills the requirements above.
          <strong>To chain</strong> feeds the logistics production bar instead.
          <strong>Donate</strong> adds materials to the treasury for buffs and contribution points.
        </p>

        {#if depositable.length === 0}
          <p class="dim tiny">You are not carrying any stackable materials.</p>
        {/if}
      {/if}
    </section>


    <section class="panel">
      <h2>Guild Treasury & Buffs</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div style="margin-bottom: 0.75rem; font-size: 1.1rem;">
            <Money amount={guildDepot.data.GuildGold ?? 0} icon />
          </div>

          {#if (guildDepot.data.ActiveBuffs ?? []).filter(b => b.ExpiresAtEpoch * 1000 > Date.now()).length > 0}
            <div class="active-buffs-bar">
              {#each (guildDepot.data.ActiveBuffs ?? []).filter(b => b.ExpiresAtEpoch * 1000 > Date.now()) as ab}
                {@const buffInfo = BUFF_TYPES.find(b => b.type === ab.BuffType)}
                <span class="active-buff-chip">
                  {buffInfo?.label ?? ab.BuffType} T{ab.Tier} — until {new Date(ab.ExpiresAtEpoch * 1000).toLocaleTimeString()}
                </span>
              {/each}
            </div>
          {/if}

          {#each BUFF_TYPES as buff}
            {@const active = (guildDepot.data.ActiveBuffs ?? []).find(b => b.BuffType === buff.type && b.ExpiresAtEpoch * 1000 > Date.now())}
            {@const isOpen = expandedBuff === buff.type}
            <div class="buff-block">
              <button class="buff-header" onclick={() => toggleBuff(buff.type)}>
                <span class="buff-title">{isOpen ? '▼' : '▶'} {buff.label}</span>
                {#if active}
                  <span class="good-text tiny">Active T{active.Tier} until {new Date(active.ExpiresAtEpoch * 1000).toLocaleString()}</span>
                {:else}
                  <span class="dim tiny">Inactive</span>
                {/if}
              </button>

              {#if isOpen}
                {#each BUFF_TIERS as td}
                  <div class="buff-tier-row">
                    <span class="tier-label">T{td.tier}<br><span class="dim tiny">{td.region}</span></span>

                    <div class="buff-path">
                      <div class="mat-req">
                        <span class="mat-name">{prettifyBaseId(td.commonWood)}</span>
                        <span class="mat-stock" class:mat-ok={getDepotQty(td.commonWood) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.commonWood) < BUFF_COST_PER_MAT}>
                          {getDepotQty(td.commonWood).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                        </span>
                      </div>
                      <div class="mat-req">
                        <span class="mat-name">{prettifyBaseId(td.commonOre)}</span>
                        <span class="mat-stock" class:mat-ok={getDepotQty(td.commonOre) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.commonOre) < BUFF_COST_PER_MAT}>
                          {getDepotQty(td.commonOre).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                        </span>
                      </div>
                      <button
                        class="tiny-btn"
                        disabled={myRole < 1 || !canActivateTierPath(td, 'common')}
                        onclick={() => handleActivateBuff(buff.type, td.tier, 'common')}
                      >Activate (1h)</button>
                    </div>

                    <div class="buff-path rare">
                      <div class="mat-req">
                        <span class="mat-name rare-mat">{prettifyBaseId(td.rareWood)}</span>
                        <span class="mat-stock" class:mat-ok={getDepotQty(td.rareWood) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.rareWood) < BUFF_COST_PER_MAT}>
                          {getDepotQty(td.rareWood).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                        </span>
                      </div>
                      <div class="mat-req">
                        <span class="mat-name rare-mat">{prettifyBaseId(td.rareOre)}</span>
                        <span class="mat-stock" class:mat-ok={getDepotQty(td.rareOre) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.rareOre) < BUFF_COST_PER_MAT}>
                          {getDepotQty(td.rareOre).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                        </span>
                      </div>
                      <button
                        class="tiny-btn rare-btn"
                        disabled={myRole < 1 || !canActivateTierPath(td, 'rare')}
                        onclick={() => handleActivateBuff(buff.type, td.tier, 'rare')}
                      >Activate (9h)</button>
                    </div>
                  </div>
                {/each}
              {/if}
            </div>
          {/each}
        {/if}
      {/if}
    </section>

    <section class="panel">
      <h2>Guild Contributors</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to contribute.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div class="prize-info">
            <h3> Weekly Prizes</h3>
            <p class="dim tiny">Every week, <strong>50% of the guild treasury</strong> is distributed to the top 3 material contributors:</p>
            <ul class="prize-list">
              <li><span class="gold-text">1st place</span> — 25% of treasury</li>
              <li><span class="silver-text">2nd place</span> — 15% of treasury</li>
              <li><span class="bronze-text">3rd place</span> — 10% of treasury</li>
            </ul>
            <p class="dim tiny">Only material contributions count toward the leaderboard, not gold donations.</p>
          </div>

          <h3>Weekly Leaderboard</h3>
          {#if (guildDepot.data.Leaderboard ?? []).filter(m => m.WeeklyContributionPoints > 0).length === 0}
            <p class="dim small">No material contributions this week yet.</p>
          {:else}
            <ul class="members" style="margin-bottom: 1rem;">
              {#each (guildDepot.data.Leaderboard ?? []).filter(m => m.WeeklyContributionPoints > 0) as member, i}
                <li>
                  <span class="who">
                    {#if i === 0}{:else if i === 1}{:else if i === 2}{:else}#{i + 1}{/if}
                    {member.Name}
                    {#if member.PlayerId === connection.currentPlayerId}<span class="dim tiny">you</span>{/if}
                  </span>
                  <span class="dim">{member.WeeklyContributionPoints.toLocaleString()} pts</span>
                </li>
              {/each}
            </ul>
          {/if}


        {/if}
      {/if}
    </section>

    <!-- Cross-shard war hidden -->
    <section class="panel">
      <h2>Members</h2>

      {#if roster.isPending}
        <p class="dim small">Loading the roster...</p>
      {:else if members.length === 0}
        <p class="dim small">No members listed.</p>
      {:else}
        <ul class="members">
          {#each members as member (member.PlayerId)}
            <li>
              <span class="who" style="display: flex; gap: 0.5rem; align-items: center; width: 100%;">
                {nameById.get(member.PlayerId) ?? `Player #${member.PlayerId}`}
                <span class="dim tiny">[{ROLE_NAMES[member.Role] ?? 'Unknown'}]</span>
                {#if member.PlayerId === connection.currentPlayerId}
                  <span class="dim tiny">you</span>
                {/if}
                
                <span style="flex: 1;"></span>
                
                {#if myRole >= 1 && member.Role < myRole && member.PlayerId !== connection.currentPlayerId}
                  {#if myRole === 2}
                    {#if member.Role === 0}
                      <button class="tiny-btn" disabled={busy} onclick={() => handlePromote(member.PlayerId)}>Promote</button>
                    {:else if member.Role === 1}
                      <button class="tiny-btn" disabled={busy} onclick={() => handleDemote(member.PlayerId)}>Demote</button>
                    {/if}
                  {/if}
                  <button class="tiny-btn warning" disabled={busy} onclick={() => handleKick(member.PlayerId)}>Kick</button>
                {/if}
              </span>
            </li>
          {/each}
        </ul>
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

  .buff-block {
    border: 1px solid var(--border);
    border-radius: 4px;
    margin-bottom: 0.75rem;
    overflow: hidden;
  }

  .buff-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.4rem 0.6rem;
    background: color-mix(in srgb, var(--accent) 10%, transparent);
    border-bottom: 1px solid var(--border);
    width: 100%;
    text-align: left;
    border: none;
    border-radius: 0;
    cursor: pointer;
    color: inherit;
    font: inherit;
  }
  .buff-header:hover {
    background: color-mix(in srgb, var(--accent) 18%, transparent);
  }

  .buff-title {
    font-weight: bold;
    font-size: 0.9rem;
  }

  .buff-tier-row {
    display: grid;
    grid-template-columns: 4rem 1fr 1fr;
    gap: 0.25rem;
    padding: 0.35rem 0.5rem;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
    align-items: start;
    font-size: 0.75rem;
  }

  .buff-tier-row:last-child {
    border-bottom: none;
  }

  .tier-label {
    font-weight: 600;
  }

  .buff-path {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    padding: 0.25rem 0.4rem;
    border-radius: 3px;
    background: color-mix(in srgb, var(--bg-panel) 50%, transparent);
  }

  .buff-path.rare {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
  }

  .mat-req {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.25rem;
  }

  .mat-name {
    color: var(--text-dim);
    font-size: 0.72rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 7rem;
  }

  .mat-name.rare-mat {
    color: var(--accent);
  }

  .mat-stock {
    font-size: 0.7rem;
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
  }

  .mat-ok { color: var(--good); }
  .mat-low { color: var(--danger); }

  .tiny-btn {
    white-space: nowrap;
    font-size: 0.72rem;
  }

  .rare-btn {
    background: color-mix(in srgb, var(--accent) 20%, transparent);
    border-color: var(--accent);
    color: var(--accent);
  }

  .active-buffs-bar {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin-bottom: 0.75rem;
  }

  .active-buff-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    background: color-mix(in srgb, var(--good) 15%, transparent);
    border: 1px solid var(--good);
    border-radius: 12px;
    padding: 0.15rem 0.5rem;
    font-size: 0.75rem;
    color: var(--good);
  }

  .prize-info {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
    border: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
    border-radius: 4px;
    padding: 0.6rem 0.8rem;
    margin-bottom: 0.75rem;
  }

  .prize-list {
    list-style: none;
    margin: 0.4rem 0;
    padding: 0;
    font-size: 0.82rem;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .gold-text   { color: #f0c040; }
  .silver-text { color: #c0c0c0; }
  .bronze-text { color: #cd7f32; }

  .members {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }

  .members li {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
    padding: 0.35rem 0;
    border-bottom: 1px solid var(--line, rgba(255, 255, 255, 0.07));
  }

  .members li:last-child {
    border-bottom: none;
  }

  .who {
    font-weight: 600;
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
