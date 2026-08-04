import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { cpSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';

// Modul: the artwork lives OUTSIDE this package, in client/Assets/Images/
// SpritesWeb, and is deliberately not duplicated into the repo - the server
// links the same tree in through FolkIdle.Server.csproj so the two clients
// cannot drift. A hosted build still needs its own copy, because serving 214
// files off the API instance is the worst place to serve them from.
//
// Done here rather than in the build command so that the deploy needs no
// special invocation: anything that runs `vite build` gets the art. Not
// through publicDir, which Vite allows only one of and which would have to
// point outside the project root.
//
// Copied in `closeBundle` rather than `buildStart`, because Vite empties
// outDir as part of the build - anything written before that is thrown away.
function copySprites() {
  const source = resolve(__dirname, '..', 'client', 'Assets', 'Images', 'SpritesWeb');
  return {
    name: 'folkidle-copy-sprites',
    apply: 'build' as const,
    closeBundle() {
      // Absent in a source checkout without the Unity tree. Warn rather than
      // fail: the build is still valid, the icons just fall back to initials,
      // and a hard failure here would block a client-only checkout entirely.
      if (!existsSync(source)) {
        this.warn(`sprite source not found at ${source} - built without artwork`);
        return;
      }
      cpSync(source, resolve(__dirname, 'dist', 'sprites'), { recursive: true });
    },
  };
}

export default defineConfig({
  plugins: [svelte(), copySprites()],
  server: {
    // Pinned, not left to Vite's "first free port" default: the server's CORS
    // allow-list is an exact-match list of origins (FOLKIDLE_WEB_ORIGINS), so
    // a dev server that silently moved to 5174 would fail every request with
    // an opaque browser CORS error rather than a useful one.
    port: 5173,
    strictPort: true,
  },
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
  },
});
