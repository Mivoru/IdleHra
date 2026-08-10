<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { authedGet } from '../net/auth';
  import ItemIcon from './ItemIcon.svelte';

  export let playerId: number;
  export let onClose: () => void;

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

  function getEquip(id?: number | null): ProfileEquipment | undefined {
    if (!id) return undefined;
    return profile.data?.Equipment.find((e: ProfileEquipment) => e.Id === id);
  }

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

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="overlay" onclick={onClose}>
  <div class="modal" onclick={(e) => e.stopPropagation()}>
    <div class="header">
      <h3>{profile.data ? `${profile.data.Username}'s Profile` : 'Loading Profile...'}</h3>
      <button class="close-btn" onclick={onClose}>&times;</button>
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
                  <div class="slot">
                    <span class="tiny dim">Weapon</span>
                    {#if char.EquippedWeaponId && getEquip(char.EquippedWeaponId)}
                      {@const item = getEquip(char.EquippedWeaponId)!}
                      <ItemIcon baseItemId={item.BaseItemId} name={item.BaseItemId} qualityTier={item.QualityTier} size="md" />
                    {:else}
                      <div class="empty-slot"></div>
                    {/if}
                  </div>
                  <div class="slot">
                    <span class="tiny dim">Helmet</span>
                    {#if char.EquippedHelmetId && getEquip(char.EquippedHelmetId)}
                      {@const item = getEquip(char.EquippedHelmetId)!}
                      <ItemIcon baseItemId={item.BaseItemId} name={item.BaseItemId} qualityTier={item.QualityTier} size="md" />
                    {:else}
                      <div class="empty-slot"></div>
                    {/if}
                  </div>
                  <div class="slot">
                    <span class="tiny dim">Chest</span>
                    {#if char.EquippedChestId && getEquip(char.EquippedChestId)}
                      {@const item = getEquip(char.EquippedChestId)!}
                      <ItemIcon baseItemId={item.BaseItemId} name={item.BaseItemId} qualityTier={item.QualityTier} size="md" />
                    {:else}
                      <div class="empty-slot"></div>
                    {/if}
                  </div>
                  <div class="slot">
                    <span class="tiny dim">Leggings</span>
                    {#if char.EquippedLeggingsId && getEquip(char.EquippedLeggingsId)}
                      {@const item = getEquip(char.EquippedLeggingsId)!}
                      <ItemIcon baseItemId={item.BaseItemId} name={item.BaseItemId} qualityTier={item.QualityTier} size="md" />
                    {:else}
                      <div class="empty-slot"></div>
                    {/if}
                  </div>
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
  .overlay {
    position: fixed;
    top: 0; left: 0; right: 0; bottom: 0;
    background: rgba(0,0,0,0.6);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
  }
  .modal {
    background: var(--bg-surface, #1e1e1e);
    border: 1px solid var(--border);
    border-radius: 8px;
    width: 90%;
    max-width: 500px;
    max-height: 90vh;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }
  .header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 1rem;
    border-bottom: 1px solid var(--border);
    background: var(--bg-dark);
  }
  .header h3 {
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
  }
  .close-btn:hover {
    color: var(--danger);
  }
  .content {
    padding: 1rem;
    overflow-y: auto;
  }
  .profile {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .meta {
    padding: 0.5rem;
    background: var(--bg-dark);
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
    background: var(--bg-light, rgba(255,255,255,0.02));
  }
  .equipment-grid {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.5rem;
  }
  .slot {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25rem;
  }
  .empty-slot {
    width: 32px;
    height: 32px;
    border: 1px dashed var(--border);
    border-radius: 2px;
    opacity: 0.3;
  }
  .err {
    color: var(--danger);
  }
</style>
