// Modul: WHAT THIS CLIENT COSTS A PHONE, MEASURED RATHER THAN ASSUMED.
//
// TASK_BOARD D2: "The client renders a 10 Hz packet stream and VirtualList
// windows an inventory that has reached 17,836 rows. Both are fine on a
// desktop. Neither has been measured on a phone."
//
// This is the half of that a desktop CAN answer honestly. Chromium's CDP will
// slow the main thread by a fixed factor, and a 4x slowdown is the usual stand
// -in for a mid-range Android device against a developer machine. It is a
// STAND-IN and this file says so rather than pretending: it does not model a
// slower GPU, less memory, a throttled radio, or thermal behaviour after
// twenty minutes. Those are A2, on glass.
//
// What it does answer, and what nothing answered before:
//
//   - Does the main thread keep up with the packet stream, or does the event
//     loop fall behind and start dropping frames?
//   - How long is the LONGEST task? Anything over ~200ms is a visible stutter
//     and over 50ms is a missed frame.
//   - Does scrolling a windowed list stay smooth, which is the one screen
//     whose row count grew by four orders of magnitude?
//   - Does memory climb over a few minutes, which is what a leak in a 10 Hz
//     subscriber looks like before anybody notices?
//
// A THRESHOLD IT FAILS ON, deliberately. A measurement a test only prints is
// decoration - this repo has the scar (`ProgressionRateTests` printed "104% of
// the health bar per second" for months). So the budgets below are asserted,
// and they are generous on purpose: they exist to catch a regression that
// makes the client several times worse, not to police a millisecond.
//
// Run: npm run check:perf
import { open, signIn, go } from './screens.mjs';

/** How much slower than this machine to pretend to be. */
const CPU_SLOWDOWN = 4;

/** Seconds of ordinary play to watch, per screen. */
const SAMPLE_SECONDS = 12;

/**
 * The budgets. Exceeded means a regression, not necessarily a defect on a real
 * phone - but it means somebody should look, which is the entire job.
 */
const BUDGET = {
  /** A task this long blocks a tap from being noticed. */
  longestTaskMs: 400,
  /** Sustained work per second of wall clock. Above 1.0 there is no slack. */
  mainThreadBusyRatio: 0.85,
  /** Growth over the run, per screen. A 10 Hz subscriber that leaks shows here. */
  heapGrowthMb: 24,
};

const { browser, page, errors } = await open({ width: 390, height: 844 });

const client = await page.context().newCDPSession(page);
await client.send('Emulation.setCPUThrottlingRate', { rate: CPU_SLOWDOWN });

await signIn(page);

/**
 * Watches the main thread for a while and reports what it did.
 *
 * Modul: measured with PerformanceObserver inside the page rather than from
 * the driver. A long task is defined by the browser as one that blocked the
 * event loop for over 50ms, and that is precisely the definition worth having
 * - counting from outside would measure the driver's round trips instead.
 */
async function watch(seconds) {
  await page.evaluate(() => {
    const state = { longTasks: [], startedAt: performance.now() };
    window.__perf = state;
    state.observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) state.longTasks.push(entry.duration);
    });
    try {
      state.observer.observe({ entryTypes: ['longtask'] });
    } catch {
      // Safari and some Chromium builds lack longtask; the busy ratio below
      // still works, so a missing observer degrades rather than fails.
    }
    // Modul: recorded as ABSENT rather than as zero. performance.memory is
    // Chromium-only, and a missing API that reports "+0.0MB grown" is a
    // measurement that always passes - the decoration this file's own header
    // warns about.
    state.hasMemoryApi = typeof performance.memory !== 'undefined';
    state.heapAtStart = performance.memory?.usedJSHeapSize ?? 0;
  });

  await page.waitForTimeout(seconds * 1000);

  return page.evaluate(() => {
    const state = window.__perf;
    state.observer?.disconnect();
    const elapsed = performance.now() - state.startedAt;
    const busy = state.longTasks.reduce((sum, d) => sum + d, 0);
    return {
      elapsedMs: elapsed,
      taskCount: state.longTasks.length,
      longestMs: state.longTasks.length ? Math.max(...state.longTasks) : 0,
      // Only the portion of each long task OVER the 50ms frame budget is
      // blocking - that is the standard "total blocking time" definition.
      blockingMs: state.longTasks.reduce((sum, d) => sum + Math.max(0, d - 50), 0),
      busyRatio: busy / elapsed,
      hasMemoryApi: state.hasMemoryApi,
      heapGrowthMb: state.hasMemoryApi
        ? ((performance.memory?.usedJSHeapSize ?? 0) - state.heapAtStart) / (1024 * 1024)
        : null,
    };
  });
}

const SCREENS_TO_WATCH = [
  { screen: 'Combat', why: 'the 10 Hz stream, with a health bar interpolating every frame' },
  // Modul: the fixture's chest, NOT the 17,836-row account. VirtualList
  // renders only what is visible, so its cost is meant to be flat in the row
  // count - but "meant to" is what this file exists to stop trusting, and a
  // fixture with a few hundred rows cannot prove it. Measuring the real thing
  // needs an account nobody has locally; noted rather than claimed.
  { screen: 'Chest', why: 'VirtualList over the inventory (fixture-sized)' },
  { screen: 'Village', why: 'the densest static layout in the game' },
];

console.log(`\n=== 390x844, main thread throttled ${CPU_SLOWDOWN}x, ${SAMPLE_SECONDS}s per screen ===\n`);

const failures = [];

for (const target of SCREENS_TO_WATCH) {
  await go(page, target.screen);
  await page.waitForTimeout(1500);

  const idle = await watch(SAMPLE_SECONDS);

  console.log(`${target.screen}  (${target.why})`);
  console.log(
    `  long tasks ${String(idle.taskCount).padStart(4)}   ` +
      `longest ${idle.longestMs.toFixed(0).padStart(4)}ms   ` +
      `blocking ${idle.blockingMs.toFixed(0).padStart(5)}ms   ` +
      `busy ${(idle.busyRatio * 100).toFixed(1).padStart(5)}%   ` +
      `heap ${
        idle.heapGrowthMb === null
          ? 'n/a'
          : `${idle.heapGrowthMb >= 0 ? '+' : ''}${idle.heapGrowthMb.toFixed(1)}MB`
      }`,
  );

  if (idle.longestMs > BUDGET.longestTaskMs) {
    failures.push(`${target.screen}: longest task ${idle.longestMs.toFixed(0)}ms > ${BUDGET.longestTaskMs}ms`);
  }
  if (idle.busyRatio > BUDGET.mainThreadBusyRatio) {
    failures.push(
      `${target.screen}: main thread ${(idle.busyRatio * 100).toFixed(0)}% busy > ${BUDGET.mainThreadBusyRatio * 100}%`,
    );
  }
  if (idle.heapGrowthMb !== null && idle.heapGrowthMb > BUDGET.heapGrowthMb) {
    failures.push(`${target.screen}: heap grew ${idle.heapGrowthMb.toFixed(1)}MB > ${BUDGET.heapGrowthMb}MB`);
  }

  // Modul: the SCROLL is measured separately, because a windowed list is fine
  // standing still and is exactly the thing that stutters when moved. This is
  // the one interaction whose cost grew by four orders of magnitude without
  // anybody looking.
  if (target.screen === 'Chest') {
    await page.evaluate(() => {
      window.__perfScroll = { longTasks: [], startedAt: performance.now() };
      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) window.__perfScroll.longTasks.push(entry.duration);
      });
      try {
        observer.observe({ entryTypes: ['longtask'] });
      } catch {
        /* see watch() */
      }
      window.__perfScroll.observer = observer;
    });

    for (let i = 0; i < 24; i++) {
      await page.mouse.wheel(0, 600);
      await page.waitForTimeout(120);
    }

    const scroll = await page.evaluate(() => {
      const state = window.__perfScroll;
      state.observer?.disconnect();
      return {
        longestMs: state.longTasks.length ? Math.max(...state.longTasks) : 0,
        taskCount: state.longTasks.length,
      };
    });

    console.log(
      `  scrolling: ${String(scroll.taskCount).padStart(4)} long tasks, longest ${scroll.longestMs.toFixed(0)}ms`,
    );
    if (scroll.longestMs > BUDGET.longestTaskMs) {
      failures.push(`Chest scroll: longest task ${scroll.longestMs.toFixed(0)}ms > ${BUDGET.longestTaskMs}ms`);
    }
  }

  console.log('');
}

for (const message of errors) console.log(`console: ${message}`);

await browser.close();

if (failures.length > 0) {
  console.log('OVER BUDGET:');
  for (const failure of failures) console.log(`  ${failure}`);
  console.log(
    '\nA budget here is a stand-in for a phone, not a phone. Over budget means look;' +
      ' it does not by itself mean a player would notice.\n',
  );
  process.exit(1);
}

console.log('Within budget on every screen measured.\n');
console.log(
  'NOT measured here, and only a device can: GPU, memory pressure, radio wake-ups,\n' +
    'battery drain and thermal throttling over an hour. That is TASK_BOARD A2.\n',
);
