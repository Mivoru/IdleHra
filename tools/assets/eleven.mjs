#!/usr/bin/env node
// Generates game assets (sound effects, images, video) with the ElevenLabs API
// from tools/assets/manifest.json, into tools/assets/staging/ for review.
//
// Modul: STAGING, NEVER STRAIGHT INTO THE GAME. Every asset is looked at (or
// listened to) by a person before it is moved into client_web/public or the
// sprite pipeline. A generated asset nobody checked is how a chest with a
// sixth finger ships.
//
// Modul: THE KEY NEVER TOUCHES THE REPO. It is read from the file named by
// ELEVENLABS_KEY_FILE (the first `sk_...` token in it) or from
// ELEVENLABS_API_KEY, and it is never printed. The script refuses to run
// without one.
//
// Modul: COST IS MEASURED, NOT GUESSED. The subscription's character count is
// read before and after each asset and the difference is recorded in
// generated.json, so the full batch can be priced from a few test assets
// (docs/design/LOOT_DUNGEON_VISUALS.md section 11.5).
//
// Usage:
//   node tools/assets/eleven.mjs --list
//   node tools/assets/eleven.mjs --only sfx_achievement_chime
//   node tools/assets/eleven.mjs --only a,b,c [--dry]
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const API = 'https://api.elevenlabs.io';
const STAGING = join(HERE, 'staging');
const RECORD = join(HERE, 'generated.json');

function readKey() {
  if (process.env.ELEVENLABS_API_KEY) return process.env.ELEVENLABS_API_KEY.trim();
  const file = process.env.ELEVENLABS_KEY_FILE;
  if (file && existsSync(file)) {
    const m = /sk_[A-Za-z0-9]+/.exec(readFileSync(file, 'utf8'));
    if (m) return m[0];
  }
  return null;
}

const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const value = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

const manifest = JSON.parse(readFileSync(join(HERE, 'manifest.json'), 'utf8'));
if (flag('--list')) {
  for (const a of manifest.assets) console.log(`${a.id.padEnd(34)} ${a.kind.padEnd(6)} ${a.prompt.slice(0, 70)}`);
  process.exit(0);
}

const only = value('--only')?.split(',').map((s) => s.trim()).filter(Boolean);
if (!only?.length) {
  console.error('Name the assets to make: --only id1,id2 (see --list). The whole manifest is never run by default.');
  process.exit(2);
}
const chosen = manifest.assets.filter((a) => only.includes(a.id));
const unknown = only.filter((id) => !chosen.some((a) => a.id === id));
if (unknown.length) {
  console.error(`Not in the manifest: ${unknown.join(', ')}`);
  process.exit(2);
}

const dry = flag('--dry');
const key = dry ? null : readKey();
if (!dry && !key) {
  console.error('No API key: set ELEVENLABS_KEY_FILE to the file holding it, or ELEVENLABS_API_KEY.');
  process.exit(2);
}

async function api(path, init = {}) {
  const res = await fetch(`${API}${path}`, {
    ...init,
    headers: { 'xi-api-key': key, ...(init.body && typeof init.body === 'string' ? { 'Content-Type': 'application/json' } : {}), ...(init.headers ?? {}) },
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(`${init.method ?? 'GET'} ${path} -> ${res.status} ${text.slice(0, 300)}`);
  }
  return res;
}

async function usedCharacters() {
  const res = await api('/v1/user/subscription');
  const sub = await res.json();
  return { used: sub.character_count, limit: sub.character_limit, tier: sub.tier };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function referencesOf(asset) {
  return (asset.references ?? []).map((p) => {
    const abs = resolve(HERE, '..', '..', p);
    const ext = abs.toLowerCase().split('.').pop();
    const mime = ext === 'png' ? 'image/png' : ext === 'webp' ? 'image/webp' : 'image/jpeg';
    return { type: 'inline_base64', content_base64: readFileSync(abs).toString('base64'), mime_type: mime };
  });
}

async function makeSfx(asset) {
  const body = {
    text: asset.prompt,
    ...(asset.duration ? { duration_seconds: asset.duration } : {}),
    prompt_influence: asset.promptInfluence ?? 0.5,
    ...(asset.loop ? { loop: true } : {}),
  };
  const res = await api(`/v1/sound-generation?output_format=${asset.format ?? 'mp3_44100_128'}`, { method: 'POST', body: JSON.stringify(body) });
  return { bytes: Buffer.from(await res.arrayBuffer()), ext: 'mp3', generationId: null };
}

// Image and video generations are asynchronous: create, poll, then download
// the signed URL at once - it expires about an hour after the answer.
async function makeFlow(asset, kind) {
  const body = {
    model_id: asset.model,
    prompt: asset.prompt,
    ...(asset.aspectRatio ? { aspect_ratio: asset.aspectRatio } : {}),
    ...(asset.resolution ? { resolution: asset.resolution } : {}),
    ...(asset.quality ? { quality: asset.quality } : {}),
    ...(asset.background ? { background: asset.background } : {}),
    ...(asset.durationSecs ? { duration_secs: asset.durationSecs } : {}),
    ...(asset.references ? { images: referencesOf(asset) } : {}),
    ...(asset.seed !== undefined ? { seed: asset.seed } : {}),
  };
  const created = await (await api(`/v1/flows/${kind}`, { method: 'POST', body: JSON.stringify(body) })).json();
  const interval = kind === 'video' ? 10_000 : 2_000;
  const deadline = Date.now() + (kind === 'video' ? 15 : 4) * 60_000;
  for (;;) {
    if (Date.now() > deadline) throw new Error(`${asset.id}: still not finished; generation ${created.id}`);
    await sleep(interval);
    const gen = await (await api(`/v1/flows/${kind}/${created.id}`)).json();
    if (gen.status === 'failed') throw new Error(`${asset.id}: failed - ${gen.failure_reason ?? ''} ${gen.error_message ?? ''}`);
    if (gen.status === 'completed') {
      const url = gen.content_url ?? gen.output?.content_url;
      if (!url) throw new Error(`${asset.id}: completed without a content_url: ${JSON.stringify(gen).slice(0, 300)}`);
      const file = await fetch(url);
      if (!file.ok) throw new Error(`${asset.id}: download ${file.status}`);
      const mime = gen.content_mime_type ?? file.headers.get('content-type') ?? '';
      const ext = mime.includes('png') ? 'png' : mime.includes('webp') ? 'webp' : mime.includes('jpeg') ? 'jpg' : mime.includes('mp4') ? 'mp4' : kind === 'video' ? 'mp4' : 'png';
      return { bytes: Buffer.from(await file.arrayBuffer()), ext, generationId: created.id };
    }
  }
}

mkdirSync(STAGING, { recursive: true });
const record = existsSync(RECORD) ? JSON.parse(readFileSync(RECORD, 'utf8')) : { assets: [] };

for (const asset of chosen) {
  if (dry) {
    console.log(`dry  ${asset.id} (${asset.kind}, ${asset.model ?? 'default model'}): ${asset.prompt}`);
    continue;
  }
  const before = await usedCharacters();
  const started = Date.now();
  const out = asset.kind === 'sfx' ? await makeSfx(asset) : await makeFlow(asset, asset.kind);
  const after = await usedCharacters();
  const file = join(STAGING, `${asset.id}.${out.ext}`);
  writeFileSync(file, out.bytes);
  const entry = {
    id: asset.id,
    kind: asset.kind,
    model: asset.model ?? null,
    prompt: asset.prompt,
    generationId: out.generationId,
    file: `tools/assets/staging/${asset.id}.${out.ext}`,
    bytes: out.bytes.length,
    credits: after.used - before.used,
    seconds: Math.round((Date.now() - started) / 1000),
    at: new Date().toISOString(),
  };
  record.assets.push(entry);
  writeFileSync(RECORD, JSON.stringify(record, null, 2) + '\n');
  console.log(`made ${asset.id} -> ${entry.file} (${entry.bytes} bytes, ${entry.credits} credits, ${entry.seconds} s; ${after.limit - after.used} left on ${after.tier})`);
}
