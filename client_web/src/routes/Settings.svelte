<script lang="ts">
  import { onMount } from 'svelte';
  import { language, setLanguage, translations, loadTranslations, coverage, LANGUAGES, t } from '../lib/ui/i18n';
  import { volume, muted, unlockAudio, play, preloadAll, CLIPS, type ClipName } from '../lib/ui/audio';
  import { tutorialStep, skipTutorial, TutorialStep, currentPrompt } from '../lib/stores/tutorial';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { playerState, pushLocalNotice } from '../lib/stores/game';

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

  const clipNames = Object.keys(CLIPS) as ClipName[];
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

    {#if $tutorialStep === TutorialStep.Completed}
      <p class="dim">Finished. It will not start again on this browser.</p>
    {:else if $tutorialStep === TutorialStep.Inactive}
      <p class="dim">Not started. It arms automatically on a brand-new account.</p>
    {:else}
      <p class="active">Step {$tutorialStep} of 3 &middot; {currentPrompt()}</p>
      <button onclick={skip}>Skip the tutorial</button>
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
</style>
