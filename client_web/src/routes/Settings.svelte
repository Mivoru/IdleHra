<script lang="ts">
  import { onMount } from 'svelte';
  import { language, setLanguage, translations, loadTranslations, coverage, LANGUAGES, t } from '../lib/ui/i18n';
  import { volume, muted, unlockAudio, play, preloadAll, CLIPS, type ClipName } from '../lib/ui/audio';
  import { tutorialPrompt, skipTutorial, unskipTutorial } from '../lib/stores/tutorial';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { playerState, pushLocalNotice, commandResults, connectionStatus } from '../lib/stores/game';
  import { triggerGdprPurge } from '../lib/net/commands';
  import { submitSupportTicket, scrubTrace, fetchAdminStatus, adminToggleProfanity, adminAnnounce, adminBan, adminUnban, adminSendMail } from '../lib/net/rest';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';

  const snap = $derived($playerState);

  onMount(() => {
    void loadTranslations();
  });

  function pickLanguage(code: (typeof LANGUAGES)[number]['code'], wireId: number) {
    setLanguage(code);

    // Modul: TargetLanguageId is 1-4 on the wire while LocalizationMatrix
    // indexes 0-3. Sending the index would be rejected outright for English
    // (0 is invalid) and would select the wrong language for the rest, so the
    // wire id is carried explicitly on each entry rather than derived.
    //
    // ValidateLanguageSwitchRequest also demands that ~25 other fields are
    // zero, so nothing else may ride along with this.
    connection.send({ Command: CommandType.SwitchLanguage, TargetLanguageId: wireId });
  }

  function testSound(name: ClipName) {
    unlockAudio();
    play(name);
  }

  async function enableAudio() {
    unlockAudio();
    await preloadAll();
    play('windowOpen');
  }

  function skip() {
    skipTutorial();
    pushLocalNotice('Tutorial skipped.', 'info');
  }

  function showAgain() {
    unskipTutorial();
  }

  const clipNames = Object.keys(CLIPS) as ClipName[];

  // ---------------------------------------------------------------------------
  // Admin / Dev Settings
  // ---------------------------------------------------------------------------

  const queryClient = useQueryClient();
  const adminQuery = createQuery(() => ({
    queryKey: ['adminStatus'],
    queryFn: fetchAdminStatus,
    retry: false // don't retry 403s
  }));

  const isAdmin = $derived(adminQuery.data?.isAdmin === true);
  let devProfanity = $state(true);
  $effect(() => {
    if (adminQuery.data !== undefined) {
      devProfanity = adminQuery.data.profanityEnabled;
    }
  });

  let devAnnounceMsg = $state('');
  let devBanUsername = $state('');
  let devMailUsername = $state('');
  let devMailItem = $state('');
  let devMailQty = $state(1);
  let devMailGold = $state(0);
  let devMailMsg = $state('');
  
  async function toggleProfanity() {
    devProfanity = !devProfanity;
    await adminToggleProfanity(devProfanity);
    pushLocalNotice('Profanity filter ' + (devProfanity ? 'enabled' : 'disabled'));
  }

  async function doAnnounce() {
    if (!devAnnounceMsg) return;
    await adminAnnounce(devAnnounceMsg);
    devAnnounceMsg = '';
    pushLocalNotice('Announcement sent!');
  }

  async function doBan() {
    if (!devBanUsername) return;
    await adminBan(devBanUsername);
    devBanUsername = '';
    pushLocalNotice('Player banned.');
  }

  async function doUnban() {
    if (!devBanUsername) return;
    await adminUnban(devBanUsername);
    devBanUsername = '';
    pushLocalNotice('Player unbanned.');
  }

  async function doMail() {
    await adminSendMail({
      TargetUsername: devMailUsername || null,
      BaseItemId: devMailItem || null,
      QualityTier: 0,
      Quantity: devMailQty,
      Gold: devMailGold,
      SenderName: 'Dev',
      MessageText: devMailMsg || null
    });
    devMailUsername = '';
    devMailItem = '';
    devMailQty = 1;
    devMailGold = 0;
    devMailMsg = '';
    pushLocalNotice('Admin mail sent!');
  }

  // ---------------------------------------------------------------------------
  // Support
  // ---------------------------------------------------------------------------

  let supportMessage = $state('');
  let supportSent = $state(false);

  /**
   * The diagnostic bundle. Deliberately assembled from things this client
   * already knows rather than scraping the page: connection phase, the last
   * few command result codes, and the player's own id. Nothing else in this
   * app has a legitimate reason to appear in a support log.
   */
  function buildTrace(): string {
    const lines = [
      `player=${snap?.PlayerId ?? 'unknown'}`,
      `phase=${$connectionStatus.phase} attempt=${$connectionStatus.attempt}`,
      `epoch=${connection.currentEpoch}`,
      `agent=${navigator.userAgent}`,
      `language=${$language}`,
      '--- recent command results ---',
      ...$commandResults.slice(-8).map((entry) => `code=${entry.code} tick=${entry.tick}`),
      '--- message ---',
      supportMessage,
    ];
    return scrubTrace(lines.join('\n'));
  }

  const tracePreview = $derived(buildTrace());

  async function sendSupport() {
    try {
      await submitSupportTicket(buildTrace());
      supportSent = true;
      // Modul: the handler writes ONE LINE TO THE SERVER CONSOLE and returns
      // 200. It does not store a ticket, assign an id or produce a body. So
      // this must not promise a reply - a 200 means "received", nothing more,
      // and anything warmer is a lie the player discovers by waiting.
      pushLocalNotice('Sent. There is no ticketing system behind this yet.', 'info');
    } catch {
      pushLocalNotice('Could not reach the server.');
    }
  }

  // ---------------------------------------------------------------------------
  // Account erasure
  // ---------------------------------------------------------------------------

  let purgeConfirmation = $state('');
  const PURGE_PHRASE = 'DELETE MY ACCOUNT';
  const purgeArmed = $derived(purgeConfirmation.trim() === PURGE_PHRASE);

  function purge() {
    if (!purgeArmed || !snap) return;

    const outcome = triggerGdprPurge(snap.PlayerId, connection.currentEpoch);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    // Modul: BOTH OUTCOMES LOOK IDENTICAL FROM HERE.
    //
    // A successful purge ends with TerminateSessionForSecurity. So does a
    // REJECTED one - the interlock hash is checked for exact equality against
    // the server's current epoch, and a checkpoint flush landing between the
    // last state update and this command makes it stale. There is no result
    // code either way and no way to produce one from the client side.
    //
    // Saying so is the only honest thing available.
    pushLocalNotice(
      'Request sent. You will be disconnected either way - sign in again to see whether the account is gone.',
      'info',
    );
  }
</script>

<div class="grid">
  <section class="panel">
    <h2>Language</h2>
    <p class="dim small">
      The same 28-key table the Unity client reads, served from /gamedata - one
      table, not two. A blank translation falls back to English rather than
      showing nothing.
    </p>

    <div class="langs">
      {#each LANGUAGES as lang}
        <button
          class:active={$language === lang.code}
          onclick={() => pickLanguage(lang.code, lang.wireId)}
        >
          {lang.name}
          {#if $translations.size > 0}
            <span class="dim tiny">{coverage(lang.code)}/{$translations.size}</span>
          {/if}
        </button>
      {/each}
    </div>

    {#if $translations.size > 0}
      <h3>Sample</h3>
      <ul class="samples">
        <li><span class="dim tiny">EventNone</span> {$t('EventNone')}</li>
        <li><span class="dim tiny">ActiveEventPrefix</span> {$t('ActiveEventPrefix')}</li>
      </ul>
      <p class="dim tiny">
        Only 28 keys exist, so most of this client's text is not translated at
        all - the table covers event names and a handful of labels. Stated
        rather than implied by a language picker that suggests full coverage.
      </p>
    {/if}
  </section>

  <section class="panel">
    <h2>Sound</h2>
    <p class="dim small">
      The same ten effects the Unity client plays - the server serves them from
      its Resources folder rather than keeping a second copy.
    </p>

    <label class="check">
      <input type="checkbox" bind:checked={$muted} />
      Mute
    </label>

    <label>
      Volume
      <input type="range" min="0" max="1" step="0.05" bind:value={$volume} disabled={$muted} />
    </label>

    <!-- Browsers refuse to start an AudioContext before a user gesture, so
         audio is armed by a button rather than at load - starting early gives
         a suspended context that silently plays nothing. -->
    <button onclick={enableAudio}>Enable and preload sound</button>

    <h3>Test a cue</h3>
    <div class="clips">
      {#each clipNames as name}
        <button class="tiny-btn" onclick={() => testSound(name)}>{name}</button>
      {/each}
    </div>
  </section>

  <section class="panel">
    <h2>Tutorial</h2>

    <!-- Modul: the three states became two, because the third was a fiction.
         "Not started - it arms automatically on a brand-new account" described
         a machine that armed off IsFreshAccount and then never advanced. The
         steps are read from the player's own state now, so there is nothing to
         start: either the three things are done or they are not. -->
    {#if $tutorialPrompt}
      <p class="active">
        {$tutorialPrompt.index} / {$tutorialPrompt.total} &middot; {$tutorialPrompt.title}
      </p>
      <p class="dim small">{$tutorialPrompt.body}</p>
      <button onclick={skip}>Hide the tutorial</button>
    {:else}
      <p class="dim">
        Nothing outstanding - you have fought, dressed and stocked the larder.
      </p>
      <button onclick={showAgain}>Show it again</button>
    {/if}

    <h3>Accessibility</h3>
    <p class="dim small">
      Animations respect your system's reduced-motion setting: floating damage
      numbers and the rarity glow both stop moving when it is on. Bars carry
      their real numbers as text rather than colour alone.
    </p>

    {#if snap}
      <h3>Session</h3>
      <dl class="stats">
        <div><dt>Player</dt><dd>#{snap.PlayerId}</dd></div>
        <div>
          <dt>Last save</dt>
          <!-- TicksSinceLastFlush / 10 is exactly the whole-second age of the
               last successful save - the save-trust indicator's whole point. -->
          <dd>{(snap.TicksSinceLastFlush / 10).toFixed(0)}s ago</dd>
        </div>
      </dl>
    {/if}
  </section>

  {#if isAdmin}
      <section class="panel admin-panel">
        <header class="head">
          <h2 style="color: var(--err)">Dev Settings (Admin Only)</h2>
        </header>

        <p class="dim small">
          These settings are visible only to you.
        </p>

        <div class="admin-grid">
          <div class="admin-card">
            <h3>Global Profanity Filter</h3>
            <button onclick={toggleProfanity} class:active={devProfanity}>
              {devProfanity ? 'Enabled (ON)' : 'Disabled (OFF)'}
            </button>
          </div>

          <div class="admin-card">
            <h3>Announcement</h3>
            <div class="flex-row">
              <input type="text" bind:value={devAnnounceMsg} placeholder="Message to all players..." />
              <button onclick={doAnnounce}>Broadcast</button>
            </div>
          </div>

          <div class="admin-card">
            <h3>Ban / Unban Player</h3>
            <div class="flex-row">
              <input type="text" bind:value={devBanUsername} placeholder="Player Username..." />
              <button onclick={doBan} style="color: var(--err)">Ban</button>
              <button onclick={doUnban}>Unban</button>
            </div>
          </div>

          <div class="admin-card" style="grid-column: 1 / -1;">
            <h3>Admin Mailer</h3>
            <p class="dim tiny">Leave Target Username empty to send to ALL players.</p>
            <div class="form-grid">
              <input type="text" bind:value={devMailUsername} placeholder="Target Username (empty = ALL)" />
              <input type="text" bind:value={devMailItem} placeholder="Base Item ID (e.g. axe_copper)" />
              <input type="number" bind:value={devMailQty} placeholder="Quantity" />
              <input type="number" bind:value={devMailGold} placeholder="Gold Amount" />
              <input type="text" bind:value={devMailMsg} placeholder="Text Message (optional)" style="grid-column: 1 / -1;" />
              <button onclick={doMail} style="grid-column: 1 / -1;">Send Mail</button>
            </div>
          </div>
        </div>
      </section>
    {/if}

    <section class="panel">
      <header class="head">
        <h2>Support</h2>
    <p class="dim small">
      Sends a short diagnostic bundle with your message. Bearer tokens, email
      addresses and long opaque ids are stripped in your browser before
      anything is sent - a reduction of risk rather than a guarantee, so do not
      paste anything private into the box.
    </p>

    <label>
      What went wrong
      <textarea rows="4" bind:value={supportMessage}></textarea>
    </label>

    <details>
      <summary>See exactly what will be sent</summary>
      <pre>{tracePreview}</pre>
    </details>

    <button disabled={supportMessage.trim().length === 0} onclick={sendSupport}>
      Send
    </button>

    {#if supportSent}
      <p class="dim tiny">
        Received by the server. There is no ticketing system behind this
        endpoint yet, so nobody will reply to it - stated here rather than
        implied by a reference number that does not exist.
      </p>
    {/if}
  </section>

  <section class="panel danger-panel">
    <h2>Delete this account</h2>

    <p class="warn">
      <strong>This cannot be undone.</strong> Every character, item, guild
      membership and purchase is erased permanently.
    </p>

    <p class="dim small">
      Type <code>{PURGE_PHRASE}</code> below to enable the button. The request
      also carries a one-time interlock computed from your player id and the
      server's current save generation, so it cannot be replayed from a
      captured request.
    </p>

    <label>
      Confirmation
      <input type="text" bind:value={purgeConfirmation} placeholder={PURGE_PHRASE} />
    </label>

    <button class="destructive" disabled={!purgeArmed || !snap} onclick={purge}>
      Permanently delete
    </button>

    <p class="dim tiny">
      You will be disconnected whether or not it succeeds - the server ends the
      session either way and sends no result code. Signing in again is the only
      way to find out which happened.
    </p>
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(19rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
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
  }

  .active {
    color: var(--good);
    font-size: 0.88rem;
    margin: 0 0 0.6rem;
  }

  .langs {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.35rem;
  }

  .langs button {
    display: grid;
    gap: 0.1rem;
    font-size: 0.85rem;
  }

  .langs button.active {
    border-color: var(--accent);
    color: var(--accent);
  }

  .samples {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
    font-size: 0.85rem;
  }

  .samples li {
    display: grid;
    gap: 0.05rem;
  }

  label {
    display: grid;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    margin-bottom: 0.6rem;
  }

  label.check {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  label.check input {
    width: auto;
  }

  input[type='range'] {
    width: 100%;
  }

  .clips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  .tiny-btn {
    font-size: 0.7rem;
    padding: 0.2rem 0.45rem;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    margin: 0;
  }

  /* The one panel on this screen that can destroy something. Bordered in the
     danger colour AND separated by its own heading and a typed confirmation -
     colour is the last of the three signals, not the only one. */
  .danger-panel {
    border-color: var(--danger);
  }

  .warn {
    font-size: 0.85rem;
    color: var(--danger);
    margin: 0 0 0.6rem;
  }

  .destructive:not(:disabled) {
    border-color: var(--danger);
    color: var(--danger);
  }

  textarea,
  input[type='text'] {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
    resize: vertical;
  }

  code {
    background: var(--bg-raised);
    padding: 0.05rem 0.3rem;
    border-radius: 4px;
    font-size: 0.85em;
  }

  details {
    margin: 0 0 0.7rem;
    font-size: 0.78rem;
    color: var(--text-dim);
  }

  summary {
    cursor: pointer;
  }

  pre {
    margin: 0.4rem 0 0;
    padding: 0.5rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
    font-size: 0.68rem;
    max-height: 12rem;
    overflow: auto;
    white-space: pre-wrap;
    word-break: break-all;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }
  .admin-panel {
    border: 1px solid var(--err);
    background: rgba(255, 0, 0, 0.05);
  }

  .admin-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
    gap: 1rem;
    margin-top: 1rem;
  }

  .admin-card {
    background: var(--bg-hover);
    padding: 1rem;
    border-radius: 4px;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .admin-card h3 {
    margin: 0;
    font-size: 0.9rem;
    color: var(--fg);
  }

  .flex-row {
    display: flex;
    gap: 0.5rem;
  }

  .form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.5rem;
  }

  .flex-row input, .form-grid input {
    min-width: 0;
  }

  @media (max-width: 600px) {
    .flex-row {
      flex-direction: column;
    }
    .form-grid {
      grid-template-columns: 1fr;
    }
  }
</style>
