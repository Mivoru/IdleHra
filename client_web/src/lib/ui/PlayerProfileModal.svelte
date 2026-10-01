<script lang="ts">
  import PlayerAvatar from './PlayerAvatar.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { authedGet } from '../net/auth';
  import ItemIcon from './ItemIcon.svelte';
  import { EQUIPMENT_SLOTS } from './slots';
  import { prettifyBaseId } from '../net/content';
  import { portal } from './portal';
  import { registerOverlay } from '../stores/sheet';
  import { LAYER_Z } from '../net/backButton';

  // Modul: `$props()`, NOT `export let`, AND THAT IS THE WHOLE BUG FIX.
  //
  // Reported as "I click show profile, it says fetching data, and I have to
  // close it and click again".
  //
  // A Svelte 5 component is in RUNES mode only if it uses a rune. This file
  // used `export let`, which is the Svelte 4 form, so the component compiled
  // as LEGACY - and legacy components track reactivity through the compiler's
  // own invalidation, not through signals. `createQuery` from
  // @tanstack/svelte-query builds its result out of runes, so the template
  // read `profile.isPending` exactly once, at mount, and never heard that the
  // request had finished. The spinner was not waiting on the network; it had
  // stopped listening.
  //
  // The second click worked because by then the query cache was WARM: the
  // fresh component's very first render already had the data, so a template
  // that only renders once was enough. That is why it looked intermittent
  // rather than broken.
  //
  // Nothing else in the file had to change - which is the dangerous part.
  // `export let` is not deprecated syntax that warns, it is a different
  // reactivity system that compiles cleanly and silently disagrees with any
  // rune-based library it is handed.
  const { playerId, onClose }: { playerId: number; onClose: () => void } = $props();
  const titleId = $props.id();

  interface ProfileEquipment {
    Id: number;
    BaseItemId: string;
    QualityTier: number;
    AffixPayload: any;
    IsAffixLocked: boolean;
    SetId: number;
  }

  interface ProfileCharacter {
    SlotIndex: number;
    Level: number;
    AgePhase: number;
    IsFemale: boolean;
    EquippedAxeId?: number;
    EquippedPickaxeId?: number;
    EquippedRodId?: number;
    EquippedWeaponId?: number;
    EquippedHelmetId?: number;
    EquippedChestId?: number;
    EquippedGlovesId?: number;
    EquippedLeggingsId?: number;
    EquippedBootsId?: number;
    EquippedAmuletId?: number;
    EquippedRingId?: number;
  }

  interface PlayerProfile {
    PlayerId: number;
    Username: string;
    /** The worn title's display name, resolved by the server, or null. */
    ActiveTitle: string | null;
    GuildId: number;
    CurrentLevel: number;
    LastLogoutTimestamp: number;
    Characters: ProfileCharacter[];
    Equipment: ProfileEquipment[];
  }

  async function fetchProfile(id: number) {
    return authedGet<PlayerProfile>(`/api/v1/players/profile?id=${id}`);
  }

  const profile = createQuery(() => ({
    queryKey: ['profile', playerId],
    queryFn: () => fetchProfile(playerId),
    staleTime: 60000,
  }));

  // Modul: ALL ELEVEN SLOTS, in EQUIPMENT_SLOTS' order. This showed four -
  // weapon, helmet, chest, leggings - while the endpoint has always returned
  // every worn piece including the amulet, the ring and the three tools, so a
  // fully dressed character looked half naked to anyone inspecting it. The
  // CLAUDE.md rule: every list that stopped short of eleven has been a bug.
  //
  // The profile's field per slot index, written out because the three tool
  // slots carry no `field` in EQUIPMENT_SLOTS (the hot-path wire does not
  // ship them) while this REST shape does. A slot added to EQUIPMENT_SLOTS
  // without a key here still renders - as an empty box, which is the thing to
  // look for.
  const PROFILE_FIELD: Record<number, keyof ProfileCharacter> = {
    0: 'EquippedWeaponId',
    1: 'EquippedHelmetId',
    2: 'EquippedChestId',
    3: 'EquippedGlovesId',
    4: 'EquippedLeggingsId',
    5: 'EquippedBootsId',
    6: 'EquippedAmuletId',
    7: 'EquippedRingId',
    8: 'EquippedAxeId',
    9: 'EquippedPickaxeId',
    10: 'EquippedRodId',
  };

  function wornIn(char: ProfileCharacter, slotIndex: number): ProfileEquipment | undefined {
    const field = PROFILE_FIELD[slotIndex];
    const id = field ? (char[field] as number | undefined) : undefined;
    if (!id) return undefined;
    return profile.data?.Equipment.find((e: ProfileEquipment) => e.Id === id);
  }

  // Modul: BACK CLOSES THE PROFILE, not the screen it was opened from. It was
  // left out of the back handler because its open state is local to the
  // screens that open it (Friends, Leaderboards, chat) - which is what the
  // overlay stack is for: the modal registers itself while it is mounted.
  $effect(() => registerOverlay(() => onClose(), LAYER_Z.playerProfile));

  function getAge(phase: number) {
    switch (phase) {
      case 0: return 'Child';
      case 1: return 'Adult';
      case 2: return 'Senior';
      case 3: return 'Elder';
      default: return 'Unknown';
    }
  }
</script>

<!-- Modul: PORTALLED TO <body>. Chat renders this inside the chat dock, whose
     window was blurred - and a backdrop-filter makes an ancestor the box a
     position: fixed child is laid out in, so the "full-screen" overlay was the
     size of the ~416px chat window and clipped by it. See ui/portal.ts. -->
<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="overlay" onclick={onClose} use:portal>
  <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
  <div
    class="modal"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby={titleId}
    onclick={(e) => e.stopPropagation()}
  >
    <div class="header">
      <!-- Task 54: the profile is where a face is shown largest. -->
      <PlayerAvatar {playerId} size="lg" name={profile.data?.Username ?? ''} />
      <h3 id={titleId}>
        {profile.data ? `${profile.data.Username}'s Profile` : 'Loading Profile...'}
        {#if profile.data?.ActiveTitle}
          <span class="title-badge">{profile.data.ActiveTitle}</span>
        {/if}
      </h3>
      <button class="close-btn" aria-label="Close profile" onclick={onClose}>&times;</button>
    </div>
    
    <div class="content">
      {#if profile.isPending}
        <p class="dim">Fetching profile data...</p>
      {:else if profile.isError}
        <p class="err">Could not load profile. {profile.error?.message}</p>
      {:else if profile.data}
        {@const p = profile.data}
        <div class="profile">
          <div class="meta">
            <p><strong>Account Level:</strong> {p.CurrentLevel}</p>
            <p><strong>Last Online:</strong> {new Date(p.LastLogoutTimestamp * 1000).toLocaleString()}</p>
          </div>

          <div class="characters">
            {#each p.Characters as char (char.SlotIndex)}
              <div class="character-card">
                <h4>Character {char.SlotIndex + 1}</h4>
                <p class="dim tiny">Level {char.Level} • {char.IsFemale ? 'Female' : 'Male'} • {getAge(char.AgePhase)}</p>
                
                <div class="equipment-grid">
                  {#each EQUIPMENT_SLOTS as slot (slot.index)}
                    {@const item = wornIn(char, slot.index)}
                    {@const itemLabel = item ? prettifyBaseId(item.BaseItemId) : ''}
                    <!-- The name is written under the icon, not left in a
                         tooltip: a phone has no hover, and this is a screen
                         people open to see what someone is wearing. -->
                    <div class="slot" data-slot={slot.index}>
                      <span class="tiny dim">{slot.label}</span>
                      {#if item}
                        <ItemIcon baseItemId={item.BaseItemId} name={itemLabel} qualityTier={item.QualityTier} size="md" />
                        <span class="item-name">{itemLabel}</span>
                      {:else}
                        <div class="empty-slot"></div>
                      {/if}
                    </div>
                  {/each}
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}
    </div>
  </div>
</div>

<style>
  /* A title (task 37) sits beside the name, in the name's own line. */
  .title-badge {
    display: inline-block;
    margin-left: 0.4rem;
    font-size: 0.75em;
    font-style: italic;
    color: var(--accent, #d9c48b);
    font-weight: 600;
  }

  /* z-index 1000 is LAYER_Z.playerProfile in net/backButton.ts. */
  .overlay {
    position: fixed;
    inset: 0;
    /* TODO(tokens): --scrim once app.css defines it. */
    background: rgba(0, 0, 0, 0.6);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
    /* Fixed, so body's safe-area padding does not reach it - its own inset. */
    padding: calc(0.5rem + var(--sa-top)) calc(0.5rem + var(--sa-right)) calc(0.5rem + var(--sa-bottom))
      calc(0.5rem + var(--sa-left));
  }
  /* Modul: THE APP'S TOKENS. This read --bg-surface and --bg-dark, which the
     theme never defines, so it fell back to a hard-coded #1e1e1e - and the
     light theme's dark ink text sat on it: dark on dark, the same defect
     ContextMenu had. */
  .modal {
    background: var(--bg-panel);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    width: 100%;
    max-width: 500px;
    max-height: 90vh;
    /* dvh after vh: the vh line is the fallback for an engine without it. On
       mobile web vh is the LARGE viewport, taller than what shows under the
       URL bar. */
    max-height: calc(100dvh - 1rem - var(--sa-top) - var(--sa-bottom));
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }
  .header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.75rem;
    padding: 1rem;
    border-bottom: 1px solid var(--border);
    background: var(--bg-raised);
    flex-shrink: 0;
  }
  .header h3 {
    flex: 1;
    min-width: 0;
    margin: 0;
    font-size: 1.1rem;
  }
  .close-btn {
    background: transparent;
    border: none;
    color: var(--text);
    font-size: 1.5rem;
    cursor: pointer;
    line-height: 1;
    padding: 0 0.5rem;
    /* 44px on every width: it was ~24px on a desktop, and this is the only
       visible way out of the modal. */
    flex-shrink: 0;
    min-width: 44px;
    min-height: 44px;
  }
  .close-btn:hover {
    color: var(--danger);
  }
  .content {
    padding: 1rem;
    overflow-y: auto;
    overscroll-behavior: contain;
    min-height: 0;
  }
  .profile {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .meta {
    padding: 0.5rem;
    background: var(--bg-raised);
    border-radius: 4px;
    border: 1px solid var(--border);
  }
  .characters {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }
  .character-card {
    padding: 0.5rem;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: var(--bg);
  }
  /* Eleven slots wrap; a flex row of eleven ran off a phone. */
  .equipment-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(4.2rem, 1fr));
    gap: 0.5rem;
    margin-top: 0.5rem;
  }
  .slot {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25rem;
    min-width: 0;
    text-align: center;
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
  .err {
    color: var(--danger);
  }
</style>
