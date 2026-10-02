<script lang="ts">
  // Modul: task 107. Applications are the leader's to review, so only a leader
  // is ever shown this. It used to sit on the Friends tab for everybody, with
  // "None pending, or you are not the leader" - the player had to work out
  // which applied. The caller decides who sees it; an empty list here
  // therefore means "none pending", not "not allowed".
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchGuildApplications,
    approveGuildApplication,
    rejectGuildApplication,
  } from '../net/rest';
  import { pushLocalNotice } from '../stores/game';
  import Skeleton from './Skeleton.svelte';
  import { profileLink } from './profileLink';
  import QueryError from './QueryError.svelte';

  const client = useQueryClient();
  const applications = createQuery(() => ({
    queryKey: queryKeys.guildApplications,
    queryFn: fetchGuildApplications,
  }));
  let busy = $state(false);

  async function reviewApplication(applicationId: number, approve: boolean) {
    busy = true;
    try {
      // Modul: `Success: false` arrives with HTTP 200 and MUST be checked.
      // ApproveApplicationAsync returns it when the guild is full, or when
      // someone else already handled the application - normal outcomes, none
      // of them an HTTP error. Fire-and-forget made a refusal look exactly
      // like an approval.
      const result = approve
        ? await approveGuildApplication(applicationId)
        : await rejectGuildApplication(applicationId);

      if (result?.Success === false) {
        pushLocalNotice(
          approve
            ? 'Not approved - the guild may be full, or it was already handled.'
            : 'Not rejected - it may already have been handled.',
          'error',
        );
      } else {
        pushLocalNotice(approve ? 'Application approved.' : 'Application rejected.', 'info');
      }

      client.invalidateQueries({ queryKey: queryKeys.guildApplications });
      client.invalidateQueries({ queryKey: queryKeys.guildRoster });
    } catch {
      pushLocalNotice('Could not reach the server.', 'error');
    } finally {
      busy = false;
    }
  }
</script>

<section class="panel" data-testid="guild-applications">
  <h2>Applications</h2>
  {#if applications.isPending}
    <Skeleton rows={1} />
  {:else if applications.isError}
    <QueryError query={applications} what="guild applications" />
  {:else if (applications.data ?? []).length === 0}
    <p class="dim small">No applications pending.</p>
  {:else}
    <ul class="rows">
      {#each applications.data ?? [] as application (application.Id)}
        <li>
          <button class="name name-link" use:profileLink={{ playerId: application.PlayerId, name: application.Username }}>{application.Username}</button>
          <span class="dim small">lv {application.ApplicantLevel}</span>
          <button class="tiny-btn" disabled={busy} onclick={() => reviewApplication(application.Id, true)}>
            Approve
          </button>
          <button class="tiny-btn" disabled={busy} onclick={() => reviewApplication(application.Id, false)}>
            Reject
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  h2 {
    margin: 0 0 0.6rem;
    font-size: 1.05rem;
  }
  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0;
  }
  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }
  .rows li {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.5rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }
  .name {
    font-weight: 600;
    margin-right: auto;
    overflow-wrap: anywhere;
  }
  /* The applicant's name opens their profile - who is asking to join. */
  .name-link {
    background: none;
    border: 0;
    padding: 0;
    min-height: 44px;
    /* Modul: a three-letter name is still a thumb's width (check:touch caught "dev" at 28px). */
    min-width: 44px;
    color: inherit;
    font: inherit;
    font-weight: 600;
    text-align: left;
    cursor: pointer;
    text-decoration: underline dotted;
  }
</style>
