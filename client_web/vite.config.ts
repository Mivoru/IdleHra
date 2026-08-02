import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
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
