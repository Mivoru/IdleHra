<script lang="ts">
  // Modul: SKILLS BELONG TO THE CHARACTER, not to the village.
  //
  // These are combat abilities - they spend mana, they have cooldowns, they
  // multiply the next hit - and they lived under Village, between the building
  // queue and the mentor slots. Same class of problem as the affix reroll being
  // in the Forge: the feature worked, and a player looking for it had no reason
  // to look where it was.
  //
  // Extracted rather than copied so there is one implementation of unlocking
  // and casting, whichever screen renders it.
  import { playerState, pushLocalNotice } from '../stores/game';
  import { unlockSkill, castSkill, MAX_SKILL_ID } from '../net/commands';
  import type { StateUpdate } from '../net/protocol.generated';
  import Bar from './Bar.svelte';

  const snap = $derived($playerState);

  const skills = $derived(
    snap
      ? Array.from({ length: MAX_SKILL_ID }, (_, index) => {
          const id = index + 1;
          // The four cooldowns are separate wire fields, so the name is
          // computed rather than indexed - see StateUpdatePacket.
          const cooldownField = `Skill${id}CooldownRemainingMs` as keyof StateUpdate;
          const cooldown = snap[cooldownField];
          return {
            id,
            unlocked: (snap.UnlockedSkillsBitmask & (1 << index)) !== 0,
            cooldownMs: typeof cooldown === 'number' ? cooldown : 0,
          };
        })
      : [],
  );

  function unlock(skillId: number) {
    const outcome = unlockSkill(skillId, snap?.AvailableSkillPoints ?? 0);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  function cast(skillId: number) {
    const outcome = castSkill(skillId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

{#if snap}
  <div class="head">
    <h3>Skills</h3>
    <span class="dim tiny">{snap.AvailableSkillPoints} points</span>
  </div>

  <div class="mana">
    <span class="dim tiny">Mana</span>
    <Bar
      value={snap.CurrentMana}
      max={Math.max(1, snap.MaxMana)}
      color="var(--accent)"
      label={`${snap.CurrentMana} / ${snap.MaxMana}`}
    />
  </div>

  <ul class="skills">
    {#each skills as skill (skill.id)}
      <li>
        <span class="name">Skill {skill.id}</span>
        {#if !skill.unlocked}
          <span class="dim tiny">locked</span>
          <button
            class="tiny-btn"
            disabled={snap.AvailableSkillPoints <= 0}
            title={snap.AvailableSkillPoints <= 0 ? 'Skill points come from levelling' : ''}
            onclick={() => unlock(skill.id)}
          >
            Unlock
          </button>
        {:else if skill.cooldownMs > 0}
          <span class="dim tiny">{(skill.cooldownMs / 1000).toFixed(1)}s</span>
          <button class="tiny-btn" disabled>Cooling</button>
        {:else}
          <span class="dim tiny">ready</span>
          <button class="tiny-btn" onclick={() => cast(skill.id)}>Cast</button>
        {/if}
      </li>
    {/each}
  </ul>
{/if}

<style>
  .head {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
    margin-bottom: 0.4rem;
  }
  .head h3 { margin: 0; }
  .head .dim { margin-left: auto; }

  .mana { display: grid; gap: 0.2rem; margin-bottom: 0.6rem; }

  .skills {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.35rem;
  }

  .skills li {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    font-size: 0.88rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }
  .skills li:last-child { border-bottom: none; }

  .name { font-weight: 600; }
  .skills li .dim { margin-left: auto; }

  .dim { opacity: 0.75; }
  .tiny { font-size: 0.8rem; }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.5rem;
  }
</style>
