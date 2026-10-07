<script lang="ts">
  // Modul: the admin mailer used to ask for a base id typed from memory, and a
  // typo sent a mail the server could only reject. This lists the catalogue the
  // client already loaded. `value` stays the raw BaseId string - exactly what
  // the mailer sent before - so typing an id the list does not know still works
  // (the server stays the judge) and nothing downstream changes.
  import { contentRegistry } from '../net/registry.svelte';
  import { prettifyBaseId } from '../net/content';

  let { value = $bindable(''), placeholder = 'Search items...' }: { value?: string; placeholder?: string } = $props();

  let open = $state(false);
  const MAX_SHOWN = 40;

  const all = $derived(
    [...(contentRegistry.current?.items.values() ?? [])]
      .map((item) => ({
        baseId: item.BaseId,
        name: prettifyBaseId(item.BaseId),
        tier: item.RegionTier,
        gold: item.BaseValueGold,
      }))
      .sort((a, b) => a.name.localeCompare(b.name)),
  );

  const matches = $derived.by(() => {
    const q = value.trim().toLowerCase();
    const hits = q === '' ? all : all.filter((i) => i.baseId.toLowerCase().includes(q) || i.name.toLowerCase().includes(q));
    return hits.slice(0, MAX_SHOWN);
  });

  function pick(baseId: string) {
    value = baseId;
    open = false;
  }
</script>

<div class="picker">
  <input
    type="text"
    bind:value
    {placeholder}
    autocomplete="off"
    onfocus={() => (open = true)}
    oninput={() => (open = true)}
    onblur={() => setTimeout(() => (open = false), 150)}
    onkeydown={(e) => e.key === 'Escape' && (open = false)}
  />
  {#if open && matches.length > 0}
    <ul class="list" role="listbox">
      {#each matches as item (item.baseId)}
        <li role="option" aria-selected={item.baseId === value}>
          <!-- mousedown, not click: the input's blur would close the list
               before a click ever landed. -->
          <button type="button" onmousedown={(e) => { e.preventDefault(); pick(item.baseId); }} onclick={() => pick(item.baseId)}>
            <span class="name">{item.name}</span>
            <span class="meta dim">{item.baseId} - tier {item.tier} - {item.gold}g</span>
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .picker {
    position: relative;
    min-width: 0;
  }

  .picker input {
    width: 100%;
    box-sizing: border-box;
  }

  .list {
    position: absolute;
    z-index: 20;
    left: 0;
    right: 0;
    top: 100%;
    margin: 2px 0 0;
    padding: 0.25rem;
    list-style: none;
    max-height: 16rem;
    overflow-y: auto;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: 0 8px 20px rgba(0, 0, 0, 0.4);
  }

  .list button {
    width: 100%;
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    gap: 0.1rem 0.6rem;
    text-align: left;
    background: none;
    border: none;
    box-shadow: none;
  }

  .meta {
    font-size: var(--fs-xs);
    overflow-wrap: anywhere;
  }
</style>
