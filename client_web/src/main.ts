import { mount } from 'svelte';
import './app.css';
import App from './App.svelte';
import { confirmBundleBooted } from './lib/net/liveUpdate';

const app = mount(App, { target: document.getElementById('app')! });

// Modul: AFTER the mount, and that ordering is the whole safety net.
//
// A live-updated bundle is rolled back unless it confirms it booted, so this
// call is the assertion "I work". Made before the mount it would confirm a
// bundle whose entire UI throws at runtime - which this codebase has shipped
// before, with `<Snippet />` instead of `{@render Snippet()}`: svelte-check
// clean, screen dead. Mounting is the cheapest honest evidence that the
// JavaScript parsed and produced a screen.
//
// Not awaited: nothing downstream depends on it and a slow bridge must not
// delay the first paint. See lib/net/liveUpdate.ts for why a failure here is
// swallowed rather than thrown.
void confirmBundleBooted();

export default app;
