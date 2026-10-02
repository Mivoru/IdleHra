// Modul: TASK 106 - WHAT A MODAL OWES THE PAGE BEHIND IT, in one place.
//
// Nine overlays each drew their own backdrop and none of them managed focus:
// Tab walked straight out of a death card into the nav behind it, a screen
// reader read the whole game underneath "Welcome back", and closing a card
// dropped focus on <body>, so a keyboard player started again from the top of
// the page. `Modal.svelte` calls `openModal` on mount; this module is the part
// that has to be shared between instances, because two modals can be open at
// once (the exit prompt over a death card, a guild over a profile).
//
// - The app root (#app) is `inert` while any modal is open. Every modal is
//   portalled to <body>, so it is never inside the root it disables.
// - A lower modal's scrim is `inert` while a higher one is open, so Tab and a
//   screen reader only ever see the top card.
// - Tab cycles inside the top card. It does NOT pull focus out of another
//   body-level overlay (a DetailSheet opened from the profile, a ContextMenu):
//   those are on top of the modal and own their focus.
// - Focus goes back to whatever had it before the modal opened, after the
//   root's `inert` is lifted (an inert element cannot take focus).
//
// Escape and the hardware back button are NOT handled here: App.svelte owns
// both through resolveBackPress, and a second Escape listener is exactly the
// "each overlay decides for itself which layer Escape meant" bug that resolver
// replaced. A Modal registers its closer on stores/sheet.ts instead.

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]),' +
  ' textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"]), [contenteditable]:not([contenteditable="false"])';

interface OpenModal {
  card: HTMLElement;
  scrim: HTMLElement;
}

const stack: OpenModal[] = [];
let keyListener: ((event: KeyboardEvent) => void) | null = null;

function appRoot(): HTMLElement | null {
  return typeof document === 'undefined' ? null : document.getElementById('app');
}

/** The focusable elements inside `root`, in tab order (DOM order; nobody here sets a positive tabindex). */
export function focusableIn(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (el) => !el.closest('[inert]') && el.getClientRects().length > 0,
  );
}

function onKey(event: KeyboardEvent): void {
  if (event.key !== 'Tab' || event.defaultPrevented) return;
  const top = stack[stack.length - 1];
  if (!top) return;

  const active = document.activeElement as HTMLElement | null;
  const inCard = !!active && top.card.contains(active);
  const stray = !active || active === document.body || !!appRoot()?.contains(active);
  // Focus is in some other overlay above this one - leave it alone.
  if (!inCard && !stray) return;

  const items = focusableIn(top.card);
  if (items.length === 0) {
    event.preventDefault();
    top.card.focus();
    return;
  }
  const first = items[0];
  const last = items[items.length - 1];
  if (!inCard || active === top.card) {
    event.preventDefault();
    (event.shiftKey ? last : first).focus();
  } else if (event.shiftKey && active === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
  }
}

/**
 * Marks `card` (inside `scrim`, which is on <body>) as the top modal. Returns
 * the cleanup to run when it closes. Safe to call during SSR-less tests: it
 * only touches `document` when it exists.
 */
export function openModal(card: HTMLElement, scrim: HTMLElement): () => void {
  const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const below = stack[stack.length - 1];
  const entry: OpenModal = { card, scrim };
  stack.push(entry);

  if (stack.length === 1) appRoot()?.setAttribute('inert', '');
  below?.scrim.setAttribute('inert', '');
  if (!keyListener) {
    keyListener = onKey;
    document.addEventListener('keydown', keyListener);
  }

  // The card itself, not its first button: focusing "Again" or "Leave" would
  // put a destructive default one Enter away from a key the player pressed
  // for something else. Tab reaches the first control from here.
  card.focus({ preventScroll: true });

  return () => {
    const index = stack.indexOf(entry);
    if (index === -1) return;
    const wasTop = index === stack.length - 1;
    stack.splice(index, 1);

    const top = stack[stack.length - 1];
    if (wasTop) top?.scrim.removeAttribute('inert');
    if (stack.length === 0) {
      appRoot()?.removeAttribute('inert');
      if (keyListener) document.removeEventListener('keydown', keyListener);
      keyListener = null;
    }

    if (!wasTop) return;
    // Back to where the player was, if that is still on the page and usable.
    if (previous && previous.isConnected && !previous.closest('[inert]')) {
      previous.focus({ preventScroll: true });
    } else if (top) {
      top.card.focus({ preventScroll: true });
    }
  };
}

/** For tests: how many modals are open. */
export function openModalCount(): number {
  return stack.length;
}
