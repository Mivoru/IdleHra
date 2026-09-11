// Modul: `cap sync` ON WINDOWS WRITES A Package.swift THAT SWIFT CANNOT PARSE.
//
// The Capacitor CLI builds each local plugin's path with Node's `path.relative`
// and interpolates the result straight into a Swift string literal. On Linux
// and macOS that yields `../../../node_modules/@capacitor/app`. On Windows it
// yields `..\..\..\node_modules\@capacitor\app`, and a backslash in a Swift
// string literal is an ESCAPE INTRODUCER:
//
//   "..\..\..\node_modules\@capacitor\app"
//        ^\.  invalid escape     ^\n  is a NEWLINE     ^\@ invalid escape
//
// So the manifest does not merely point somewhere wrong - it does not compile,
// and the iOS project committed from a Windows box cannot be built on a Mac at
// all. Nobody found out because this repository's only Apple toolchain is
// hypothetical.
//
// It also breaks CI, which is the part that is checkable today: the client job
// runs `npx cap sync` on Linux and then fails on a dirty tree. Linux
// regenerates forward slashes, the committed file has backslashes, the diff is
// never empty - so `Verify the committed native projects are not stale` fails
// on every push from this machine, for ever.
//
// WHY A NORMALISER RATHER THAN A GUARD ALONE. A test that merely FAILS on a
// backslash would make every Windows `npm run sync` produce a tree that has to
// be hand-repaired before it can be committed - a chore nobody remembers,
// which is how the file got this way. This runs as part of `sync`, so the
// separator is correct by construction; `tests/nativeProjects.test.ts` is the
// guard for the case where somebody syncs without it.
//
// DELIBERATELY NARROW. Only `path:` arguments in Package.swift are touched.
// A blanket backslash replacement over the file would corrupt the one place a
// backslash is legitimate - `versionName.split("\\.")`-style literals in
// generated Gradle, and any Swift regex that lands here later.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const packageSwift = join(here, '..', 'ios', 'App', 'CapApp-SPM', 'Package.swift');

/** `--check` reports instead of writing, for a CI lane that wants to assert. */
const checkOnly = process.argv.includes('--check');

/**
 * Rewrites `path: "..\a\b"` to `path: "../a/b"`.
 *
 * Returns the corrected text and how many literals it touched, so the caller
 * can stay silent when there was nothing to do - a build step that prints on
 * every run is one people stop reading.
 */
export function normalizePackageSwift(text) {
  let fixed = 0;
  const out = text.replace(/path:\s*"([^"]*)"/g, (whole, value) => {
    if (!value.includes('\\')) return whole;
    fixed += 1;
    return whole.replace(value, value.replaceAll('\\', '/'));
  });
  return { text: out, fixed };
}

if (!existsSync(packageSwift)) {
  // No iOS project checked out is not an error - `cap add ios` may simply not
  // have been run here. Saying nothing is right; failing would break `sync`
  // on an Android-only machine.
  process.exit(0);
}

const original = readFileSync(packageSwift, 'utf8');
const { text, fixed } = normalizePackageSwift(original);

if (fixed === 0) process.exit(0);

if (checkOnly) {
  console.error(
    `Package.swift has ${fixed} path literal(s) with Windows separators. ` +
      'Run: node scripts/normalize-native.mjs',
  );
  process.exit(1);
}

writeFileSync(packageSwift, text, 'utf8');
console.log(`normalized ${fixed} Package.swift path literal(s) to forward slashes`);
