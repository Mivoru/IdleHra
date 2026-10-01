// Modul: the state machine behind ConfirmButton, apart from the component so
// it can be tested without a DOM. First press ARMS (nothing happens yet),
// the second press inside the window COMMITS, and anything else - the timer,
// Escape, focus leaving - disarms. Two-step rather than a native confirm():
// the WebView draws confirm() as an unstyled system dialog, and two confirm
// styles side by side read as two different apps.

export interface ConfirmArm {
  readonly armed: boolean;
  /** Returns 'armed' on the first press and 'committed' on the second. */
  press(): 'armed' | 'committed';
  disarm(): void;
  dispose(): void;
}

export function createConfirmArm(options: {
  timeoutMs?: number;
  onCommit: () => void;
  onChange?: (armed: boolean) => void;
  timers?: { set: (fn: () => void, ms: number) => unknown; clear: (handle: unknown) => void };
}): ConfirmArm {
  const timers = options.timers ?? {
    set: (fn: () => void, ms: number) => setTimeout(fn, ms),
    clear: (handle: unknown) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  };
  let armed = false;
  let handle: unknown = null;

  function setArmed(next: boolean) {
    if (handle !== null) {
      timers.clear(handle);
      handle = null;
    }
    // Read on every arm, so a caller can pass a getter that follows a prop.
    if (next) handle = timers.set(() => setArmed(false), options.timeoutMs ?? 4000);
    if (armed !== next) {
      armed = next;
      options.onChange?.(next);
    }
  }

  return {
    get armed() {
      return armed;
    },
    press() {
      if (!armed) {
        setArmed(true);
        return 'armed';
      }
      // Disarm BEFORE committing, so a commit that throws or re-renders the
      // row cannot leave a live "Really?" behind for the next tap.
      setArmed(false);
      options.onCommit();
      return 'committed';
    },
    disarm: () => setArmed(false),
    dispose() {
      if (handle !== null) timers.clear(handle);
      handle = null;
    },
  };
}
