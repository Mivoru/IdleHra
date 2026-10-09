import { writable } from 'svelte/store';

/**
 * Whether the "Leave FolkIdle?" confirmation is open.
 *
 * Modul: A STORE BECAUSE TWO COMPONENTS ANSWER THE BACK BUTTON. App.svelte
 * renders the prompt and answers back on the login form; Game.svelte, loaded
 * after sign-in, answers it everywhere else. Both have to read and close the
 * same prompt, and the prompt has to outlive either of them - so it is not
 * component state in one of them.
 */
export const exitPromptOpen = writable(false);
