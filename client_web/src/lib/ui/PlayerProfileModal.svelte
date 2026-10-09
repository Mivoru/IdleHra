<script lang="ts">
  // Modul: THE ONE PROFILE HOST. Mounted once in App; any name in the game
  // calls `openProfile` (stores/profile.ts, usually through the `profileLink`
  // action) and this draws whatever is on top of the stack - a player, or a
  // guild reached from a player. It used to be mounted per screen off each
  // screen's local state, which is why the guild roster never had one.
  //
  // Still `$props()`-free and rune-based on purpose: this file once used
  // `export let`, compiled as a LEGACY component, and never heard its
  // `createQuery` finish ("it says fetching data, and I have to close it and
  // click again"). tests/runesMode.test.ts guards that.
  import { createQuery } from '@tanstack/svelte-query';
  import PlayerAvatar from './PlayerAvatar.svelte';
  import PlayerTitle from './PlayerTitle.svelte';
  import ItemIcon from './ItemIcon.svelte';
  import Affixes from './Affixes.svelte';
  import DetailSheet from './DetailSheet.svelte';
  import Skeleton from './Skeleton.svelte';
  import { EQUIPMENT_SLOTS } from './slots';
  import { prettifyBaseId } from '../net/content';
  import { rarityColor, rarityName } from './rarity';
  import { formatNumber } from './format';
  import Modal from './Modal.svelte';
  import { profileLink, guildLink } from './profileLink';
  import { registerOverlay } from '../stores/sheet';
  import { LAYER_Z } from '../net/backButton';
  import {
    profileStack,
    popProfile,
    closeProfiles,
    PROFILE_STALE_MS,
    type ProfileEntry,
  } from '../stores/profile';
  import {
    queryKeys,
    fetchPlayerProfile,
    fetchGuildView,
    type ProfilePiece,
    type ProfileCharacter,
  } from '../net/rest';

  const titleId = $props.id();

  const top = $derived<ProfileEntry | null>($profileStack.length > 0 ? $profileStack[$profileStack.length - 1] : null);
  const depth = $derived($profileStack.length);
  const playerId = $derived(top?.kind === 'player' ? top.playerId : 0);
  const guildId = $derived(top?.kind === 'guild' ? top.guildId : 0);

  const profile = createQuery(() => ({
    queryKey: queryKeys.playerProfile(playerId),
    queryFn: () => fetchPlayerProfile(playerId),
    enabled: playerId > 0,
    staleTime: PROFILE_STALE_MS,
  }));

  const guild = createQuery(() => ({
    queryKey: queryKeys.guildView(guildId),
    queryFn: () => fetchGuildView(guildId),
    enabled: guildId > 0,
    staleTime: PROFILE_STALE_MS,
  }));

  // Modul: BACK POPS ONE LEVEL - guild back to the profile it came from - and
  // only while something is open, so the stack in stores/sheet.ts never holds
  // a closer for an overlay that is not on screen.
  $effect(() => {
    if (depth === 0) return;
    return registerOverlay(() => popProfile(), LAYER_Z.playerProfile);
  });

  // Selected piece for the stats sheet. Cleared whenever the entry changes.
  let piece = $state<ProfilePiece | null>(null);
  $effect(() => {
    void top;
    piece = null;
  });

  const ROLE_NAMES: Record<number, string> = { 0: 'Member', 1: 'Officer', 2: 'Leader' };
  const AGE = ['Child', 'Adult', 'Senior', 'Elder'];

  function wornIn(char: ProfileCharacter, slotIndex: number): ProfilePiece | undefined {
    return char.Worn.find((p) => p.SlotIndex === slotIndex);
  }

  function hours(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    return h >= 1 ? `${formatNumber(h)} h` : `${Math.floor(seconds / 60)} min`;
  }

  function lastSeen(epoch: number, online: boolean): string {
    if (online) return 'Online now';
    if (!epoch) return 'Never seen';
    return `Last seen ${new Date(epoch * 1000).toLocaleString()}`;
  }

  function timeLeft(epoch: number): string {
    const minutes = Math.max(0, Math.round((epoch * 1000 - Date.now()) / 60000));
    return minutes >= 60 ? `${Math.floor(minutes / 60)} h ${minutes % 60} min` : `${minutes} min`;
  }

  // The name the shell shows before the answer: what the tapped row knew.
  const shellName = $derived(
    top?.kind === 'player'
      ? (profile.data?.Username ?? top.name ?? '')
      : top?.kind === 'guild'
        ? (guild.data?.Name ?? top.name ?? '')
        : '',
  );
</script>

{#if top}
  <!-- Modul: PORTALLED TO <body> (Modal does it). Chat opens this from inside
       the chat dock, whose window is blurred - and a backdrop-filter makes an
       ancestor the box a position: fixed child is laid out in, so the
       "full-screen" overlay was the size of the chat window. See ui/portal.ts.
       The scrim closes the whole stack; back and Escape pop one level through
       the registerOverlay above, so the Modal registers nothing itself. -->
  <Modal
    labelledby={titleId}
    z={LAYER_Z.playerProfile}
    width="500px"
    flush
    layout="block"
    register={false}
    onClose={closeProfiles}
    class="modal"
    testid={top.kind === 'player' ? 'player-profile' : 'guild-view'}
  >
    {#snippet header()}
      <div class="header">
        {#if depth > 1}
          <button class="back-btn" aria-label="Back" data-testid="profile-back" onclick={popProfile}>&lsaquo;</button>
        {/if}
        {#if top.kind === 'player'}
          <PlayerAvatar playerId={top.playerId} size="lg" name={shellName} />
        {/if}
        <h3 id={titleId}>
          {#if top.kind === 'guild'}<span class="dim kind">Guild</span>{/if}
          <span class="who" data-testid="profile-name">{shellName || (top.kind === 'player' ? 'Player' : 'Guild')}</span>
          {#if top.kind === 'player'}
            <PlayerTitle playerId={top.playerId} />
          {/if}
        </h3>
        <button class="close-btn" aria-label="Close profile" onclick={closeProfiles}>&times;</button>
      </div>
    {/snippet}

      <div class="content">
        {#if top.kind === 'player'}
          {#if profile.isPending}
            <Skeleton />
          {:else if profile.isError}
            <p class="err">Could not load this profile. {profile.error?.message}</p>
          {:else if profile.data}
            {@const p = profile.data}
            <div class="facts">
              <span class="fact" data-testid="profile-level"><b>Level {p.Level}</b></span>
              {#if p.Guild}
                <button
                  class="guild-link"
                  data-testid="profile-guild-link"
                  use:guildLink={{ guildId: p.Guild.GuildId, name: p.Guild.Name }}
                >
                  {p.Guild.Name}
                </button>
                <span class="dim small">{ROLE_NAMES[p.Guild.Role] ?? 'Member'} &middot; tier {p.Guild.Tier}</span>
              {:else}
                <span class="dim small">No guild</span>
              {/if}
              <span class="dim small" class:online={p.IsOnline}>{lastSeen(p.LastLogoutTimestamp, p.IsOnline)}</span>
            </div>

            {#each p.Characters as char (char.SlotIndex)}
              <section class="character-card" data-testid="profile-character">
                <h4>
                  {char.Name || `Character ${char.SlotIndex + 1}`}
                  <span class="dim tiny">
                    {char.IsMain ? 'Main' : ''}{char.IsMain ? ' · ' : ''}{char.Activity} · {char.IsFemale ? 'Female' : 'Male'} · {AGE[char.AgePhase] ?? ''}
                  </span>
                </h4>
                <div class="equipment-grid">
                  {#each EQUIPMENT_SLOTS as slot (slot.index)}
                    {@const item = wornIn(char, slot.index)}
                    {@const itemLabel = item ? prettifyBaseId(item.BaseItemId) : ''}
                    <!-- The name is written under the icon, not left in a
                         tooltip: a phone has no hover. A tap shows its stats. -->
                    <div class="slot" data-slot={slot.index}>
                      <span class="tiny dim">{slot.label}</span>
                      {#if item}
                        <button class="piece" aria-label={`${itemLabel}, show stats`} onclick={() => (piece = item)}>
                          <ItemIcon baseItemId={item.BaseItemId} name={itemLabel} qualityTier={item.QualityTier} size="md" />
                          <span class="item-name">{itemLabel}</span>
                        </button>
                      {:else}
                        <div class="empty-slot"></div>
                      {/if}
                    </div>
                  {/each}
                </div>
              </section>
            {/each}
            {#if p.MoreEquippedCharacters > 0}
              <p class="dim tiny">and {p.MoreEquippedCharacters} more villagers carrying gear.</p>
            {/if}

            <section class="stats" data-testid="profile-stats">
              <h4>Statistics</h4>
              <dl>
                <dt>Monsters slain</dt><dd>{formatNumber(p.Stats.TotalKills)}</dd>
                <dt>Bosses slain</dt><dd>{formatNumber(p.Stats.BossesSlain)}</dd>
                <dt>Regions completed</dt><dd>{p.Stats.RegionsCompleted} / 5</dd>
                <dt>Achievements</dt><dd>{formatNumber(p.Stats.AchievementsClaimed)}</dd>
                <dt>Play time</dt><dd>{hours(p.Stats.TotalPlayTimeSeconds)}</dd>
                <dt>Best hit</dt><dd>{formatNumber(p.Stats.BestHit)}</dd>
                {#if p.Stats.BestDropBaseId}
                  <dt>Best drop</dt>
                  <dd style:color={rarityColor(p.Stats.BestDropTier)}>{rarityName(p.Stats.BestDropTier)} {prettifyBaseId(p.Stats.BestDropBaseId)}</dd>
                {/if}
                <dt>Deepest Delve floor</dt><dd>{p.Stats.DelveDeepestFloor}</dd>
                <dt>Rebirths</dt><dd>{p.Stats.RebirthCount}</dd>
                <dt>Seals (Book of Deeds)</dt><dd>{p.Stats.SealsEarned}</dd>
                {#if p.Stats.BestSeasonRank > 0}<dt>Best season rank</dt><dd>#{p.Stats.BestSeasonRank}</dd>{/if}
                <dt>Items crafted</dt><dd>{formatNumber(p.Stats.TotalItemsCrafted)}</dd>
                <dt>Deaths</dt><dd>{formatNumber(p.Stats.TotalDeaths)}</dd>
                <dt>Mastery (wood / ore / fish)</dt>
                <dd>{p.Stats.WoodcuttingMasteryLevel} / {p.Stats.MiningMasteryLevel} / {p.Stats.FishingMasteryLevel}</dd>
              </dl>
            </section>
          {/if}
        {:else}
          {#if guild.isPending}
            <Skeleton />
          {:else if guild.isError}
            <p class="err">Could not load this guild. {guild.error?.message}</p>
          {:else if guild.data}
            {@const g = guild.data}
            <div class="facts">
              <span class="fact"><b>Tier {g.Tier}</b></span>
              <span class="small">{g.ActiveMembers}/{g.MaxMembers} members</span>
              <span class="small" data-testid="guild-view-rank">Rank #{g.Rank} &middot; rating {formatNumber(g.Rating)}</span>
              {#if g.ViewerIsMember}<span class="dim small">Your guild</span>{/if}
            </div>
            <section class="stats">
              <h4>Guild</h4>
              <dl>
                <dt>This week's points</dt><dd>{formatNumber(g.WeeklyPoints)}</dd>
                <dt>Joining</dt><dd>{g.JoinType === 0 ? 'Open' : `By application, level ${g.MinApplicationLevel}+`}</dd>
                <dt>Tax</dt><dd>{g.TaxRatePct}%</dd>
                <dt>Monoliths (wood / ore)</dt><dd>{g.WoodcuttingMonolithLevel} / {g.MiningMonolithLevel}</dd>
                <dt>Active buffs</dt>
                <dd>
                  {#if g.ActiveBuffs.length === 0}
                    none
                  {:else}
                    {#each g.ActiveBuffs as b (b.BuffType)}
                      <span class="buff">{b.BuffType} T{b.Tier} ({timeLeft(b.ExpiresAtEpoch)})</span>
                    {/each}
                  {/if}
                </dd>
              </dl>
            </section>
            <section>
              <h4>Members</h4>
              <ul class="members">
                {#each g.Members as m (m.PlayerId)}
                  <li>
                    <span class="dot" class:online={m.IsOnline} title={m.IsOnline ? 'Online' : 'Offline'}></span>
                    <PlayerAvatar playerId={m.PlayerId} size="sm" name={m.Username} />
                    <button
                      class="name-btn"
                      data-testid="guild-view-member"
                      use:profileLink={{ playerId: m.PlayerId, name: m.Username }}
                    >
                      {m.Username}
                    </button>
                    <span class="dim tiny">lv {m.Level} &middot; {ROLE_NAMES[m.Role] ?? 'Member'}</span>
                  </li>
                {/each}
              </ul>
            </section>
          {/if}
        {/if}
      </div>
  </Modal>

  {#if piece}
    {@const label = prettifyBaseId(piece.BaseItemId)}
    <DetailSheet title={label} onClose={() => (piece = null)} testid="profile-item-sheet">
      <div class="piece-head">
        <ItemIcon baseItemId={piece.BaseItemId} name={label} qualityTier={piece.QualityTier} size="lg" />
        <span style:color={rarityColor(piece.QualityTier)}>{rarityName(piece.QualityTier)}</span>
      </div>
      <Affixes affixes={piece.Affixes} baseItemId={piece.BaseItemId} qualityTier={piece.QualityTier} />
    </DetailSheet>
  {/if}
{/if}

<style>
  .header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.75rem;
    border-bottom: 1px solid var(--border);
    background: var(--bg-raised);
    flex-shrink: 0;
  }
  .header h3 {
    flex: 1;
    min-width: 0;
    margin: 0;
    font-size: 1.1rem;
    overflow-wrap: anywhere;
  }
  .kind {
    display: block;
    font-size: 0.7rem;
    font-weight: normal;
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }
  .close-btn,
  .back-btn {
    background: transparent;
    border: none;
    color: var(--text);
    font-size: 1.5rem;
    cursor: pointer;
    line-height: 1;
    padding: 0 0.5rem;
    /* 44px on every width: these are the only visible ways out. */
    flex-shrink: 0;
    min-width: 44px;
    min-height: 44px;
  }
  .close-btn:hover {
    color: var(--danger);
  }
  .content {
    padding: 0.75rem;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }
  .facts {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.35rem 0.75rem;
    padding: 0.5rem;
    background: var(--bg-raised);
    border-radius: 4px;
    border: 1px solid var(--border);
  }
  .online {
    color: var(--good);
  }
  .guild-link {
    min-height: 44px;
    flex-shrink: 0;
    font-weight: 600;
  }
  .character-card {
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: var(--bg);
  }
  .character-card h4,
  .stats h4,
  section h4 {
    margin: 0 0 0.35rem;
  }
  /* Eleven slots wrap; a flex row of eleven ran off a phone. */
  .equipment-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(4.2rem, 1fr));
    gap: 0.5rem;
  }
  .slot {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25rem;
    min-width: 0;
    text-align: center;
  }
  .piece {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.2rem;
    background: transparent;
    border: 0;
    padding: 0;
    min-width: 44px;
    min-height: 44px;
    max-width: 100%;
    color: inherit;
    cursor: pointer;
  }
  .item-name {
    font-size: 0.62rem;
    line-height: 1.2;
    color: var(--text-dim);
    max-width: 100%;
    overflow-wrap: anywhere;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  /* The size of a 'md' ItemIcon, so a filled slot and an empty one line up. */
  .empty-slot {
    width: 2.6rem;
    height: 2.6rem;
    border: 1px dashed var(--border);
    border-radius: 2px;
    opacity: 0.3;
  }
  .stats dl {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 0.2rem 0.75rem;
    margin: 0;
    font-size: 0.85rem;
  }
  .stats dt {
    color: var(--text-dim);
  }
  .stats dd {
    margin: 0;
    text-align: right;
    overflow-wrap: anywhere;
  }
  .buff {
    display: block;
  }
  .members {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }
  .members li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
  }
  .dot {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--text-dim);
    flex-shrink: 0;
  }
  .dot.online {
    background: var(--good);
  }
  .name-btn {
    background: transparent;
    border: 0;
    color: var(--text);
    text-align: left;
    padding: 0 0.25rem;
    min-height: 44px;
    min-width: 0;
    overflow-wrap: anywhere;
    cursor: pointer;
    text-decoration: underline dotted;
  }
  .piece-head {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.5rem;
  }
  .err {
    color: var(--danger);
  }
</style>
