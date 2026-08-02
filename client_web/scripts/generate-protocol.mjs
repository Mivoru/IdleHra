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
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const serverProject = resolve(repoRoot, 'server', 'FolkIdle.Server', 'FolkIdle.Server.csproj');
const prebuiltDll = resolve(repoRoot, 'server', 'FolkIdle.Server', 'bin', 'Debug', 'net8.0', 'FolkIdle.Server.dll');
const outputPath = resolve(here, '..', 'src', 'lib', 'net', 'protocol.generated.ts');

const checkOnly = process.argv.includes('--check');

function dumpSchema() {
  // Prefer the already-built DLL: it is an order of magnitude faster than
  // `dotnet run`, and `dotnet run` would rebuild - which fails outright while
  // a server process is holding the output DLL open, a trap this repo hits
  // often enough to be worth avoiding here.
  if (existsSync(prebuiltDll)) {
    try {
      return execFileSync('dotnet', [prebuiltDll, '--dump-protocol'], {
        encoding: 'utf8',
        maxBuffer: 32 * 1024 * 1024,
      });
    } catch (err) {
      console.warn('prebuilt DLL failed, falling back to dotnet run:', err.message);
    }
  }

  return execFileSync(
    'dotnet',
    ['run', '--project', serverProject, '--no-launch-profile', '--', '--dump-protocol'],
    { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 },
  );
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

  return lines.join('\n');
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
