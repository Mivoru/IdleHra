// Modul: EVERY APP ICON, SPLASH AND LOADING-SCREEN IMAGE, FROM THREE SOURCES.
//
//   resources/icon.png               the painted app icon (rounded frame,
//                                    transparent corners, 1024 px)
//   resources/loading-landscape.jpg  the loading-screen art for wide screens
//   resources/loading-portrait.jpg   the same scene recomposed for phones
//
// The icon used to be an SVG drawn in code (a shield on charred oak). The
// owner replaced it with painted artwork on 2026-10-08, so the sources became
// raster and this script stopped rasterising and started COMPOSING: the same
// picture is placed at a different scale and on a different background for
// each target, because each platform crops or fills it differently (see
// `place` below).
//
// WHY THIS SCRIPT RATHER THAN @capacitor/assets OR sharp. Both pull in sharp,
// a native binary that has to match the platform - a dependency this repo
// would carry on every machine and every CI runner to produce files that
// change about once a year. Playwright is already a devDependency here (the
// geometry checkers use it) and it scales an image exactly as the browser
// would.
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

/** app.css --bg (dark theme), and the colour every opaque target sits on. */
const BG = '#100d0a';

const dataUrl = (path, mime) => `data:${mime};base64,${readFileSync(resolve(root, path)).toString('base64')}`;
const ICON = dataUrl('resources/icon.png', 'image/png');
const LANDSCAPE = dataUrl('resources/loading-landscape.jpg', 'image/jpeg');
const PORTRAIT = dataUrl('resources/loading-portrait.jpg', 'image/jpeg');

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
 * Splash buckets Capacitor generates. All of them get the same square image -
 * the platform centre-crops - which is why the splash is the icon, small, in
 * the middle of a plain field: nothing that matters is near an edge.
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
 * Renders one image into a width x height canvas.
 *
 * `scale` is the fraction of the canvas the image's longer side fills
 * (`contain`), or `'cover'` to fill the canvas and crop the overflow - the
 * loading art. `bg` null leaves the canvas transparent.
 *
 * Modul: `omitBackground` plus a transparent page. Without it every
 * transparent target comes out on Chromium's default white, which on a round
 * launcher mask shows as a white square with the artwork inside it.
 */
async function place(src, { width, height = width, scale = 1, bg = null, type = 'png', quality }) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
  const fit =
    scale === 'cover'
      ? 'width:100%;height:100%;object-fit:cover'
      : `width:${scale * 100}%;height:${scale * 100}%;object-fit:contain`;
  await page.setContent(
    `<!doctype html><style>
       html,body{margin:0;padding:0;width:100%;height:100%;background:${bg ?? 'transparent'}}
       body{display:flex;align-items:center;justify-content:center;overflow:hidden}
       img{display:block;${fit}}
     </style><img src="${src}">`,
    { waitUntil: 'load' },
  );
  const buffer = await page.screenshot({
    omitBackground: bg === null,
    type,
    ...(type === 'jpeg' ? { quality } : {}),
  });
  await page.close();
  return buffer;
}

let written = 0;
let bytes = 0;

async function emit(src, options, outPath, label) {
  const buffer = await place(src, options);
  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, buffer);
  written += 1;
  bytes += buffer.length;
  const size = `${options.width}x${options.height ?? options.width}`;
  console.log(`  ${label.padEnd(50)} ${size.padStart(9)}  ${(buffer.length / 1024).toFixed(1)}kB`);
}

// Modul: THE ADAPTIVE FOREGROUND IS SHRUNK TO 0.62 OF ITS LAYER. The launcher
// shows the middle 72dp of 108 and may mask that to a circle; the guaranteed
// zone is a 66dp circle. The painted icon is a complete framed tile with the
// word FOLKIDLE along its bottom edge, so drawn full-bleed the mask would cut
// the name in half. At 0.62 the frame's corners are what the circle trims.
console.log('\nAndroid launcher icons');
for (const density of ANDROID_DENSITIES) {
  const dir = resolve(root, `android/app/src/main/res/mipmap-${density.dir}`);
  await emit(ICON, { width: density.launcher }, `${dir}/ic_launcher.png`, `mipmap-${density.dir}/ic_launcher.png`);
  await emit(ICON, { width: density.launcher, scale: 0.9 }, `${dir}/ic_launcher_round.png`, `mipmap-${density.dir}/ic_launcher_round.png`);
  await emit(
    ICON,
    { width: density.foreground, scale: 0.62 },
    `${dir}/ic_launcher_foreground.png`,
    `mipmap-${density.dir}/ic_launcher_foreground.png`,
  );
}

// Modul: THE NATIVE SPLASH IS THE ICON, NOT THE LOADING ART. Android 12+
// ignores a splash bitmap and draws the launcher icon on a flat colour
// whatever this script writes, and iOS's launch image is aspect-filled into
// any shape of screen, which would crop the painted title. So both native
// splashes show the same thing - the icon on --bg - and the illustrated
// loading screen (index.html, #boot) takes over as soon as the WebView paints.
console.log('\nAndroid splash');
for (const dir of ANDROID_SPLASH_DIRS) {
  const target = resolve(root, `android/app/src/main/res/${dir}/splash.png`);
  if (!existsSync(dirname(target))) continue;
  await emit(ICON, { width: 1024, scale: 0.3, bg: BG }, target, `${dir}/splash.png`);
}

console.log('\niOS');
// One 1024 icon: modern Xcode takes a single size and derives the rest. It
// must be OPAQUE - iOS fills transparency with black and applies its own
// rounded mask - so the transparent corners become --bg here.
await emit(
  ICON,
  { width: 1024, bg: BG },
  resolve(root, 'ios/App/App/Assets.xcassets/AppIcon.appiconset/AppIcon-512@2x.png'),
  'AppIcon.appiconset/AppIcon-512@2x.png',
);
for (const name of ['splash-2732x2732.png', 'splash-2732x2732-1.png', 'splash-2732x2732-2.png']) {
  await emit(
    ICON,
    { width: 2732, scale: 0.22, bg: BG },
    resolve(root, `ios/App/App/Assets.xcassets/Splash.imageset/${name}`),
    `Splash.imageset/${name}`,
  );
}

// Modul: MOBILE WEB, task 110d. "Add to Home Screen" from a phone's browser
// reads public/manifest.webmanifest and, on iOS, the apple-touch-icon link in
// index.html (180). Vite copies public/ to the site root as-is.
//   icon-192 / icon-512  "any": the framed tile as painted, transparent corners
//   icon-maskable-512    the tile at 0.8 on --bg - a maskable icon's safe zone
//                        is the middle 80% circle, so full-bleed would lose
//                        the name to a circular mask
//   apple-touch-icon     opaque, because iOS fills transparency with black
//   favicon-48           what a search result shows beside the link; Google
//                        wants a multiple of 48 px
console.log('\nMobile web (public/icons)');
const webIcons = [
  [{ width: 192 }, 'icon-192.png'],
  [{ width: 512 }, 'icon-512.png'],
  [{ width: 512, scale: 0.8, bg: BG }, 'icon-maskable-512.png'],
  [{ width: 180, bg: BG }, 'apple-touch-icon.png'],
  [{ width: 48 }, 'favicon-48.png'],
];
for (const [options, name] of webIcons) {
  await emit(ICON, options, resolve(root, `public/icons/${name}`), `public/icons/${name}`);
}

// Modul: THE LOADING ART, at two widths per orientation. The sources are
// ~1.2 MB each - shipping those would make the loading screen the slowest
// thing to load. index.html picks one by orientation and width with a
// <picture>, so a phone downloads one ~150 kB file, never both.
console.log('\nLoading screen (public/loading)');
const loadingImages = [
  [LANDSCAPE, 1920, 1072, 'landscape-1920.jpg'],
  [LANDSCAPE, 1280, 715, 'landscape-1280.jpg'],
  [PORTRAIT, 1080, 1935, 'portrait-1080.jpg'],
  [PORTRAIT, 720, 1290, 'portrait-720.jpg'],
];
for (const [src, width, height, name] of loadingImages) {
  await emit(
    src,
    { width, height, scale: 'cover', bg: BG, type: 'jpeg', quality: 78 },
    resolve(root, `public/loading/${name}`),
    `public/loading/${name}`,
  );
}

await browser.close();

console.log(`\n${written} files, ${(bytes / 1024).toFixed(0)}kB total.`);
console.log('These are committed artefacts - commit what changed.\n');

// Modul: the adaptive icon's BACKGROUND colour is a resource, not a PNG, and
// it was Capacitor's white. Left white, a launcher that masks to a circle
// shows a white ring around dark artwork. Written here so the colour cannot
// drift from the artwork it sits behind.
const backgroundXml = resolve(root, 'android/app/src/main/res/values/ic_launcher_background.xml');
writeFileSync(
  backgroundXml,
  `<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="ic_launcher_background">#100D0A</color>\n</resources>\n`,
);
console.log('ic_launcher_background.xml set to #100D0A (app.css --bg).\n');
