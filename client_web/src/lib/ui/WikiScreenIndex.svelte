<script lang="ts">
  // Modul: the coverage ledger, rendered.
  //
  // Every screen the game has, and either the wiki tab that documents it or a
  // stated reason it needs no page. tests/wiki.test.ts asserts this list is
  // exactly `src/routes/*.svelte`, so the ledger cannot quietly fall behind the
  // game - which is the only way a coverage claim is worth anything.

  import { SCREEN_COVERAGE, type CoverageStatus } from './wikiData';

  let { onJump }: { onJump: (tab: string) => void } = $props();

  const order: Record<CoverageStatus, number> = {
    documented: 0,
    'no-page-needed': 1,
    removed: 2,
  };

  const rows = SCREEN_COVERAGE.slice().sort(
    (a, b) => order[a.status] - order[b.status] || a.label.localeCompare(b.label),
  );

  const documented = rows.filter((r) => r.status === 'documented').length;
</script>

<p class="dim small">
  {documented} of {rows.length} screens have a page here. The rest are listed
  with the reason they do not need one, because "we documented everything" is
  only a claim if the list of everything is written down beside it.
</p>

<div class="scroll">
  <table>
    <thead>
      <tr>
        <th>Screen</th>
        <th>Where it is written down</th>
        <th>What that covers</th>
      </tr>
    </thead>
    <tbody>
      {#each rows as row (row.screen)}
        <tr>
          <td><strong>{row.label}</strong></td>
          <td>
            {#if row.status === 'documented' && row.tab}
              <button type="button" class="jump" onclick={() => onJump(row.tab!)}>
                Open the page
              </button>
            {:else if row.status === 'removed'}
              <span class="tag removed">Being removed</span>
            {:else}
              <span class="tag none">No page needed</span>
            {/if}
          </td>
          <td class="dim">{row.note}</td>
        </tr>
      {/each}
    </tbody>
  </table>
</div>

<style>
  .scroll {
    overflow-x: auto;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    background: rgba(0, 0, 0, 0.12);
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
    min-width: 30rem;
  }

  th {
    text-align: left;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--border);
    color: var(--text-dim);
    font-weight: 600;
    white-space: nowrap;
  }

  td {
    padding: 0.45rem 0.6rem;
    border-bottom: 1px solid rgba(128, 128, 128, 0.12);
    vertical-align: top;
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  .jump {
    background: transparent;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    color: var(--accent);
    padding: 0.15rem 0.5rem;
    font-size: 0.78rem;
    cursor: pointer;
    white-space: nowrap;
  }

  .tag {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    border: 1px solid var(--border);
    border-radius: 999px;
    padding: 0.05rem 0.45rem;
    color: var(--text-dim);
    white-space: nowrap;
  }

  .tag.removed {
    border-color: var(--danger);
    color: var(--danger);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
</style>
