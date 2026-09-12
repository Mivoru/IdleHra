// Modul: stamps the build so the game can say which one it is.
//
// TWO NUMBERS, and they answer different questions:
//
//   APP_VERSION  package.json's version, bumped by hand. Human, semantic, and
//                what the "what's new" window keys off - it must match the
//                newest entry in releaseNotes.ts, which a test enforces.
//
//   BUILD_ID     the moment this build ran. Changes on EVERY build even when
//                the version does not, which is the only property that makes
//                stale-client detection work.
//
// Writes public/version.json, which ships as a static file beside the bundle.
// That file is the whole mechanism for noticing a stale tab: a browser running
// yesterday's bundle holds yesterday's BUILD_ID as a compile-time constant, but
// fetching /version.json reaches whatever is deployed NOW. Comparing the two
// needs no server endpoint, no wire field and no database - which is why it is
// done this way rather than over the socket.
//
// Generated, not committed - see .gitignore. A checkout without it is fine: the
// client treats a failed fetch as "no information" and says nothing.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');

const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

/** ISO seconds, no punctuation: 20260913T010704Z. Sorts, and is readable. */
function buildId() {
  return new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
}

export function stamp() {
  const version = { version: pkg.version, buildId: buildId() };

  const target = join(root, 'public');
  mkdirSync(target, { recursive: true });
  writeFileSync(join(target, 'version.json'), JSON.stringify(version, null, 2) + '\n', 'utf8');

  return version;
}

// Written on import so vite.config.ts can call stamp() and hand the same values
// to `define` - one stamp per build, rather than the config and the file
// disagreeing by however long the build takes.
if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith('stamp-version.mjs')) {
  const v = stamp();
  console.log(`stamped ${v.version} (build ${v.buildId})`);
}
