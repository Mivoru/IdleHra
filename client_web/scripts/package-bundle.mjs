// Modul: PACKAGES dist/ AS AN OVER-THE-AIR BUNDLE, and stamps it with a version
// the phone can compare against what it is already running.
//
// The shape modern games use: the store install is the SHELL, and the content
// arrives over the air. Here the shell is the APK (Java, the Capacitor plugins,
// the permissions) and the content is everything in dist/ - so a gameplay
// change reaches a player by opening the app, not by reinstalling it.
//
// WHAT THIS DOES NOT COVER, and cannot: anything native. A new Capacitor
// plugin, a new Android permission, a Gradle change - none of that is in dist/,
// so none of it can travel this way. That is why the manifest also carries
// `minNative`; see the server endpoint.
//
// VERSIONING IS THE COMMIT COUNT, as `1.0.<n>`.
//
//   - It is MONOTONIC, which is the only property the comparison needs.
//   - It is reproducible from the checkout, so the same commit always produces
//     the same version and two machines cannot disagree.
//   - It sorts above the APK's own `versionName` of "1.0" under semver, which
//     is what the plugin compares against on a fresh install. A bundle that did
//     not sort above it would be ignored for ever, silently.
//
// Deliberately NOT a timestamp: two builds of the same commit would differ, and
// the phone would re-download 19 MB to arrive at identical files.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, readdirSync, statSync, readFileSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const here = dirname(fileURLToPath(import.meta.url));
const clientRoot = join(here, '..');
const dist = join(clientRoot, 'dist');
const outDir = join(clientRoot, 'ota');

function resolveVersion() {
  // Modul: THE ENVIRONMENT WINS, because the Docker build has no git history.
  //
  // The web image's build context is the repository without .git, so counting
  // commits inside it answers with nothing useful. The deploy computes the
  // version on the host - which does have the history - and passes it in as a
  // build argument. Locally there is no such argument and the git path is the
  // convenient one.
  const injected = (process.env.FOLKIDLE_BUNDLE_VERSION ?? '').trim();
  if (injected.length > 0) return injected;

  const count = execFileSync('git', ['rev-list', '--count', 'HEAD'], {
    cwd: clientRoot,
    encoding: 'utf8',
  }).trim();
  return `1.0.${count}`;
}

if (!existsSync(dist)) {
  console.error('dist/ is missing - run a web build first (npm run build:web).');
  process.exit(1);
}

const version = resolveVersion();

rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

// Modul: ZIPPED WITH THE SYSTEM ZIP, not with a new npm dependency.
//
// This repository already refused `@capacitor/assets` because it drags in
// `sharp`, a native binary that has to match the platform - the icon generator
// rasterises with Playwright instead, which was already here. Same reasoning:
// PowerShell's Compress-Archive on Windows and `zip` elsewhere are both already
// on the machines that run this, and a build step is a bad place to discover a
// postinstall script.
const zipPath = join(outDir, `${version}.zip`);

function zipDist() {
  if (process.platform === 'win32') {
    // -Path dist\* so the archive has the web root at its TOP LEVEL. Zipping
    // the directory itself would nest everything under `dist/`, and the plugin
    // unpacks the archive AS the web root - index.html has to be at the root or
    // the bundle loads a blank page.
    execFileSync(
      'powershell',
      [
        '-NoProfile',
        '-Command',
        `Compress-Archive -Path '${dist}\\*' -DestinationPath '${zipPath}' -Force`,
      ],
      { stdio: 'inherit' },
    );
    return;
  }
  execFileSync('zip', ['-r', '-q', zipPath, '.'], { cwd: dist, stdio: 'inherit' });
}

zipDist();

const bytes = statSync(zipPath).size;
const sha256 = createHash('sha256').update(readFileSync(zipPath)).digest('hex');

/** Every file in dist, for the size report only - see the note on diffing below. */
function walk(dir) {
  let files = 0;
  let size = 0;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      const sub = walk(full);
      files += sub.files;
      size += sub.size;
    } else {
      files += 1;
      size += statSync(full).size;
    }
  }
  return { files, size };
}

const tree = walk(dist);
const mb = (n) => `${(n / 1024 / 1024).toFixed(1)} MB`;

console.log(`bundle ${version}`);
console.log(`  ${tree.files} files, ${mb(tree.size)} on disk -> ${mb(bytes)} zipped`);
console.log(`  sha256 ${sha256}`);
console.log(`  ${relative(clientRoot, zipPath)}`);

// Modul: THE WHOLE BUNDLE TRAVELS, and that is a deliberate first step rather
// than an oversight.
//
// About 18 of these megabytes are sprites that change only when somebody draws
// something; the app CODE is around 700 KB. The plugin does support
// differential downloads - `getLatest` may return a `manifest` of
// `{file_name, file_hash, download_url}` and it then fetches only the files
// whose hashes differ - which would cut a typical update by roughly 25x.
//
// It is not used yet because the hash format has to match the plugin's own
// byte for byte, and getting that wrong does not fail loudly: it silently
// re-downloads everything, or worse, decides nothing changed. That needs a
// device to verify and there has not been one. Full-bundle updates are correct
// today and slower than they need to be, which is the right order to do these
// things in.
console.log('');
console.log(`  note: full-bundle update. ~${mb(tree.size - 720 * 1024)} of this is artwork that`);
console.log('        rarely changes - see the differential note in this script.');
