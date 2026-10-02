<script lang="ts">
  import { formatNumber } from '../lib/ui/format';
  import PlayerAvatar from '../lib/ui/PlayerAvatar.svelte';
  import { profileLink } from '../lib/ui/profileLink';
  import ConfirmButton from '../lib/ui/ConfirmButton.svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice, typicalHit } from '../lib/stores/game';
  import {
    queryKeys,
    fetchGuildRoster,
    fetchGuilds,
    fetchPlayerNames,
    fetchStatistics,
    fetchGuildLogistics,
    fetchGuildDepot,
    donateToGuildDepot,
    activateGuildBuff,
    fetchGuildShardMatch,
    fetchGuildWarUnlock,
    fetchMaterials,
    kickGuildMember,
    promoteGuildMember,
    demoteGuildMember,
    fetchGuildLeavePreview,
    leaveGuild,
  } from '../lib/net/rest';
  import {
    contributeToWarSupply,
    launchGuildRaid,
    depositGuildMaterial,
    contributeToGuildStock,
    registerGuildDefense,
    executeCombatTurn,
    submitShardAttack,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { setGuildWarLockProgress } from '../lib/stores/commandResults';
  import { invalidateOwnedItems, invalidateGuildMembership } from '../lib/net/queryClient';
  import { loadContent, prettifyBaseId, type ContentRegistry } from '../lib/net/content';
  import Bar from '../lib/ui/Bar.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';
  import Money from '../lib/ui/Money.svelte';
  import GuildBrowser from '../lib/ui/GuildBrowser.svelte';
  import GuildApplications from '../lib/ui/GuildApplications.svelte';

  const client = useQueryClient();
  const roster = createQuery(() => ({ queryKey: queryKeys.guildRoster, queryFn: fetchGuildRoster }));
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const guilds = createQuery(() => ({ queryKey: queryKeys.guilds, queryFn: fetchGuilds }));

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
  // Modul: GUILD WARS ARE LOCKED behind a population floor (server:
  // GuildWarUnlock) - the war code could mint diamonds for two alt guilds, and
  // a war means nothing with one guild. The panel says so with the live
  // progress, and hands the same numbers to the command-result toast so a
  // refused war command reads "now 12/50" rather than a bare refusal.
  const warLock = createQuery(() => ({
    queryKey: queryKeys.guildWarUnlock,
    queryFn: fetchGuildWarUnlock,
    staleTime: 5 * 60_000,
  }));
  const warLocked = $derived(warLock.data ? !warLock.data.Unlocked : false);
  // Modul: task 107. The war card is collapsed while locked; see its markup.
  let warOpen = $state(false);

  $effect(() => {
    const d = warLock.data;
    setGuildWarLockProgress(d && !d.Unlocked ? { players: d.QualifyingPlayers, required: d.RequiredPlayers, guilds: d.QualifyingGuilds, requiredGuilds: d.RequiredGuilds } : null);
  });

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
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
  }

  // --- raid -----------------------------------------------------------------
  function raid() {
    const outcome = launchGuildRaid(hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
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
        invalidateOwnedItems(client);
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

  // Modul: task 107. The header card's numbers come from the guild directory
  // (tier, members, tax) - the one place the server already publishes them -
  // matched on name, which is unique. The weekly rank is read off the same
  // leaderboard the ranking card shows, so the two cannot disagree.
  const myGuild = $derived((guilds.data ?? []).find((g) => g.Name === statistics.data?.GuildName));
  const myWeeklyRank = $derived.by(() => {
    const board = (guildDepot.data?.Leaderboard ?? []).filter((m) => m.WeeklyContributionPoints > 0);
    const at = board.findIndex((m) => m.PlayerId === connection.currentPlayerId);
    return at >= 0 ? at + 1 : 0;
  });

  async function handleKick(id: number) {
    if (busy) return;
    busy = true;
    try {
        await kickGuildMember(id);
        pushLocalNotice('Member kicked.', 'info');
    } catch (err: any) {
        pushLocalNotice(err.message || 'Failed to kick.', 'error');
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
        pushLocalNotice('Member promoted.', 'info');
    } catch (err: any) {
        pushLocalNotice(err.message || 'Failed to promote.', 'error');
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
        pushLocalNotice('Member demoted.', 'info');
    } catch (err: any) {
        pushLocalNotice(err.message || 'Failed to demote.', 'error');
    } finally {
        busy = false;
        refresh();
    }
  }

  // --- leave ----------------------------------------------------------------
  // Modul: task 94. There was no way out of a guild at all - the engine had a
  // leave with succession and nothing called it. The confirm says what the
  // server WILL do, read from its own preview: a leader is told who leads
  // next, the last member that the guild closes. Guessing the successor here
  // from the roster would be a second copy of the succession rule.
  const leavePreview = createQuery(() => ({
    queryKey: queryKeys.guildLeavePreview,
    queryFn: fetchGuildLeavePreview,
    enabled: hasGuild,
  }));

  const memberName = (id: number) => nameById.get(id) ?? `Player #${id}`;

  const leaveNote = $derived.by(() => {
    const p = leavePreview.data;
    if (!p || !p.InGuild) return '';
    if (p.ClosesGuild) {
      return 'You are the last member: leaving closes the guild for good, with its depot, treasury and buffs.';
    }
    if (p.IsLeader && p.SuccessorPlayerId > 0) {
      return `${memberName(p.SuccessorPlayerId)} will lead the guild after you.`;
    }
    return 'You can join this or another guild again from the Guild tab.';
  });

  async function handleLeave() {
    if (busy) return;
    busy = true;
    try {
      const result = await leaveGuild();
      if (!result.Left) {
        pushLocalNotice(result.Reason || 'Could not leave the guild.', 'error');
      } else if (result.ClosedGuild) {
        pushLocalNotice('You left, and the guild has closed.', 'info');
      } else if (result.SuccessorPlayerId > 0) {
        pushLocalNotice(`You left. ${memberName(result.SuccessorPlayerId)} leads the guild now.`, 'info');
      } else {
        pushLocalNotice('You left the guild.', 'info');
      }
    } catch (err: any) {
      pushLocalNotice(err?.message || 'Could not leave the guild.', 'error');
    } finally {
      busy = false;
      invalidateGuildMembership(client);
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

  // Modul: MATERIALS ONLY. The depot takes stackable materials; the equipment
  // half of the snapshot was never read on this screen. See fetchMaterials.
  const inventory = createQuery(() => ({ queryKey: queryKeys.materials, queryFn: fetchMaterials }));

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
      // Modul: a stack with NO ItemDefinition is kept. Materials live in two
      // namespaces - items.json defines 326 pieces of equipment and 16 of the
      // 20 buff materials, while copper_ore, iron_ore, raw_log and oak_log are
      // unified COMMODITY ids that ContentRegistry, CraftingEngine and the
      // Village all use and items.json does not carry. Dropping the rows
      // without a definition removed exactly those four from this panel, so a
      // player holding 5,000 copper ore saw it listed as x0 with a dead
      // button. Donation needs the base id only; just the two numeric-id APIs
      // below need a definition, and those are gated separately.
      .sort((a, b) => a.baseId.localeCompare(b.baseId));
  });

  // Modul: this holds a BASE ID, because that is what the <select> below puts
  // in it - the options carry `value={baseId}`. It used to be typed as a number
  // and looked up against `definition.Id`, so the comparison was number ===
  // string and never matched: depotMax was permanently 0, and every button in
  // this panel is disabled on `depotMax === 0`. Depositing, chaining and
  // donating were all dead, which is why the live GuildDepotBalances and
  // GuildContributionLedgers tables had no rows at all.
  //
  // The two APIs disagree about what a material is, so both forms are kept
  // side by side rather than converted at each call site: donateToGuildDepot
  // wants the base id, depositGuildMaterial/contributeToGuildStock want the
  // numeric definition id and REFUSE a non-integer.
  let depotMaterial = $state('');
  let depotQuantity = $state(1);

  const depotRow = $derived(depositable.find((row) => row.baseId === depotMaterial));
  const depotMax = $derived(depotRow?.quantity ?? 0);
  const depotMaterialId = $derived(depotRow?.definition?.Id ?? 0);

  function refreshDepot() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.guildLogistics });
      invalidateOwnedItems(client);
    }, 700);
  }

  function deposit() {
    const outcome = depositGuildMaterial(
      depotMaterialId,
      Math.min(depotQuantity, depotMax),
      hasGuild,
      itemDefinitionCount,
    );
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    refreshDepot();
  }

  function contributeStock() {
    const outcome = contributeToGuildStock(
      depotMaterialId,
      Math.min(depotQuantity, depotMax),
      hasGuild,
    );
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
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
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
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
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
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
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
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
      invalidateOwnedItems(client);
    }, 700);
  }

  // Modul: a separate donateMaterial/donateQuantity/donateMax trio used to live
  // here. It was orphaned when the two donate paths were merged - handleDonate
  // reads the depot* state, so nothing ever read these - and a second copy of
  // the same state is how the two halves of this panel disagreed in the first
  // place. One material selection, one quantity.

  // Modul: task 107. One destination choice replaces three buttons. The three
  // calls are unchanged - only which one a tap reaches is.
  type DepositDest = 'depot' | 'chain' | 'treasury';
  const DEPOSIT_DESTINATIONS: { key: DepositDest; label: string; hint: string }[] = [
    { key: 'depot', label: 'Depot', hint: 'Fills the requirements listed above. The material leaves your backpack for good.' },
    { key: 'chain', label: 'Chain', hint: 'Feeds the logistics production bar instead of the requirements.' },
    { key: 'treasury', label: 'Treasury', hint: 'Pays for buffs and earns weekly ranking points. Buff materials only.' },
  ];
  let depositDest = $state<DepositDest>('treasury');

  const canDeposit = $derived(
    depotMaterial !== '' &&
      depotMax > 0 &&
      depotMaterialId !== 0 &&
      (depositDest !== 'treasury' || isDonatableMaterial(depotMaterial)),
  );

  function depositSelected() {
    if (depositDest === 'depot') deposit();
    else if (depositDest === 'chain') contributeStock();
    else void handleDonate();
  }

  async function handleDonate() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (depotQuantity < 1) return pushLocalNotice('Quantity must be positive.', 'info');
    if (depotMaterial === '') return;

    try {
        await donateToGuildDepot(depotMaterial, Math.min(depotQuantity, depotMax));
        pushLocalNotice('Material donated for Weekly Contribution Points!', 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to donate.', 'info');
    }
  }

  // Only allow buff-related materials: logs and ores from the 5 regions.
  //
  // Modul: FOUR of these twenty are not in items.json - copper_ore, iron_ore,
  // obsidian_ore and silver_ore. They are real unified commodity ids (the
  // Village and CraftingEngine both use them) but they have no ItemDefinition,
  // and GuildDepotBalances is keyed on ItemDefinitionId, so the donate
  // endpoint answers 400 for all four. The server's own BuffTierMaterials
  // table has the same four, which makes the common-ore path of several buff
  // tiers unreachable. Fixing it means either cataloguing those four or
  // repointing the tiers at ores that exist - a content decision, recorded
  // here rather than silently worked around.
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

  // Modul: logs and ores are identified by a BASE ID SUFFIX, which is how this
  // codebase classifies items generally (see getArmourFamily, which reads a
  // prefix). The previous version filtered on `definition.Subtype === 'Log'`,
  // and ItemDefinition has no Subtype field at all - so every comparison was
  // against undefined, the filter was permanently false, and the branch below
  // it rendered zero options. The dropdown only ever showed the hardcoded
  // BUFF_MATERIAL_IDS set, which is the opposite of what "allow all materials
  // in the depot select" was meant to do.
  function isLogOrOre(baseId: string): boolean {
    return baseId.endsWith('_log') || baseId.endsWith('_ore');
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
  <p class="dim pad">Waiting for the first state snapshot...</p>
{:else if statistics.isPending}
  <div class="grid"><Skeleton /></div>
{:else if statistics.isError && statistics.data === undefined}
  <div class="grid"><QueryError query={statistics} what="your guild membership" /></div>
{:else if !hasGuild}
  <!-- Modul: task 107. Guildless: the browser and Create, nothing else. The
       dashboard cards all say "Join a guild to ..." and there is nothing on
       this tab to look at until one has. -->
  <div class="grid"><GuildBrowser /></div>
{:else}
  <div class="grid">
    <section class="panel head" data-testid="guild-header">
      <h1 class="guild-name">{myGuild?.Name ?? statistics.data?.GuildName}</h1>
      <p class="dim small head-stats">
        {#if myGuild}
          Tier {myGuild.CurrentTier} &middot; {myGuild.ActiveMembers}/{myGuild.MaxMembers} members
          &middot; {myGuild.TaxRatePct}% tax &middot;
        {/if}
        You: {ROLE_NAMES[myRole] ?? 'Member'}
        &middot; Weekly rank: {myWeeklyRank ? `#${myWeeklyRank}` : 'unranked'}
      </p>
    </section>

    <section class="panel">
      <h2>Members</h2>

      {#if roster.isPending}
        <p class="dim small">Loading the roster...</p>
      {:else if roster.isError}
        <QueryError query={roster} what="the guild roster" />
      {:else if members.length === 0}
        <p class="dim small">No members listed.</p>
      {:else}
        <ul class="members">
          {#each members as member (member.PlayerId)}
            <li>
              <span class="who" style="display: flex; gap: 0.5rem; align-items: center; width: 100%;">
                <PlayerAvatar playerId={member.PlayerId} size="sm" />
                <!-- A name opens the profile, as it does in chat and Friends. -->
                <button
                  class="name-link"
                  data-testid="guild-member-name"
                  use:profileLink={{ playerId: member.PlayerId, name: nameById.get(member.PlayerId) }}
                >
                  {nameById.get(member.PlayerId) ?? `Player #${member.PlayerId}`}
                </button>
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
                  <!-- Modul: two taps. Kicking was one, and a mis-tap on a phone
                       removed a guildmate with no way back. -->
                  <ConfirmButton small label="Kick" confirmLabel="Really kick?" disabled={busy} onConfirm={() => handleKick(member.PlayerId)} />
                {/if}
              </span>
            </li>
          {/each}
        </ul>
      {/if}

      {#if hasGuild}
        <div class="leave" data-testid="guild-leave">
          <ConfirmButton
            label="Leave guild"
            confirmLabel={leavePreview.data?.ClosesGuild ? 'Really close it?' : 'Really leave?'}
            disabled={busy || !leavePreview.data?.InGuild}
            onConfirm={handleLeave}
          />
          {#if leaveNote}
            <p class="dim small" data-testid="guild-leave-note">{leaveNote}</p>
          {/if}
        </div>
      {/if}
    </section>

    {#if myRole === 2}
      <GuildApplications />
    {/if}

    <section class="panel">
      <h2>Guild Treasury & Buffs</h2>
      {#if statistics.isPending}
        <Skeleton />
      {:else if statistics.isError && statistics.data === undefined}
        <QueryError query={statistics} what="your guild membership" />
      {:else if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.isError && guildDepot.data === undefined}
          <QueryError query={guildDepot} what="the guild treasury" />
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
                <span class="buff-title">
                  <!-- Modul: SVG rather than ▼/▶. A glyph is a font's opinion
                       about a shape, differs by family, is not guaranteed to
                       exist, and is announced by a screen reader as "black
                       right-pointing triangle" in the middle of a buff name. -->
                  <svg class="caret" class:open={isOpen} viewBox="0 0 12 12" aria-hidden="true">
                    <path d="M4.5 2 L8.5 6 L4.5 10" fill="none" stroke="currentColor"
                          stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
                  </svg>
                  {buff.label}
                </span>
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
                          {formatNumber(getDepotQty(td.commonWood))} / {formatNumber(BUFF_COST_PER_MAT)}
                        </span>
                      </div>
                      <div class="mat-req">
                        <span class="mat-name">{prettifyBaseId(td.commonOre)}</span>
                        <span class="mat-stock" class:mat-ok={getDepotQty(td.commonOre) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.commonOre) < BUFF_COST_PER_MAT}>
                          {formatNumber(getDepotQty(td.commonOre))} / {formatNumber(BUFF_COST_PER_MAT)}
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
                          {formatNumber(getDepotQty(td.rareWood))} / {formatNumber(BUFF_COST_PER_MAT)}
                        </span>
                      </div>
                      <div class="mat-req">
                        <span class="mat-name rare-mat">{prettifyBaseId(td.rareOre)}</span>
                        <span class="mat-stock" class:mat-ok={getDepotQty(td.rareOre) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.rareOre) < BUFF_COST_PER_MAT}>
                          {formatNumber(getDepotQty(td.rareOre))} / {formatNumber(BUFF_COST_PER_MAT)}
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
      <h2>Depot &amp; donations</h2>

      {#if statistics.isPending}
        <Skeleton />
      {:else if statistics.isError && statistics.data === undefined}
        <QueryError query={statistics} what="your guild membership" />
      {:else if !hasGuild}
        <p class="dim">Join a guild to use its depot.</p>
      {:else}
        <p class="dim small">
          Per-material stock against what the guild needs. Depositing moves the
          material out of your backpack permanently.
        </p>

        {#if logistics.isPending}
          <Skeleton />
        {:else if logistics.isError}
          <QueryError query={logistics} what="the depot requirements" />
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
                  label={`${formatNumber(stock)} / ${formatNumber(Number(row.TargetRequirement))}`}
                />
              </li>
            {/each}
          </ul>
        {/if}

        <h3>Gold</h3>
        <div class="row">
          <input type="number" min="1" step="100" bind:value={treasuryGold} aria-label="Gold to contribute" />
          <button disabled={!hasGuild || treasuryGold < 1} onclick={giveGold}>Contribute gold</button>
        </div>
        <!-- Modul: task 107. This said gold "raises your own contribution
             ranking" while the Contributors card said gold does not count. The
             server settles it: the donate route puts gold in the treasury and
             grants guild experience (GuildContributionEngine, the "gold"
             branch of ContributeDepotMaterialAsync) and writes no member
             points at all. Only materials earn the weekly ranking. -->
        <p class="dim tiny">
          Goes into the treasury and raises the guild's tier. It does not count toward the weekly ranking.
        </p>

        <h3>Materials</h3>
        <label class="pick">
          <span class="dim tiny">Material</span>
          <select bind:value={depotMaterial}>
            <option value="">Choose...</option>
            {#each Array.from(BUFF_MATERIAL_IDS) as baseId}
              {@const invItem = depositable.find(d => d.baseId === baseId)}
              <option value={baseId}>
                {prettifyBaseId(baseId)} (x{formatNumber(invItem?.quantity ?? 0)})
              </option>
            {/each}
            {#each depositable.filter(d => !BUFF_MATERIAL_IDS.has(d.baseId) && isLogOrOre(d.baseId)) as row}
              <option value={row.baseId}>
                {prettifyBaseId(row.baseId)} (x{formatNumber(row.quantity)})
              </option>
            {/each}
          </select>
        </label>

        <div class="row">
          <input type="number" min="1" max={depotMax || 1} bind:value={depotQuantity} aria-label="Quantity" />
          <button class="max-btn" disabled={depotMax === 0} onclick={() => (depotQuantity = depotMax)}>Max</button>
        </div>

        <!-- Modul: ONE destination choice and ONE button, replacing "To depot /
             To chain / Donate" - three controls of different widths that
             needed a paragraph to tell apart. The disabled rule follows the
             destination: depot and chain go through APIs that take a numeric
             definition id and refuse anything else, so they need a catalogued
             item; the treasury takes a base id but ALSO needs a catalogued
             item (GuildDepotBalances is keyed on ItemDefinitionId) and only
             the buff set. Four of the twenty used to be uncatalogued - see the
             note by BUFF_MATERIAL_IDS. -->
        <div class="dest" role="radiogroup" aria-label="Deposit to">
          <span class="dim tiny">Deposit to</span>
          {#each DEPOSIT_DESTINATIONS as d (d.key)}
            <button
              type="button"
              role="radio"
              class="dest-btn"
              class:active={depositDest === d.key}
              aria-checked={depositDest === d.key}
              onclick={() => (depositDest = d.key)}
            >{d.label}</button>
          {/each}
        </div>
        <button class="deposit-go" disabled={!canDeposit} onclick={depositSelected}>
          Deposit to {DEPOSIT_DESTINATIONS.find((d) => d.key === depositDest)?.label}
        </button>
        <p class="dim tiny" data-testid="deposit-hint">
          {DEPOSIT_DESTINATIONS.find((d) => d.key === depositDest)?.hint}
        </p>

        {#if inventory.isError && inventory.data === undefined}
          <QueryError query={inventory} what="your materials" />
        {:else if depositable.length === 0}
          <p class="dim tiny">You are not carrying any stackable materials.</p>
        {/if}
      {/if}
    </section>


    <section class="panel">
      <h2>Weekly material ranking</h2>
      {#if statistics.isPending}
        <Skeleton />
      {:else if statistics.isError && statistics.data === undefined}
        <QueryError query={statistics} what="your guild membership" />
      {:else if !hasGuild}
        <p class="dim">Join a guild to contribute.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.isError && guildDepot.data === undefined}
          <QueryError query={guildDepot} what="the guild treasury" />
        {:else if guildDepot.data}
          <div class="prize-info">
            <h3> Weekly Prizes</h3>
            <p class="dim tiny">Every week, <strong>50% of the guild treasury</strong> is distributed to the top 3 material contributors:</p>
            <ul class="prize-list">
              <li><span class="gold-text">1st place</span> — 25% of treasury</li>
              <li><span class="silver-text">2nd place</span> — 15% of treasury</li>
              <li><span class="bronze-text">3rd place</span> — 10% of treasury</li>
            </ul>
            <p class="dim tiny">Only materials deposited to the Treasury earn points here. Gold does not.</p>
          </div>

          <h3>This week</h3>
          {#if (guildDepot.data.Leaderboard ?? []).filter(m => m.WeeklyContributionPoints > 0).length === 0}
            <p class="dim small">No material contributions this week yet.</p>
          {:else}
            <ul class="members" style="margin-bottom: 1rem;">
              {#each (guildDepot.data.Leaderboard ?? []).filter(m => m.WeeklyContributionPoints > 0) as member, i}
                <li>
                  <span class="who">
                    {#if i === 0}{:else if i === 1}{:else if i === 2}{:else}#{i + 1}{/if}
                    <button class="name-link" use:profileLink={{ playerId: member.PlayerId, name: member.Name }}>{member.Name}</button>
                    {#if member.PlayerId === connection.currentPlayerId}<span class="dim tiny">you</span>{/if}
                  </span>
                  <span class="dim">{formatNumber(member.WeeklyContributionPoints)} pts</span>
                </li>
              {/each}
            </ul>
          {/if}


        {/if}
      {/if}
    </section>

    <!-- Modul: GUILD WAR IS ON HOLD, not removed - decided 2026-09-01, and
         docs/FUTURE_PLANS.md still lists it as planned. The panel is hidden
         rather than deleted so the handlers keep a caller; they are also the
         four remaining svelte-check errors, and that is the trade being made
         knowingly.

         NOTE the Logistics section below is inside this hidden panel too, so
         the "To chain" button in the visible Depot feeds a production bar
         players cannot see. That is a real inconsistency, not part of the
         hold - see the 2026-09-01 handoff. -->
    <section class="panel" style="display:none;">
      <h2>Raid</h2>

      {#if snap.GuildRaidBossMaxHp > 0}
        <p class="dim small">Tier {snap.GuildRaidTier}</p>
        <Bar
          value={Number(snap.GuildRaidBossCurrentHp)}
          max={Number(snap.GuildRaidBossMaxHp)}
          color="var(--danger)"
          label={`${formatNumber(Number(snap.GuildRaidBossCurrentHp))} / ${formatNumber(Number(snap.GuildRaidBossMaxHp))}`}
        />
      {:else}
        <p class="dim">No raid boss active.</p>
      {/if}

      <button disabled={!hasGuild} onclick={raid}>Launch raid</button>

      <!-- The Treasury block that stood here was a second copy of the
           Contribute gold control, superseded by the one in the Depot panel
           below. Both were bound to the same treasuryGold state and called the
           same giveGold, so they were never out of step - but only this one was
           inside display:none, which is the only reason players never saw two.
           Removed rather than left as the kind of duplicate that gets edited on
           one side. -->

      <h3>Logistics</h3>
      <div class="axis">
        <span class="dim tiny">Depot level {snap.GuildLogisticsLevel}</span>
        <Bar
          value={Number(snap.GuildLogisticsCurrentStock)}
          max={Math.max(1, Number(snap.GuildLogisticsTargetRequirement))}
          color="var(--accent)"
          label={`${formatNumber(Number(snap.GuildLogisticsCurrentStock))} / ${formatNumber(Number(snap.GuildLogisticsTargetRequirement))}`}
        />
      </div>
    </section>

    <section class="panel">
      <!-- Modul: task 107. This card used to be FIRST, so a phone opened the
           Guild tab on "unlock at 50 players, 1/50" and the roster sat 1,700px
           down. It is last now and, while locked, a one-line header that opens
           on tap. {#if}, not <details>: a closed <details> keeps its
           author-styled children live (client_web/CLAUDE.md). -->
      {#if warLocked && warLock.data}
        {@const lock = warLock.data}
        <button
          class="war-toggle"
          data-testid="guild-war-toggle"
          aria-expanded={warOpen}
          onclick={() => (warOpen = !warOpen)}
        >
          <span class="war-title">Guild war</span>
          <span class="dim tiny">Locked &middot; {lock.QualifyingPlayers} / {lock.RequiredPlayers} players</span>
        </button>
      {:else}
        <h2>Guild war</h2>
      {/if}

      {#if warLocked && warLock.data && warOpen}
        {@const lock = warLock.data}
        <p class="war-locked" data-testid="guild-war-locked">
          Guild Wars unlock at {lock.RequiredPlayers} players.
        </p>
        <div class="axis">
          <span class="dim tiny">Players at level {lock.MinimumLevel}+</span>
          <Bar
            value={Math.min(lock.QualifyingPlayers, lock.RequiredPlayers)}
            max={lock.RequiredPlayers}
            label={`${lock.QualifyingPlayers} / ${lock.RequiredPlayers}`}
          />
        </div>
        <div class="axis">
          <span class="dim tiny">Guilds with {lock.RequiredMembersPerGuild}+ such members</span>
          <Bar
            value={Math.min(lock.QualifyingGuilds, lock.RequiredGuilds)}
            max={lock.RequiredGuilds}
            label={`${lock.QualifyingGuilds} / ${lock.RequiredGuilds}`}
          />
        </div>
        <p class="dim tiny">
          Both are needed. Once reached, Guild Wars stay unlocked for good.
        </p>
      {:else if warLocked}
        <!-- collapsed -->
      {:else if warId <= 0}
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
              label={`${formatNumber(axis.ours)} vs ${formatNumber(axis.theirs)}`}
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
          Contributions are burned into the war effort.
          <!-- Modul: the command takes a numeric commodity id and there is no
               picker for it yet; this panel is hidden with the rest of Guild
               Wars until that is built. -->
        </p>
      {/if}
    </section>

  </div>
{/if}

<style>
  /* Modul: ONE COLUMN, on purpose. The auto-fit grid this replaced left a hole
     under the tall Depot card on a desktop, because a row is as tall as its
     tallest card. A phone was one column anyway; this makes the desktop agree
     and puts the cards in the order the dashboard is meant to be read. */
  .grid {
    display: grid;
    grid-template-columns: minmax(0, 44rem);
    justify-content: center;
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .guild-name {
    margin: 0 0 0.3rem;
    font-size: 1.4rem;
    line-height: 1.2;
    overflow-wrap: anywhere;
  }
  .head-stats {
    margin: 0;
  }

  .war-toggle {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.15rem;
    width: 100%;
    background: none;
    border: none;
    color: inherit;
    font: inherit;
    text-align: left;
    padding: 0;
    cursor: pointer;
  }
  .war-title {
    font-size: 1.05rem;
    font-weight: 600;
  }
  .war-toggle .tiny {
    margin: 0;
  }

  .pick {
    display: grid;
    gap: 0.2rem;
    margin-bottom: 0.5rem;
  }
  .pick select {
    width: 100%;
  }
  .max-btn {
    flex: none;
  }

  .dest {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem;
    margin: 0.2rem 0 0.6rem;
  }
  .dest .tiny {
    margin: 0 0.2rem 0 0;
  }
  .dest-btn {
    flex: 1 1 0;
    min-width: 4.5rem;
  }
  .dest-btn.active {
    background: color-mix(in srgb, var(--accent) 25%, transparent);
    border-color: var(--accent);
  }
  .deposit-go {
    width: 100%;
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

  /* overflow: hidden keeps the rounded corners clean - and is also why the
     tier rows CROPPED instead of scrolling when they were too wide to fit.
     Kept, because the rows wrap now and no longer overflow; if anything in
     here ever does again, expect it to vanish silently rather than announce
     itself. */
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
  @media (hover: hover) and (pointer: fine) {
    .buff-header:hover {
      background: color-mix(in srgb, var(--accent) 18%, transparent);
    }
  }

  .caret {
    width: 11px;
    height: 11px;
    transition: rotate 140ms ease;
  }

  /* rotate, not transform - see app.css on button:active for why. */
  .caret.open {
    rotate: 90deg;
  }

  .buff-title {
    font-weight: bold;
    font-size: 0.9rem;
  }

  /* Modul: WRAPS RATHER THAN OVERFLOWS. This was
     `grid-template-columns: 4rem 1fr 1fr`, and a `1fr` track has a minimum of
     min-content - so the two paths could never shrink below their longest
     label ("Golden Frostpine Log" plus "0 / 25 000"). The row forced itself
     wider than the panel and the rare column was clipped mid-word, which is
     how expanding a buff produced a cropped window.

     Flex with a basis instead of fixed tracks: the two paths sit side by side
     when there is room for both and stack when there is not, at whatever width
     the panel happens to be. No media query, because the panel's width depends
     on the grid it sits in rather than on the viewport - a breakpoint would be
     guessing at the wrong number. */
  .buff-tier-row {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
    padding: 0.35rem 0.5rem;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
    align-items: stretch;
    font-size: 0.75rem;
  }

  .buff-tier-row:last-child {
    border-bottom: none;
  }

  .tier-label {
    font-weight: 600;
    /* Never shrinks and never wraps its own tier number away. */
    flex: 0 0 3.75rem;
  }

  /* 11rem is about what one path needs before its names start ellipsising, so
     two of them fit side by side in a panel of roughly 26rem and stack below
     that. min-width: 0 is load-bearing - a flex item defaults to min-content
     and would refuse to shrink, which is the same trap the grid had. */
  .buff-path {
    display: flex;
    flex: 1 1 11rem;
    min-width: 0;
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

  /* The NAME is what gives way when space is tight - it ellipsises. The stock
     figure beside it must not, because "0 / 25 000" truncated to "0 / 2" reads
     as a different number rather than as a shortened one, which is exactly what
     the cropped panel was showing. max-width is gone: the flex basis above
     decides the width now, so a fixed cap only re-created the same clipping at
     a different size. */
  .mat-name {
    color: var(--text-dim);
    font-size: 0.72rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    min-width: 0;
    flex: 1 1 auto;
  }

  .mat-name.rare-mat {
    color: var(--accent);
  }

  .mat-stock {
    font-size: 0.7rem;
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
    /* Holds its full width while the name beside it gives way. */
    flex: 0 0 auto;
  }

  .mat-ok { color: var(--good); }
  .mat-low { color: var(--danger); }

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
  /* Modul: medal colours tuned for charred oak vanish on parchment - #c0c0c0
     on #f6edd8 is about 1.6:1. Same hues, darker, for the light theme (which
     app.css selects with prefers-color-scheme; there is no data-theme). */
  @media (prefers-color-scheme: light) {
    .gold-text   { color: #85650a; }
    .silver-text { color: #5b6672; }
    .bronze-text { color: #8a4b18; }
  }

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
    border-bottom: 1px solid var(--line);
  }

  .leave {
    margin-top: 0.9rem;
    padding-top: 0.7rem;
    border-top: 1px solid var(--border);
    display: grid;
    gap: 0.35rem;
    justify-items: start;
  }

  .leave p {
    margin: 0;
  }

  .members li:last-child {
    border-bottom: none;
  }

  .who {
    font-weight: 600;
  }

  /* A name that opens a profile: text, not a boxed button, at a thumb's height. */
  .name-link {
    background: none;
    border: 0;
    padding: 0 0.15rem;
    min-height: 44px;
    min-width: 0;
    flex-shrink: 1;
    color: inherit;
    font: inherit;
    font-weight: 600;
    text-align: left;
    overflow-wrap: anywhere;
    cursor: pointer;
    text-decoration: underline dotted;
    text-underline-offset: 0.2em;
  }

  .axis {
    display: grid;
    gap: 0.2rem;
    margin-bottom: 0.5rem;
  }

  .war-locked {
    margin: 0 0 0.6rem;
    font-weight: 600;
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
