<script lang="ts">
  // Modul: TASK 85, "ORDERS" - up to three automation rules per character,
  // one slot opening at each of level 20, 40 and 60.
  //
  // Everything here is the SERVER's (GET /api/v1/automation-rules): the level,
  // which slots are open, which spots are fishing spots, the highest fusion
  // tier. The panel holds a draft and posts it; the server validates it
  // (AutomationRules.Validate) and answers 200 with a Result, so a refusal is
  // said on the panel rather than swallowed.
  //
  // The rules act on the server - on a death, on a dry larder, on drops
  // landing - and the offline catch-up applies the same ones, so an order set
  // here also holds while the player is away.
  //
  // The selects are static lists (rule kinds, five spots, fourteen tiers):
  // nothing inside them ticks, which is what breaks a <select> on Android.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchAutomationRules,
    saveAutomationRules,
    AUTOMATION_RULE,
    type AutomationRule,
  } from '../net/rest';
  import { pushLocalNotice, playerState } from '../stores/game';
  import { locationName } from './locations';
  import { rarityName } from './rarity';

  // Modul: TASK 97 - the Character screen's person switcher drives this panel,
  // so it shows the ONE person on screen. Without a characterId it lists every
  // fielded character, as it did before.
  const { characterId }: { characterId?: string } = $props();

  const client = useQueryClient();
  const view = createQuery(() => ({ queryKey: queryKeys.automationRules, queryFn: fetchAutomationRules }));
  const v = $derived(view.data);
  const snap = $derived($playerState);
  const reached = $derived(Number(snap?.HighestLocationReached ?? 1));

  // One draft per character, filled from the server and replaced whenever the
  // server's answer changes (after a save, or a refetch).
  let drafts = $state<Record<string, AutomationRule[]>>({});
  let saving = $state<string | null>(null);
  let lastResult = $state<Record<string, string>>({});

  $effect(() => {
    if (!v) return;
    const next: Record<string, AutomationRule[]> = {};
    for (const c of v.Characters) next[c.CharacterId] = c.Rules.map((r) => ({ Type: r.Type, Param: r.Param }));
    drafts = next;
  });

  const RULE_LABELS: Record<number, string> = {
    [AUTOMATION_RULE.None]: 'No order',
    [AUTOMATION_RULE.FishWhenLarderDry]: 'When the larder runs dry, go fishing',
    [AUTOMATION_RULE.StepDownOnDeath]: 'After a death, fight one monster easier',
    [AUTOMATION_RULE.AutoFuseToTier]: 'Fuse new drops up to a tier',
  };

  const REFUSALS: Record<string, string> = {
    SlotLocked: 'That slot is not open at your level yet.',
    DuplicateRule: 'Each order can be given once per character.',
    NotAFishingSpot: 'Pick a fishing spot.',
    TierOutOfRange: 'Pick a tier to fuse up to.',
    NotFielded: 'That character is not in one of your three slots.',
    UnknownRule: 'The server does not know that order.',
    ParamWithoutRule: 'That slot has a setting but no order.',
  };

  function defaultParam(type: number): number {
    if (type === AUTOMATION_RULE.FishWhenLarderDry) return v?.FishingSpots[0]?.ActivityId ?? 0;
    if (type === AUTOMATION_RULE.AutoFuseToTier) return 2;
    return 0;
  }

  function setType(characterId: string, slot: number, type: number) {
    const rules = drafts[characterId];
    if (!rules) return;
    rules[slot] = { Type: type, Param: defaultParam(type) };
  }

  function setParam(characterId: string, slot: number, param: number) {
    const rules = drafts[characterId];
    if (!rules) return;
    rules[slot] = { ...rules[slot], Param: param };
  }

  /** Kinds already taken by another slot of the same character - each order once. */
  function takenElsewhere(characterId: string, slot: number, type: number): boolean {
    const rules = drafts[characterId] ?? [];
    return type !== AUTOMATION_RULE.None && rules.some((r, i) => i !== slot && r.Type === type);
  }

  function dirty(characterId: string): boolean {
    const saved = v?.Characters.find((c) => c.CharacterId === characterId)?.Rules ?? [];
    const draft = drafts[characterId] ?? [];
    return draft.some((r, i) => r.Type !== saved[i]?.Type || r.Param !== saved[i]?.Param);
  }

  async function save(characterId: string) {
    const rules = drafts[characterId];
    if (!rules || saving) return;
    saving = characterId;
    try {
      const answer = await saveAutomationRules(characterId, rules);
      const result = answer?.Result ?? 'Failed';
      lastResult = { ...lastResult, [characterId]: result };
      if (result === 'Ok') {
        pushLocalNotice('Orders saved. They act while you are away as well.', 'info');
      } else {
        pushLocalNotice(REFUSALS[result] ?? 'The orders were not saved - try again.', 'error');
      }
    } catch {
      lastResult = { ...lastResult, [characterId]: 'Failed' };
      pushLocalNotice('The orders were not saved - try again.', 'error');
    } finally {
      saving = null;
      await client.invalidateQueries({ queryKey: queryKeys.automationRules });
    }
  }

  const shown = $derived(
    v ? (characterId ? v.Characters.filter((c) => c.CharacterId === characterId) : v.Characters) : [],
  );

  // Modul: BELOW THE FIRST UNLOCK, ONE LINE. A new player met twelve dead
  // controls ("Opens at level 20/40/60" three times per person) and a disabled
  // Save - a whole panel of things they could not do. Until the first slot
  // opens there is nothing to set, so there is nothing to show but when.
  const firstUnlock = $derived(v?.UnlockLevels[0] ?? 0);
  const collapsed = $derived(v !== undefined && v.Level < firstUnlock);

  const tiers = $derived(v ? Array.from({ length: v.MaxFuseTier - 1 }, (_, i) => i + 2) : []);
</script>

<section class="panel orders" data-testid="orders-panel">
  <h2>Orders</h2>
  {#if v && collapsed}
    <p class="dim small" data-testid="orders-collapsed">
      Standing orders - what your people do on their own after a death or when
      the larder runs dry - open at level {firstUnlock}. You are level {v.Level}.
    </p>
  {:else}
  <p class="dim small">
    Standing orders your characters follow on their own - watched or away. One
    slot opens at each of level {v ? v.UnlockLevels.join(', ') : '20, 40 and 60'}.
  </p>
  {/if}

  {#if v && collapsed}
    <!-- Nothing to set yet: see the line above. -->
  {:else if view.isPending}
    <p class="dim tiny">Reading your orders&hellip;</p>
  {:else if view.isError || !v}
    <p class="warn">Your orders could not be loaded.</p>
  {:else if shown.length === 0}
    <p class="dim">No character is in a slot yet.</p>
  {:else}
    {#each shown as character (character.CharacterId)}
      {@const rules = drafts[character.CharacterId] ?? character.Rules}
      <div class="who" data-testid="orders-character" data-character-id={character.CharacterId}>
        <h3>Slot {character.Slot + 1}{character.Name ? ` - ${character.Name}` : ''}</h3>
        {#each v.UnlockLevels as unlockLevel, slot (slot)}
          {@const rule = rules[slot] ?? { Type: 0, Param: 0 }}
          {@const open = v.Level >= unlockLevel}
          <div class="slot" class:locked={!open} data-testid="orders-slot" data-slot={slot} data-open={open ? 'true' : 'false'}>
            {#if !open}
              <span class="dim">
                Opens at level {unlockLevel} (you are {v.Level}){#if rule.Type !== 0}
                  - "{RULE_LABELS[rule.Type]}" is kept for then{/if}.
              </span>
            {:else}
              <select
                aria-label={`Order ${slot + 1}`}
                data-testid="orders-type"
                value={rule.Type}
                onchange={(e) => setType(character.CharacterId, slot, Number((e.currentTarget as HTMLSelectElement).value))}
              >
                {#each Object.entries(RULE_LABELS) as [type, label] (type)}
                  <option value={Number(type)} disabled={takenElsewhere(character.CharacterId, slot, Number(type))}>{label}</option>
                {/each}
              </select>
              {#if rule.Type === AUTOMATION_RULE.FishWhenLarderDry}
                <select
                  aria-label="Fishing spot"
                  data-testid="orders-param"
                  value={rule.Param}
                  onchange={(e) => setParam(character.CharacterId, slot, Number((e.currentTarget as HTMLSelectElement).value))}
                >
                  {#each v.FishingSpots as spot (spot.ActivityId)}
                    <option value={spot.ActivityId}>
                      Fish at {locationName(spot.Location)}{spot.Location > reached ? ' (not reached yet)' : ''}
                    </option>
                  {/each}
                </select>
              {:else if rule.Type === AUTOMATION_RULE.AutoFuseToTier}
                <select
                  aria-label="Fuse up to"
                  data-testid="orders-param"
                  value={rule.Param}
                  onchange={(e) => setParam(character.CharacterId, slot, Number((e.currentTarget as HTMLSelectElement).value))}
                >
                  {#each tiers as tier (tier)}
                    <option value={tier}>Up to {rarityName(tier)} (tier {tier})</option>
                  {/each}
                </select>
              {/if}
            {/if}
          </div>
        {/each}
        <div class="actions">
          <button
            class="tiny-btn"
            data-testid="orders-save"
            disabled={saving !== null || !dirty(character.CharacterId)}
            onclick={() => save(character.CharacterId)}
          >
            {saving === character.CharacterId ? 'Saving…' : 'Save orders'}
          </button>
          {#if lastResult[character.CharacterId] && lastResult[character.CharacterId] !== 'Ok'}
            <span class="warn tiny" data-testid="orders-refusal">
              {REFUSALS[lastResult[character.CharacterId]] ?? 'Not saved.'}
            </span>
          {/if}
        </div>
      </div>
    {/each}
    <p class="dim tiny">
      Fishing waits until you have reached the spot and nobody else works it. A
      step down never goes below the first monster. Fusing spends gold from the
      chest at the Forge's price, only on stacks new drops land in.
    </p>
  {/if}
</section>

<style>
  .orders h3 {
    margin: 0.8rem 0 0.35rem;
    font-size: 0.95rem;
  }

  .who {
    border-top: 1px solid var(--border);
    padding-top: 0.2rem;
  }
  .who:first-of-type {
    border-top: 0;
  }

  .slot {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    align-items: center;
    margin: 0.3rem 0;
  }
  .slot select {
    flex: 1 1 12rem;
    min-width: 0;
    max-width: 100%;
  }
  .slot.locked {
    opacity: 0.75;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.6rem;
    margin: 0.4rem 0 0.2rem;
  }
  .actions button {
    flex-shrink: 0;
  }
</style>
