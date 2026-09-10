// Modul: EVERY APP ICON AND SPLASH, RASTERISED FROM TWO SVGs.
//
// What shipped in android/ and ios/ was Capacitor's placeholder - a blue
// asterisk on white - on a game whose whole look is charred oak and brass. A
// store listing led by a framework logo reads as a template, and TASK_BOARD C1
// has said "there are no icons" since the mobile plan was written.
//
// WHY THIS SCRIPT RATHER THAN @capacitor/assets. That tool does the same job
// and pulls in sharp, which is a native binary that has to match the platform
// - a dependency this repo would carry on every machine and every CI runner to
// produce fourteen files that change about once a year. Playwright is already
// a devDependency here (four geometry checkers use it) and rasterises an SVG
// exactly as the browser would.
//
// THE OUTPUTS ARE COMMITTED, and that is deliberate. `android/` and `ios/` are
// in the repository, the CI device lane runs `cap sync` and then fails on a
// dirty tree, and a build machine must not need a browser to produce an icon.
// This script is the SOURCE step; run it when the artwork changes and commit
// what it writes.
//
// Run: npm run generate:icons
import { chromium } from 'playwright';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');

const ICON = resolve(root, 'resources/icon.svg');
const SPLASH = resolve(root, 'resources/splash.svg');

/**
 * Android launcher icons.
 *
 * `ic_launcher` and `ic_launcher_round` are the legacy pre-Oreo icons and are
 * the FULL artwork at the density's size. `ic_launcher_foreground` is the
 * adaptive-icon layer and is 108dp at the same density - larger than the icon
 * it sits inside, because the launcher crops it.
 */
const ANDROID_DENSITIES = [
  { dir: 'mdpi', launcher: 48, foreground: 108 },
  { dir: 'hdpi', launcher: 72, foreground: 162 },
  { dir: 'xhdpi', launcher: 96, foreground: 216 },
  { dir: 'xxhdpi', launcher: 144, foreground: 324 },
  { dir: 'xxxhdpi', launcher: 192, foreground: 432 },
];

/**
 * Splash buckets Capacitor generates. All twelve get the same square image -
 * the platform centre-crops - which is why splash.svg keeps everything that
 * matters well inside the middle.
 */
const ANDROID_SPLASH_DIRS = [
  'drawable',
  'drawable-port-mdpi',
  'drawable-port-hdpi',
  'drawable-port-xhdpi',
  'drawable-port-xxhdpi',
  'drawable-port-xxxhdpi',
  'drawable-land-mdpi',
  'drawable-land-hdpi',
  'drawable-land-xhdpi',
  'drawable-land-xxhdpi',
  'drawable-land-xxxhdpi',
];

const browser = await chromium.launch();

/**
 * Renders one SVG at one size.
 *
 * Modul: `omitBackground` plus a transparent page. Without it every icon comes
 * out on Chromium's default white, which on a round launcher mask shows as a
 * white square with the artwork inside it - the exact "obviously generated"
 * look this replaces.
 */
async function render(svgPath, size, outPath) {
  const svg = readFileSync(svgPath, 'utf8');
  const page = await browser.newPage({
    viewport: { width: size, height: size },
    deviceScaleFactor: 1,
  });

  await page.setContent(
    `<!doctype html><style>
       html,body{margin:0;padding:0;background:transparent}
       svg{display:block;width:${size}px;height:${size}px}
     </style>${svg}`,
    { waitUntil: 'load' },
  );

  const buffer = await page.screenshot({ omitBackground: true, type: 'png' });
  await page.close();

  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, buffer);
  return buffer.length;
}

let written = 0;
let bytes = 0;

async function emit(svgPath, size, outPath, label) {
  const length = await render(svgPath, size, outPath);
  written += 1;
  bytes += length;
  console.log(`  ${label.padEnd(46)} ${String(size).padStart(4)}px  ${(length / 1024).toFixed(1)}kB`);
}

console.log('\nAndroid launcher icons');
for (const density of ANDROID_DENSITIES) {
  const dir = resolve(root, `android/app/src/main/res/mipmap-${density.dir}`);
  await emit(ICON, density.launcher, `${dir}/ic_launcher.png`, `mipmap-${density.dir}/ic_launcher.png`);
  await emit(ICON, density.launcher, `${dir}/ic_launcher_round.png`, `mipmap-${density.dir}/ic_launcher_round.png`);
  await emit(ICON, density.foreground, `${dir}/ic_launcher_foreground.png`, `mipmap-${density.dir}/ic_launcher_foreground.png`);
}

console.log('\nAndroid splash');
for (const dir of ANDROID_SPLASH_DIRS) {
  const target = resolve(root, `android/app/src/main/res/${dir}/splash.png`);
  if (!existsSync(dirname(target))) continue;
  await emit(SPLASH, 1024, target, `${dir}/splash.png`);
}

console.log('\niOS');
// One 1024 icon: modern Xcode takes a single size and derives the rest.
await emit(
  ICON,
  1024,
  resolve(root, 'ios/App/App/Assets.xcassets/AppIcon.appiconset/AppIcon-512@2x.png'),
  'AppIcon.appiconset/AppIcon-512@2x.png',
);
for (const name of ['splash-2732x2732.png', 'splash-2732x2732-1.png', 'splash-2732x2732-2.png']) {
  await emit(
    SPLASH,
    2732,
    resolve(root, `ios/App/App/Assets.xcassets/Splash.imageset/${name}`),
    `Splash.imageset/${name}`,
  );
}

await browser.close();

console.log(`\n${written} files, ${(bytes / 1024).toFixed(0)}kB total.`);
console.log('These are committed artefacts - commit what changed.\n');

// Modul: the adaptive icon's BACKGROUND colour is a resource, not a PNG, and
// it was Capacitor's white. Left white, a launcher that masks to a circle
// shows a white ring around dark artwork. Written here so the colour cannot
// drift from the SVG it sits behind.
const backgroundXml = resolve(root, 'android/app/src/main/res/values/ic_launcher_background.xml');
writeFileSync(
  backgroundXml,
  `<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="ic_launcher_background">#100D0A</color>\n</resources>\n`,
);
console.log('ic_launcher_background.xml set to #100D0A (app.css --bg).\n');
