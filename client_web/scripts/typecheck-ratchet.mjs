// Modul: `npm run build` HAS BEEN BROKEN FOR MONTHS, ON EVERY MACHINE.
//
// `build` chains `svelte-check`, `svelte-check` exits 1 whenever there is any
// error at all, and this repository has a documented baseline of four - the
// hidden Guild War handlers in GuildOps.svelte. So `build` has never completed,
// and neither has anything that chains it: `npm run sync` and
// `npm run build:android`, which are the two commands MOBILE.md tells you to
// run to package the app.
//
// Production never noticed because the Dockerfile calls `npx vite build`
// directly, and CI never noticed because it reimplements the ratchet in a shell
// block inside deploy.yml.
//
// THAT SHELL BLOCK IS WHY THIS FILE EXISTS. The rule "fail only if the count
// grows" was written down once, in YAML, in a place no developer can run - so
// the same rule could not be applied to `build` without copying it. Two copies
// of one truth is this codebase's dominant bug class, and the honest fix is to
// have one implementation that both CI and the build script call.
//
// A COUNT IS A WEAK GUARD AND IS KNOWN TO BE WEAK: the baseline sat at 9 for a
// long time and silently turned over into an entirely different 9. So this
// prints every error it finds, every time, rather than only on failure - a
// number that nobody reads is how the last turnover went unnoticed. Prefer
// fixing to raising the number.
//
// Run: npm run check:ratchet   (and it is what `npm run build` now chains)
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';

/**
 * How many svelte-check errors are tolerated.
 *
 * All four are in `GuildOps.svelte` and all four are the same thing: `defend`,
 * `attackShard`, `takeTurn` and `damageDelta` are the handlers of the Guild War
 * UI, which was HIDDEN rather than removed. They are dead only for as long as
 * that feature stays hidden, and docs/FUTURE_PLANS.md lists Guild Wars as
 * planned - so deleting them is a product decision, not a cleanup.
 *
 * Resolve the Guild War question and this becomes 0, and this whole file
 * becomes a plain `svelte-check`.
 */
const BASELINE = 4;

// Modul: RUN THE SCRIPT WITH THIS NODE, not through npx.
//
// `npx` is a shell shim, and on Windows it is `npx.cmd` - which Node 24 refuses
// to spawn without `shell: true` (EINVAL), while `shell: true` earns a
// deprecation warning about passing arguments through a shell. Resolving the
// package's own entry point and handing it to `process.execPath` sidesteps both
// and does not care what platform it is on.
const require = createRequire(import.meta.url);
const svelteCheckBin = resolve(
  dirname(require.resolve('svelte-check/package.json')),
  require('svelte-check/package.json').bin,
);

const result = spawnSync(
  process.execPath,
  [svelteCheckBin, '--tsconfig', './tsconfig.json'],
  { encoding: 'utf8' },
);

// Modul: BOTH streams. svelte-check writes its findings to stdout in the
// default reporter and to stderr in some versions, and a ratchet that reads
// the wrong one counts zero errors and passes everything.
const output = `${result.stdout ?? ''}${result.stderr ?? ''}`;
const errorLines = output.split(/\r?\n/).filter((line) => / ERROR /.test(line));

for (const line of errorLines) console.log(line);

console.log(`\nsvelte-check errors: ${errorLines.length} (baseline ${BASELINE})`);

if (errorLines.length > BASELINE) {
  console.error(
    `\nsvelte-check REGRESSED: ${errorLines.length} errors against a baseline of ${BASELINE}.\n` +
      'Fix the new one rather than raising the baseline.\n',
  );
  process.exit(1);
}

if (errorLines.length < BASELINE) {
  // Modul: not a failure, but it must not pass in silence either - a baseline
  // that is higher than reality is a budget somebody can spend without
  // noticing. That is exactly how 9 turned into a different 9.
  console.log(
    `\nFewer errors than the baseline. Lower BASELINE in this file to ${errorLines.length} ` +
      'so the slack cannot be spent silently.\n',
  );
}

// Modul: a CRASH is not a pass. svelte-check exits 1 for "there were errors",
// which the count above has already judged - but it exits 2 (or dies on a
// signal) when it could not run at all, and treating that as "no errors found"
// would turn a broken type-checker into a green build.
if (result.error || result.status === null || result.status > 1) {
  console.error(`\nsvelte-check could not run (exit ${result.status}): ${result.error ?? 'unknown'}\n`);
  process.exit(1);
}
