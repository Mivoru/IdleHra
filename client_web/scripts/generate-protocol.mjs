// Generates src/lib/net/protocol.generated.ts from the SERVER'S OWN
// description of the wire, obtained by running the server with
// --dump-protocol (see Program.cs and PacketJsonCodec.ExportSchemaJson).
//
// This exists because of the single most important rule in the port plan:
// every feature must exist in exactly one place per layer. A hand-written
// TypeScript mirror of StateUpdatePacket's 159 fields would be the largest
// two-sources-of-truth surface this project has ever had, and this codebase's
// dominant bug class is exactly that - two copies of one truth drifting apart.
//
// The generated file is committed, so a fresh checkout builds without the
// .NET SDK. `--check` re-generates and diffs instead of writing, which is what
// CI should run: it fails if someone changed a packet struct and did not
// regenerate.

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const serverProject = resolve(repoRoot, 'server', 'FolkIdle.Server', 'FolkIdle.Server.csproj');
const outputPath = resolve(here, '..', 'src', 'lib', 'net', 'protocol.generated.ts');

// Whichever of Debug/Release was built most recently.
//
// This used to be Debug, unconditionally, and that is a quiet way to generate
// a WRONG protocol rather than a failure: add a packet field, build Release
// (because a server process is holding the Debug output open, which is the
// normal state while anything is running locally), regenerate - and the stale
// Debug DLL reports the old layout. The file is written, the build is green,
// and the client then reads every field after the new one at the wrong offset.
//
// The whole point of generating this from the server is that the two cannot
// disagree; picking an arbitrary build configuration reintroduced exactly the
// disagreement it removes.
function newestPrebuiltDll() {
  const candidates = ['Debug', 'Release']
    .map((configuration) =>
      resolve(repoRoot, 'server', 'FolkIdle.Server', 'bin', configuration, 'net8.0', 'FolkIdle.Server.dll'),
    )
    .filter((path) => existsSync(path))
    .map((path) => ({ path, mtimeMs: statSync(path).mtimeMs }))
    .sort((a, b) => b.mtimeMs - a.mtimeMs);

  return candidates.length > 0 ? candidates[0].path : null;
}

const checkOnly = process.argv.includes('--check');

function dumpSchema() {
  // Prefer the already-built DLL: it is an order of magnitude faster than
  // `dotnet run`, and `dotnet run` would rebuild - which fails outright while
  // a server process is holding the output DLL open, a trap this repo hits
  // often enough to be worth avoiding here.
  const prebuiltDll = newestPrebuiltDll();
  if (prebuiltDll) {
    try {
      console.log(`reading protocol from ${prebuiltDll}`);
      return execFileSync('dotnet', [prebuiltDll, '--dump-protocol'], {
        encoding: 'utf8',
        maxBuffer: 32 * 1024 * 1024,
      });
    } catch (err) {
      console.warn('prebuilt DLL failed, falling back to dotnet run:', err.message);
    }
  }

  // Modul: `dotnet run` BUILDS first and prints its build diagnostics to
  // stdout, ahead of the JSON. Locally that never shows, because a prebuilt
  // DLL is nearly always present and the branch above wins - so this fallback
  // sat broken until CI, which starts from a clean checkout with no bin/, ran
  // it for the first time and died on `Unexpected token 'C'` (a compiler
  // warning path). The schema is taken from the first brace onward rather than
  // from the first byte.
  const output = execFileSync(
    'dotnet',
    ['run', '--project', serverProject, '--no-launch-profile', '--', '--dump-protocol'],
    { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 },
  );
  const firstBrace = output.indexOf('{');
  return firstBrace > 0 ? output.slice(firstBrace) : output;
}

// Kind -> TypeScript type. Every integer field becomes `number`, including the
// 64-bit ones: JSON.parse already produces a double, so pretending they are
// bigint would be a lie about what actually arrives. Values above 2^53 would
// be lossy, but nothing on this wire carries one (the largest are gold and
// epoch counters, both far below it) - and the alternative, a JSON parser that
// reviver-hooks every field, costs more than it buys at 10 Hz.
const KIND_TO_TS = {
  U8: 'number',
  I8: 'number',
  U16: 'number',
  I16: 'number',
  U32: 'number',
  I32: 'number',
  U64: 'number',
  I64: 'number',
  F32: 'number',
  F64: 'number',
  // Non-finite floats travel as "NaN"/"Infinity"/"-Infinity" strings, so the
  // type has to admit both. See PacketJsonCodec's float handling.
  GuidValue: 'string',
  // `fixed byte X[N]` is base64 of the full fixed capacity.
  FixedBytes: 'string',
};

const FLOAT_KINDS = new Set(['F32', 'F64']);

function tsTypeFor(kind) {
  if (FLOAT_KINDS.has(kind)) return 'number | string';
  const mapped = KIND_TO_TS[kind];
  if (!mapped) throw new Error(`no TypeScript mapping for wire kind '${kind}'`);
  return mapped;
}

function interfaceNameFor(packetName) {
  // AuthHandshakePacket -> AuthHandshake, matching the discriminator.
  return packetName.replace(/Packet$/, '');
}

function generate(schema) {
  const lines = [];
  lines.push('// GENERATED FILE - DO NOT EDIT BY HAND.');
  lines.push('//');
  lines.push('// Produced by client_web/scripts/generate-protocol.mjs from the server\'s own');
  lines.push('// `--dump-protocol` output, which comes from the same reflected field plan');
  lines.push('// PacketJsonCodec uses to encode. These types therefore cannot describe a');
  lines.push('// packet the server does not actually send.');
  lines.push('//');
  lines.push('// Regenerate with:  npm run generate:protocol');
  lines.push('// Verify in CI with: node scripts/generate-protocol.mjs --check');
  lines.push('');
  lines.push(`export const TYPE_PROPERTY = ${JSON.stringify(schema.typeProperty)} as const;`);
  lines.push(`export const MODE_PROPERTY = ${JSON.stringify(schema.modeProperty)} as const;`);
  lines.push('');

  // Discriminators
  lines.push('/** The `type` discriminator carried by every packet on this wire. */');
  lines.push('export const PacketType = {');
  for (const packet of schema.packets) {
    lines.push(`  ${interfaceNameFor(packet.name)}: ${JSON.stringify(packet.discriminator)},`);
  }
  lines.push('} as const;');
  lines.push('');
  lines.push('export type PacketTypeName = (typeof PacketType)[keyof typeof PacketType];');
  lines.push('');

  // Interfaces
  for (const packet of schema.packets) {
    const name = interfaceNameFor(packet.name);
    lines.push(`/** ${packet.name} - ${packet.byteSize} bytes on the binary wire. */`);
    lines.push(`export interface ${name} {`);
    lines.push(`  readonly ${schema.typeProperty}: typeof PacketType.${name};`);
    for (const field of packet.fields) {
      lines.push(`  ${field.name}: ${tsTypeFor(field.kind)};`);
    }
    lines.push('}');
    lines.push('');
  }

  // Outbound packets are built field-by-field by the client, which has no
  // reason to spell out all 54 ClientCommandPacket fields per command.
  lines.push('/** Fields a client fills in; everything omitted defaults to zero server-side. */');
  for (const packet of schema.packets) {
    const name = interfaceNameFor(packet.name);
    lines.push(`export type ${name}Draft = Partial<Omit<${name}, '${schema.typeProperty}'>>;`);
  }
  lines.push('');

  // Command opcodes
  lines.push('/** The command opcodes. Numbering has deliberate gaps - see CommandType in C#. */');
  lines.push('export const CommandType = {');
  for (const command of schema.commandTypes) {
    lines.push(`  ${command.name}: ${command.value},`);
  }
  lines.push('} as const;');
  lines.push('');
  lines.push('export type CommandTypeName = keyof typeof CommandType;');
  lines.push('');

  // Byte sizes, useful for asserting a binary session in tests.
  lines.push('/** Binary wire sizes, kept for tests that assert the binary path is untouched. */');
  lines.push('export const PACKET_BYTE_SIZE = {');
  for (const packet of schema.packets) {
    lines.push(`  ${interfaceNameFor(packet.name)}: ${packet.byteSize},`);
  }
  lines.push('} as const;');
  lines.push('');

  // Known-good anti-cheat answers, computed by the SERVER'S implementation.
  // The TypeScript mirror is tested against these, so the two can never
  // silently disagree - and disagreeing here gets a real player's account
  // quarantined as a cheater, which is not a failure mode worth discovering
  // in production.
  lines.push('/** Server-computed challenge answers. See tests/antiCheat.test.ts. */');
  lines.push('export const CHALLENGE_VECTORS: readonly {');
  lines.push('  seed: number;');
  lines.push('  playerId: number;');
  lines.push('  logicEpochCounter: number;');
  lines.push('  expectedHash: number;');
  lines.push('}[] = [');
  for (const vector of schema.challengeVectors ?? []) {
    lines.push(
      `  { seed: ${vector.seed}, playerId: ${vector.playerId}, ` +
      `logicEpochCounter: ${vector.logicEpochCounter}, expectedHash: ${vector.expectedHash} },`,
    );
  }
  lines.push('];');
  lines.push('');

  // The same idea for the account-erasure interlock. Its hash multiplies in
  // wrapping uint32, which a plain JavaScript `*` gets right for small inputs
  // and silently wrong for large ones, and the command it gates is the one
  // that cannot be undone.
  lines.push('/** Server-computed GDPR confirmation hashes. See tests/antiCheat.test.ts. */');
  lines.push('export const GDPR_CONFIRMATION_VECTORS: readonly {');
  lines.push('  playerId: number;');
  lines.push('  logicEpochCounter: number;');
  lines.push('  expectedHash: number;');
  lines.push('}[] = [');
  for (const vector of schema.gdprConfirmationVectors ?? []) {
    lines.push(
      `  { playerId: ${vector.playerId}, ` +
      `logicEpochCounter: ${vector.logicEpochCounter}, expectedHash: ${vector.expectedHash} },`,
    );
  }
  lines.push('];');
  lines.push('');

  return lines.join('\n');
}

// Modul: THE NO-.NET PATH, for a machine that can build the app but cannot run
// the server that defines the wire.
//
// An iOS build happens on a Mac, and a Mac with this repository checked out
// does not necessarily have a working .NET SDK - so `npm run build`, which
// shells out to --dump-protocol, dies there with a dotnet error in the middle
// of what otherwise looks like a Capacitor problem. `npm run sync:web` runs
// THIS instead.
//
// What it checks: the committed generated file is present, is structurally
// generator output rather than a stub, and whether the working copy has been
// modified since the commit.
//
// What it deliberately does NOT check: drift. Detecting drift means asking the
// server what its structs look like, and asking the server is the exact thing
// this machine cannot do. --check stays the only drift gate and CI stays the
// place it is guaranteed to run. This mode buys a build on a machine without
// the toolchain; it does not buy a weaker contract.
//
// Fails OPEN on anything git-related, for the same reason this repo's hooks do:
// a guard with no workaround is worse than the trap it guards.
if (process.argv.includes('--assume-committed')) {
  if (!existsSync(outputPath)) {
    console.error(`${outputPath} does not exist.`);
    console.error('It is a COMMITTED generated file, so a missing one is a broken checkout,');
    console.error('not a skipped build step. Restore it from git, or run');
    console.error('`npm run generate:protocol` on a machine with the .NET SDK.');
    process.exit(1);
  }

  const committed = readFileSync(outputPath, 'utf8');
  // A truncated or hand-written stub is the failure this catches: the file
  // exists, the build succeeds, and the client then reads fields at offsets
  // the server never wrote. Two markers the generator always emits, neither of
  // which a partial write leaves behind.
  if (
    !committed.includes('export const CommandType') ||
    !committed.includes('export const PACKET_BYTE_SIZE')
  ) {
    console.error(`${outputPath} exists but does not look like generator output.`);
    console.error('Regenerate it with `npm run generate:protocol` on a machine with .NET.');
    process.exit(1);
  }

  let locallyModified = null;
  try {
    locallyModified =
      execFileSync('git', ['status', '--porcelain', '--', outputPath], {
        encoding: 'utf8',
        cwd: repoRoot,
      }).trim().length > 0;
  } catch {
    // No git, or not a checkout (a source tarball, a CI cache). Not a reason
    // to refuse a build.
  }

  if (locallyModified) {
    console.warn(`WARNING: ${outputPath} differs from the commit.`);
    console.warn('  That is correct only if YOU regenerated it against the server this');
    console.warn('  build will talk to. It is wrong if the file was hand-edited.');
  }

  console.log('protocol.generated.ts present and assumed current (no server was contacted).');
  console.log('  Drift is caught by `node scripts/generate-protocol.mjs --check`, which CI runs.');
  process.exit(0);
}

const raw = dumpSchema();
let schema;
try {
  schema = JSON.parse(raw);
} catch (err) {
  console.error('--dump-protocol did not produce parseable JSON. First 200 chars:');
  console.error(raw.slice(0, 200));
  throw err;
}

const generated = generate(schema);

if (checkOnly) {
  const existing = existsSync(outputPath) ? readFileSync(outputPath, 'utf8') : '';
  if (existing !== generated) {
    console.error(
      `${outputPath} is out of date with the server's packet structs.\n` +
      'Run `npm run generate:protocol` and commit the result.',
    );
    process.exit(1);
  }
  console.log('protocol.generated.ts is up to date.');
} else {
  writeFileSync(outputPath, generated, 'utf8');
  const fieldTotal = schema.packets.reduce((n, p) => n + p.fields.length, 0);
  console.log(
    `wrote ${outputPath}\n  ${schema.packets.length} packets, ${fieldTotal} fields, ` +
    `${schema.commandTypes.length} opcodes`,
  );
}
