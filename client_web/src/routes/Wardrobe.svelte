<script lang="ts">
  // Task 54: the Wardrobe - open cosmetic chests, and choose the face and the
  // frame other players see. Nothing here adds power.
  //
  // Everything a player owns is the server's answer (GET /api/v1/cosmetics);
  // the catalogue is the server's too. Unowned cosmetics are shown dimmed, so
  // there is a collection to complete rather than an empty list.
  import { onMount } from 'svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import Avatar from '../lib/ui/Avatar.svelte';
  import {
    COSMETIC_KIND,
    COSMETIC_RESULT_SENTENCES,
    cosmeticKeys,
    equipCosmetic,
    fetchCosmetics,
    loadCosmeticCatalogue,
    openChest,
    rarityClass,
    type CosmeticCatalogue,
    type CosmeticDefinition,
    type CosmeticsView,
    type OpenedCosmetic,
  } from '../lib/net/cosmetics';
  import { playerState } from '../lib/stores/game';
  import { forgetWorn, requestWorn, wornByPlayer } from '../lib/stores/worn';
  import { play } from '../lib/ui/audio';
  import { noteCosmeticsView } from '../lib/stores/cosmeticChests';
  import QueryError from '../lib/ui/QueryError.svelte';

  const client = useQueryClient();
  const mine = createQuery(() => ({ queryKey: cosmeticKeys.mine, queryFn: fetchCosmetics }));

  let catalogue = $state<CosmeticCatalogue | null>(null);
  let loadError = $state('');
  onMount(() => {
    loadCosmeticCatalogue()
      .then((c) => (catalogue = c))
      .catch(() => (loadError = 'The wardrobe could not be loaded. Try again in a moment.'));
  });

  const view = $derived(mine.data ?? null);
  $effect(() => noteCosmeticsView(mine.data));
  const selfId = $derived(Number($playerState?.PlayerId ?? 0));
  $effect(() => {
    if (selfId > 0) requestWorn(selfId);
  });
  // Race and sex for the default portrait come from the same lookup other
  // players see, so the preview here is exactly what they get.
  const selfWorn = $derived(selfId > 0 ? ($wornByPlayer.get(selfId)?.worn ?? null) : null);

  let busy = $state(false);
  let message = $state('');
  let opened = $state<OpenedCosmetic | null>(null);

  const ownedIds = $derived(new Set((view?.Owned ?? []).filter((o) => !o.IsListed && o.Kind !== COSMETIC_KIND.Chest).map((o) => o.DefinitionId)));
  const copies = $derived.by(() => {
    const counts = new Map<string, number>();
    for (const o of view?.Owned ?? []) {
      if (o.Kind === COSMETIC_KIND.Chest) continue;
      counts.set(o.DefinitionId, (counts.get(o.DefinitionId) ?? 0) + 1);
    }
    return counts;
  });

  function byRarityThenName(a: CosmeticDefinition, b: CosmeticDefinition): number {
    return a.Rarity - b.Rarity || a.Name.localeCompare(b.Name);
  }
  const avatars = $derived((catalogue?.Items ?? []).filter((d) => d.Kind === COSMETIC_KIND.Avatar).sort(byRarityThenName));
  const frames = $derived((catalogue?.Items ?? []).filter((d) => d.Kind === COSMETIC_KIND.Frame).sort(byRarityThenName));
  const rarityNames = $derived(catalogue?.RarityNames ?? ['', 'Common', 'Rare', 'Epic', 'Legendary']);
  const totalChests = $derived((view?.Chests ?? []).reduce((a, b) => a + b, 0));
  const collected = $derived(ownedIds.size);
  const collectable = $derived(avatars.length + frames.length);

  function settle(next: CosmeticsView | null): void {
    if (!next) {
      message = 'The server did not answer. Try again.';
      return;
    }
    client.setQueryData(cosmeticKeys.mine, next);
    message = next.Result && next.Result !== 'Ok' ? COSMETIC_RESULT_SENTENCES[next.Result] : '';
  }

  async function open(rarity: number): Promise<void> {
    if (busy) return;
    busy = true;
    try {
      const next = await openChest(rarity);
      settle(next);
      if (next?.Opened) {
        opened = next.Opened;
        play('lootRare');
      }
    } finally {
      busy = false;
    }
  }

  // Modul: task 109 - a locked tile was a disabled button with the source in a
  // hover title, which a phone never shows. It is tappable now and says where
  // the piece comes from: a chest of its rarity, or - for a Bound piece, which
  // is never in a chest (cosmetics.ts) - the Boss Ascension ladder.
  function sourceOf(def: CosmeticDefinition): string {
    return def.Bound
      ? `${def.Name} is earned on the Boss Ascension ladder, never found in a chest.`
      : `${def.Name} comes from a ${rarityNames[def.Rarity]} chest, or from another player on the market.`;
  }

  // "×4" said nothing; spare copies are what the cosmetic market sells.
  function copiesNote(id: string): string {
    const n = copies.get(id) ?? 0;
    return n > 1 ? ` - ${n} copies` : '';
  }

  // Owned-only is the view of a player dressing; All is the view of a
  // collector. Defaults to All, which is what the screen always showed.
  let ownedOnly = $state(false);
  const shownAvatars = $derived(ownedOnly ? avatars.filter((d) => ownedIds.has(d.Id)) : avatars);
  const shownFrames = $derived(ownedOnly ? frames.filter((d) => ownedIds.has(d.Id)) : frames);

  async function wear(kind: number, id: string | null): Promise<void> {
    if (busy) return;
    busy = true;
    try {
      settle(await equipCosmetic(kind, id));
      if (selfId > 0) forgetWorn(selfId);
    } finally {
      busy = false;
    }
  }
</script>

<section class="wardrobe">
  <header class="panel head">
    <Avatar
      avatarId={view?.EquippedAvatarId ?? null}
      frameId={view?.EquippedFrameId ?? null}
      raceId={selfWorn?.RaceId ?? 0}
      female={selfWorn?.IsFemale ?? false}
      size="lg"
    />
    <div>
      <h1>Wardrobe</h1>
      <p class="dim">
        Your face and frame, as other players see them in chat, on the boards and in your profile.
        Nothing here makes you stronger.
      </p>
      {#if catalogue}
        <p class="dim small">
          Collected {collected} of {collectable}.
          <label class="owned-toggle">
            <input type="checkbox" bind:checked={ownedOnly} data-testid="wardrobe-owned-only" />
            Owned only
          </label>
        </p>
      {/if}
    </div>
  </header>

  {#if loadError}
    <p class="error">{loadError}</p>
  {/if}
  {#if mine.isError && mine.data === undefined}
    <!-- Modul: without this the chests read 0 and nothing reads as owned -
         a wardrobe that looks emptied rather than one that did not load. -->
    <QueryError query={mine} what="your cosmetics" />
  {/if}
  {#if message}
    <p class="notice" role="status">{message}</p>
  {/if}

  <section class="panel chests">
    <h2>Chests {#if totalChests > 0}<span class="count">{totalChests}</span>{/if}</h2>
    <p class="dim small">
      One every {catalogue?.LevelsPerChest ?? 5} levels, and a rare drop from any monster. A chest
      gives an avatar or a frame of its own rarity.
    </p>
    <!-- Four cards reading "0" were the first thing on the screen for most
         players. With nothing to open, the sentence above is all there is. -->
    {#if view && totalChests === 0}
      <p class="dim small" data-testid="no-chests">You have no chests to open yet.</p>
    {:else}
    <div class="chest-row">
      {#each [1, 2, 3, 4] as rarity}
        {@const count = view?.Chests[rarity] ?? 0}
        <div class="chest {rarityClass(rarity)}" class:has={count > 0}>
          <span class="chest-name">{rarityNames[rarity]}</span>
          <span class="chest-count">{count}</span>
          <button disabled={busy || count === 0} onclick={() => open(rarity)} data-testid="open-chest-{rarity}">Open</button>
        </div>
      {/each}
    </div>
    {/if}

    {#if opened}
      <div class="reveal {rarityClass(opened.Rarity)}" data-testid="chest-reveal">
        {#if opened.Kind === COSMETIC_KIND.Avatar}
          <Avatar avatarId={opened.DefinitionId} frameId={view?.EquippedFrameId ?? null} size="md" />
        {:else}
          <Avatar avatarId={view?.EquippedAvatarId ?? null} frameId={opened.DefinitionId} raceId={selfWorn?.RaceId ?? 0} female={selfWorn?.IsFemale ?? false} size="md" />
        {/if}
        <div>
          <strong>{opened.Name}</strong>
          <span class="dim small">{rarityNames[opened.Rarity]} {opened.Kind === COSMETIC_KIND.Avatar ? 'avatar' : 'frame'}</span>
        </div>
        <button
          disabled={busy}
          onclick={() => opened && wear(opened.Kind, opened.DefinitionId)}
        >Wear it</button>
      </div>
    {/if}
  </section>

  <section class="panel">
    <h2>Avatars</h2>
    <div class="collection">
      <button
        class="tile"
        class:worn={!view?.EquippedAvatarId}
        disabled={busy}
        onclick={() => wear(COSMETIC_KIND.Avatar, null)}
        data-testid="avatar-default"
      >
        <Avatar avatarId={null} frameId={null} raceId={selfWorn?.RaceId ?? 0} female={selfWorn?.IsFemale ?? false} size="tile" />
        <span class="tile-name">Your race</span>
        <span class="tile-rarity dim">Default</span>
      </button>
      {#each shownAvatars as def (def.Id)}
        {@const owned = ownedIds.has(def.Id)}
        <button
          class="tile {rarityClass(def.Rarity)}"
          class:locked={!owned}
          class:worn={view?.EquippedAvatarId === def.Id}
          aria-disabled={!owned}
          disabled={busy}
          onclick={() => (owned ? wear(COSMETIC_KIND.Avatar, def.Id) : (message = sourceOf(def)))}
          data-testid="avatar-{def.Id}"
          title={owned ? `Wear ${def.Name}` : `${def.Name} - from a ${rarityNames[def.Rarity]} chest`}
        >
          <Avatar avatarId={def.Id} frameId={null} size="tile" />
          <span class="tile-name">{def.Name}</span>
          <span class="tile-rarity">{rarityNames[def.Rarity]}{copiesNote(def.Id)}</span>
        </button>
      {/each}
    </div>
  </section>

  <section class="panel">
    <h2>Frames</h2>
    <div class="collection">
      <button
        class="tile"
        class:worn={!view?.EquippedFrameId}
        disabled={busy}
        onclick={() => wear(COSMETIC_KIND.Frame, null)}
        data-testid="frame-default"
      >
        <Avatar avatarId={view?.EquippedAvatarId ?? null} frameId={null} raceId={selfWorn?.RaceId ?? 0} female={selfWorn?.IsFemale ?? false} size="tile" />
        <span class="tile-name">No frame</span>
        <span class="tile-rarity dim">Default</span>
      </button>
      {#each shownFrames as def (def.Id)}
        {@const owned = ownedIds.has(def.Id)}
        <button
          class="tile {rarityClass(def.Rarity)}"
          class:locked={!owned}
          class:worn={view?.EquippedFrameId === def.Id}
          aria-disabled={!owned}
          disabled={busy}
          onclick={() => (owned ? wear(COSMETIC_KIND.Frame, def.Id) : (message = sourceOf(def)))}
          data-testid="frame-{def.Id}"
          title={owned ? `Wear ${def.Name}` : `${def.Name} - from a ${rarityNames[def.Rarity]} chest`}
        >
          <Avatar avatarId={view?.EquippedAvatarId ?? null} frameId={def.Id} raceId={selfWorn?.RaceId ?? 0} female={selfWorn?.IsFemale ?? false} size="tile" />
          <span class="tile-name">{def.Name}</span>
          <span class="tile-rarity">{rarityNames[def.Rarity]}{copiesNote(def.Id)}</span>
        </button>
      {/each}
    </div>
  </section>
</section>

<style>
  .wardrobe {
    display: grid;
    gap: 1rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .head {
    display: flex;
    gap: 1rem;
    align-items: center;
  }

  .head h1 {
    margin: 0 0 0.25rem;
  }

  .head p {
    margin: 0 0 0.25rem;
  }

  .small {
    font-size: 0.85rem;
  }

  .notice {
    margin: 0;
    color: var(--warn);
  }

  .panel h2 {
    margin: 0 0 0.4rem;
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .count {
    font-size: 0.8rem;
    padding: 0 0.45rem;
    border-radius: 999px;
    background: var(--accent);
    color: var(--on-accent);
  }

  /* Rarity colours, the chest's own four - not the 14-tier item palette. */
  .common {
    --c: #a9a9a9;
  }
  .rare {
    --c: #4f9be0;
  }
  .epic {
    --c: #a66be0;
  }
  .legendary {
    --c: #e0a526;
  }

  .chest-row {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(7rem, 1fr));
    gap: 0.5rem;
    margin-top: 0.5rem;
  }

  .chest {
    display: grid;
    gap: 0.3rem;
    justify-items: center;
    padding: 0.6rem;
    border: 1px solid color-mix(in srgb, var(--c) 45%, transparent);
    border-radius: 8px;
    opacity: 0.6;
  }

  .chest.has {
    opacity: 1;
    background: color-mix(in srgb, var(--c) 10%, transparent);
  }

  .chest-name {
    color: var(--c);
    font-weight: 600;
  }

  .chest-count {
    font-size: 1.3rem;
    font-variant-numeric: tabular-nums;
  }

  .reveal {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-top: 0.75rem;
    padding: 0.6rem 0.75rem;
    border-left: 3px solid var(--c);
    background: color-mix(in srgb, var(--c) 12%, transparent);
  }

  .reveal div {
    display: grid;
    flex: 1;
    min-width: 0;
  }

  .reveal button {
    flex-shrink: 0;
  }

  /* Not `.grid`: app.css turns every .grid into one column on a phone, and a
     collection of 41 tiles in one column is a very long scroll. */
  .collection {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(6.5rem, 1fr));
    gap: 0.5rem;
  }

  .tile {
    display: grid;
    justify-items: center;
    gap: 0.2rem;
    padding: 0.5rem 0.25rem;
    min-height: 44px;
    border: 1px solid color-mix(in srgb, var(--c, #666) 40%, transparent);
    border-radius: 8px;
    background: transparent;
    text-align: center;
  }

  .tile.worn {
    border-color: var(--c, var(--accent));
    box-shadow: 0 0 0 2px color-mix(in srgb, var(--c, var(--accent)) 60%, transparent);
  }

  /* Dimmed but still coloured, so an unowned piece shows what it would be.
     The picture dims, not the whole tile: a faded rarity label was unreadable. */
  .tile.locked > :global(:not(.tile-rarity)) {
    opacity: 0.5;
    filter: grayscale(0.45);
  }

  .owned-toggle {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    margin-left: 0.6rem;
    cursor: pointer;
  }

  .tile-name {
    font-size: 0.8rem;
    line-height: 1.2;
    overflow-wrap: anywhere;
  }

  .tile-rarity {
    font-size: 0.7rem;
    color: var(--c, var(--text-dim));
  }
</style>
