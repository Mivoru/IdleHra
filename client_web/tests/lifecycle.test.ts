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

vi.mock('../src/lib/net/connection', () => ({
  connection: {
    get resumeFromBackground() {
      return resumeFromBackground;
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

    // Going away is not an event the connection can act on - the socket is
    // about to be frozen whatever it does.
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
