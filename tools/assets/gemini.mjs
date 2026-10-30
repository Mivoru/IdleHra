#!/usr/bin/env node
// Generates images with the Gemini API from tools/assets/gemini.json, into
// tools/assets/staging/ for review. The sibling of eleven.mjs.
//
// Modul: STAGING, NEVER STRAIGHT INTO THE GAME - same rule as eleven.mjs.
// Gemini cannot paint a transparent background, so object assets are painted
// on a flat key colour and keyout.py removes it; a person looks at the result
// on a dark and a light floor before anything moves into client_web/public.
//
// Modul: WHY GEMINI FOR MATCHING SETS. It edits a reference image and keeps
// the object, so "the same chest, lid open" is made FROM the closed chest.
// Generated separately (the ElevenLabs batch), a closed and an open legendary
// chest came back as two different chests.
//
// Modul: THE KEY NEVER TOUCHES THE REPO. Read from GEMINI_KEY_FILE (default
// %USERPROFILE%/skola2025/geminiAPI/apiGEM.txt), else GEMINI_API_KEY; never
// printed.
//
// Modul: COST IS MEASURED. Each answer's usageMetadata (tokens) is recorded in
// generated.json; the dollar figure beside it uses the per-image prices in
// PRICE below, which are third-party estimates - check the billing page.
//
// Usage:
//   node tools/assets/gemini.mjs --list
//   node tools/assets/gemini.mjs --only id1,id2 [--variants 2] [--dry]
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..');
const API = 'https://generativelanguage.googleapis.com/v1beta';
const STAGING = join(HERE, 'staging');
const RECORD = join(HERE, 'generated.json');
const PRICE = { 'gemini-2.5-flash-image': 0.039, 'gemini-3.1-flash-image': 0.067, 'gemini-3-pro-image': 0.134 };

// Modul: THE FILE WINS OVER THE ENVIRONMENT. This machine has an old
// free-tier GEMINI_API_KEY set globally; read first, it made every image
// call answer 429 "free_tier ... limit: 0" while the paid key sat in the file.
function readKey() {
  const file = process.env.GEMINI_KEY_FILE ?? join(homedir(), 'skola2025', 'geminiAPI', 'apiGEM.txt');
  if (existsSync(file)) return readFileSync(file, 'utf8').trim();
  return process.env.GEMINI_API_KEY?.trim() || null;
}

const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const value = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

const manifest = JSON.parse(readFileSync(join(HERE, 'gemini.json'), 'utf8'));
if (flag('--list')) {
  for (const a of manifest.assets) console.log(`${a.id.padEnd(30)} ${a.model.padEnd(24)} ${a.prompt.slice(0, 60)}`);
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
  console.error(`Not in gemini.json: ${unknown.join(', ')}`);
  process.exit(2);
}
const variants = Math.max(1, Math.min(4, Number(value('--variants') ?? 1)));

const dry = flag('--dry');
const key = dry ? null : readKey();
if (!dry && !key) {
  console.error('No API key: set GEMINI_KEY_FILE to the file holding it, or GEMINI_API_KEY.');
  process.exit(2);
}

function promptOf(asset) {
  const tail = asset.key ? ` ${manifest.keyLine.replace('{key}', asset.key)}` : '';
  return `${asset.prompt}${tail} ${manifest.style}`;
}

function referencesOf(asset) {
  return (asset.references ?? []).map((p) => {
    const abs = resolve(REPO, p);
    const ext = abs.toLowerCase().split('.').pop();
    const mime = ext === 'png' ? 'image/png' : ext === 'webp' ? 'image/webp' : 'image/jpeg';
    return { inlineData: { mimeType: mime, data: readFileSync(abs).toString('base64') } };
  });
}

async function generate(asset) {
  const body = {
    contents: [{ role: 'user', parts: [...referencesOf(asset), { text: promptOf(asset) }] }],
    generationConfig: {
      responseModalities: ['IMAGE'],
      imageConfig: { aspectRatio: asset.aspectRatio ?? '1:1', ...(asset.imageSize ? { imageSize: asset.imageSize } : {}) },
    },
  };
  const res = await fetch(`${API}/models/${asset.model}:generateContent`, {
    method: 'POST',
    headers: { 'x-goog-api-key': key, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const json = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(`${asset.id}: ${res.status} ${JSON.stringify(json).slice(0, 400)}`);
  const part = json.candidates?.[0]?.content?.parts?.find((p) => p.inlineData);
  if (!part) throw new Error(`${asset.id}: no image in the answer: ${JSON.stringify(json).slice(0, 400)}`);
  const mime = part.inlineData.mimeType ?? 'image/png';
  return {
    bytes: Buffer.from(part.inlineData.data, 'base64'),
    ext: mime.includes('jpeg') ? 'jpg' : mime.includes('webp') ? 'webp' : 'png',
    usage: json.usageMetadata ?? null,
  };
}

mkdirSync(STAGING, { recursive: true });
const record = existsSync(RECORD) ? JSON.parse(readFileSync(RECORD, 'utf8')) : { assets: [] };
let spent = 0;

for (const asset of chosen) {
  for (let v = 1; v <= variants; v++) {
    const name = variants > 1 ? `${asset.id}_v${v}` : asset.id;
    if (dry) {
      console.log(`dry  ${name} (${asset.model}, refs ${(asset.references ?? []).length}): ${promptOf(asset)}`);
      continue;
    }
    const started = Date.now();
    const out = await generate(asset);
    const file = join(STAGING, `${name}.${out.ext}`);
    writeFileSync(file, out.bytes);
    const usd = PRICE[asset.model.replace(/-preview$/, '')] ?? null;
    spent += usd ?? 0;
    record.assets.push({
      id: name,
      kind: 'image',
      provider: 'gemini',
      model: asset.model,
      prompt: promptOf(asset),
      file: `tools/assets/staging/${name}.${out.ext}`,
      bytes: out.bytes.length,
      usage: out.usage,
      estimatedUsd: usd,
      seconds: Math.round((Date.now() - started) / 1000),
      at: new Date().toISOString(),
    });
    writeFileSync(RECORD, JSON.stringify(record, null, 2) + '\n');
    console.log(`made ${name} -> tools/assets/staging/${name}.${out.ext} (${out.bytes.length} bytes, ~$${usd ?? '?'}, ${out.usage?.candidatesTokenCount ?? '?'} out tokens)`);
  }
}
if (!dry) console.log(`estimated spend this run: ~$${spent.toFixed(3)}`);
