import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

/*
  COMING BACK FROM THE BACKGROUND.

  The reconnect loop is written for a server that went away. A phone poses a
  different problem: the OS froze the WebView, the socket died in a pocket, and
  the player is now looking at the screen. Backoff is exactly the wrong
  behaviour for that, and a zombie socket that still reports OPEN is worse -
  nothing on screen ever admits it.

  These test the listener wiring rather than the socket surgery: that a resume
  reaches the connection, that three overlapping sources of the same event
  produce one call, and that the native path is optional. The socket handling
  itself is exercised by hand on a device, which is the only place it is real.
*/

const resumeFromBackground = vi.fn();
const suspendForBackground = vi.fn();

vi.mock('../src/lib/net/connection', () => ({
  connection: {
    get resumeFromBackground() {
      return resumeFromBackground;
    },
    get suspendForBackground() {
      return suspendForBackground;
    },
  },
}));

const { watchAppLifecycle } = await import('../src/lib/net/lifecycle');

let stopWatching: (() => void) | null = null;

/*
  Modul: A STUBBED document AND window, NOT jsdom.

  This suite runs `environment: 'node'` and every other file in it is a pure
  function over data - that is deliberate, and it is why the whole thing takes
  two seconds. Pulling in jsdom to test six event listeners would change the
  character of the suite for one file.

  Node has had EventTarget and Event as globals since 16, so the two objects
  this module actually touches can be the real thing rather than a mock: add,
  remove and dispatch all behave exactly as they do in a browser, including
  removeEventListener identity semantics - which is what the unsubscribe test
  turns on.
*/
let visibility: 'visible' | 'hidden' = 'hidden';

function installDom() {
  const doc = new EventTarget() as EventTarget & { visibilityState: string };
  Object.defineProperty(doc, 'visibilityState', { get: () => visibility });
  (globalThis as { document?: unknown }).document = doc;
  (globalThis as { window?: unknown }).window = new EventTarget();
}

function setVisibility(state: 'visible' | 'hidden') {
  visibility = state;
  (globalThis as unknown as { document: EventTarget }).document.dispatchEvent(
    new Event('visibilitychange'),
  );
}

/*
  Modul: EVERY TEST GETS ITS OWN MINUTE, and the first version did not.

  The resume debounce is module-level state - it has to be, because the whole
  point is to collapse three listeners firing for one event. Pinning the fake
  clock to the same instant in every beforeEach meant test two opened with
  lastResumeAt already set to "now", so its resume was debounced away and four
  tests failed for a reason that had nothing to do with the code under test.

  Advancing the clock per test is the honest fix. Exporting a reset would have
  been test-only API on a module that does not otherwise need one.
*/
const CLOCK_START = Date.parse('2026-09-09T12:00:00Z');
let testIndex = 0;

beforeEach(() => {
  resumeFromBackground.mockClear();
  suspendForBackground.mockClear();
  visibility = 'hidden';
  installDom();
  vi.useFakeTimers();
  // Ten minutes apart, and the stride has to EXCEED anything a single test
  // advances by (a minute, above) - otherwise the clock walks backwards
  // between tests and the debounce swallows the next resume, which is how the
  // native-shell test failed on its own after the first fix.
  vi.setSystemTime(new Date(CLOCK_START + testIndex++ * 600_000));
});

afterEach(() => {
  stopWatching?.();
  stopWatching = null;
  vi.useRealTimers();
  delete (globalThis as { Capacitor?: unknown }).Capacitor;
  delete (globalThis as { document?: unknown }).document;
  delete (globalThis as { window?: unknown }).window;
});

describe('the app coming back to the foreground', () => {
  it('tells the connection when the page becomes visible', () => {
    stopWatching = watchAppLifecycle();

    setVisibility('visible');

    expect(resumeFromBackground).toHaveBeenCalledTimes(1);
  });

  it('says nothing when the page is being hidden', () => {
    stopWatching = watchAppLifecycle();

    setVisibility('hidden');

    // Going away is never a RESUME. What it is on a phone - a reason to put
    // the socket down - is the next describe block.
    expect(resumeFromBackground).not.toHaveBeenCalled();
  });

  it('collapses three sources of one resume into a single call', () => {
    // Modul: visibilitychange, pageshow and Capacitor's appStateChange all
    // describe the same moment and all three can fire for it. Without the
    // debounce, one resume would ask the connection three times whether its
    // socket is stale - and on a genuinely dead socket, each would close and
    // reopen.
    stopWatching = watchAppLifecycle();

    setVisibility('visible');
    (globalThis as unknown as { window: EventTarget }).window.dispatchEvent(new Event('pageshow'));
    setVisibility('visible');

    expect(resumeFromBackground).toHaveBeenCalledTimes(1);
  });

  it('treats a later resume as a new one', () => {
    stopWatching = watchAppLifecycle();

    setVisibility('visible');
    vi.setSystemTime(new Date(Date.now() + 60_000));
    setVisibility('visible');

    expect(resumeFromBackground).toHaveBeenCalledTimes(2);
  });

  it('listens to the native shell when there is one', () => {
    const handlers: Record<string, (state: { isActive: boolean }) => void> = {};
    (globalThis as { Capacitor?: unknown }).Capacitor = {
      Plugins: {
        App: {
          addListener: (event: string, handler: (state: { isActive: boolean }) => void) => {
            handlers[event] = handler;
            return { remove: () => {} };
          },
        },
      },
    };

    stopWatching = watchAppLifecycle();
    expect(handlers.appStateChange).toBeTypeOf('function');

    handlers.appStateChange({ isActive: true });
    expect(resumeFromBackground).toHaveBeenCalledTimes(1);

    // Going inactive is not a resume.
    vi.setSystemTime(new Date(Date.now() + 60_000));
    handlers.appStateChange({ isActive: false });
    expect(resumeFromBackground).toHaveBeenCalledTimes(1);
  });

  it('survives a native plugin that throws, because the web path still works', () => {
    // Modul: `@capacitor/app` is not a dependency of the web build and the
    // plugin object is whatever the native shell injected. A browser build
    // must never be taken down by reading it.
    (globalThis as { Capacitor?: unknown }).Capacitor = {
      Plugins: {
        App: {
          addListener: () => {
            throw new Error('no native bridge here');
          },
        },
      },
    };

    expect(() => {
      stopWatching = watchAppLifecycle();
    }).not.toThrow();

    setVisibility('visible');
    expect(resumeFromBackground).toHaveBeenCalledTimes(1);
  });

  it('stops listening when unsubscribed', () => {
    const stop = watchAppLifecycle();
    stop();

    setVisibility('visible');

    expect(resumeFromBackground).not.toHaveBeenCalled();
  });
});

/*
  GOING TO THE BACKGROUND, WHICH NOTHING USED TO NOTICE.

  TASK_BOARD D3 asked what the app does when backgrounded for eight hours -
  hold a socket and drain a battery, or shut down cleanly and rely on offline
  catch-up - and guessed the second was "probably already what happens".

  It was not. Nothing closed anything. The socket survived until the OS froze
  the WebView, which on Android is minutes, and for those minutes a pocketed
  phone went on receiving and decoding a 10 Hz packet stream. The behaviour was
  the operating system's to decide and it differed per platform.

  Closing costs nothing here because the SIMULATION IS ON THE SERVER: a
  disconnected client is one that is not watching, and offline catch-up pays
  for the gap - the same mechanism somebody who closes the app entirely already
  relies on.
*/
describe('going to the background', () => {
  function installNativeShell(): Record<string, (state: { isActive: boolean }) => void> {
    const handlers: Record<string, (state: { isActive: boolean }) => void> = {};
    (globalThis as { Capacitor?: unknown }).Capacitor = {
      isNativePlatform: () => true,
      Plugins: {
        App: {
          addListener: (event: string, handler: (state: { isActive: boolean }) => void) => {
            handlers[event] = handler;
            return { remove: () => {} };
          },
        },
      },
    };
    return handlers;
  }

  it('puts the socket down when a PHONE goes to the background', () => {
    const handlers = installNativeShell();
    stopWatching = watchAppLifecycle();

    handlers.appStateChange({ isActive: false });

    expect(suspendForBackground).toHaveBeenCalledTimes(1);
  });

  it('does the same on visibilitychange, because the two sources cover different platforms', () => {
    installNativeShell();
    stopWatching = watchAppLifecycle();

    setVisibility('hidden');

    expect(suspendForBackground).toHaveBeenCalledTimes(1);
  });

  it('LEAVES A DESKTOP TAB ALONE', () => {
    // Modul: the case this must not break. A hidden browser tab is a
    // legitimate way to leave an idle game running - the player switched tabs,
    // they did not leave - and closing the socket there costs them the live
    // view and any chat arriving in it, for no battery saved on a machine that
    // is plugged in. The platform is what separates the two, not the
    // visibility state, which is identical in both.
    stopWatching = watchAppLifecycle();

    setVisibility('hidden');

    expect(suspendForBackground).not.toHaveBeenCalled();
  });

  it('comes back when the app does', () => {
    const handlers = installNativeShell();
    stopWatching = watchAppLifecycle();

    handlers.appStateChange({ isActive: false });
    vi.setSystemTime(new Date(Date.now() + 60_000));
    handlers.appStateChange({ isActive: true });

    expect(suspendForBackground).toHaveBeenCalledTimes(1);
    expect(resumeFromBackground).toHaveBeenCalledTimes(1);
  });

  it('a suspend clears the resume debounce, so an immediate return still reconnects', () => {
    // Modul: the app-switcher case. Backgrounding and returning inside the
    // 400ms debounce window is one gesture on a phone, and the resume must not
    // be swallowed as a duplicate of the one before the suspend - that would
    // leave the game on a socket it deliberately closed.
    const handlers = installNativeShell();
    stopWatching = watchAppLifecycle();

    handlers.appStateChange({ isActive: true });
    expect(resumeFromBackground).toHaveBeenCalledTimes(1);

    handlers.appStateChange({ isActive: false });
    handlers.appStateChange({ isActive: true });

    expect(resumeFromBackground).toHaveBeenCalledTimes(2);
  });
});
