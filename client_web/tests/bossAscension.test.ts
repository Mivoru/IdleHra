// Modul: TASK 87 - the Boss Ascension ladder, client side. The client keeps no
// copy of the ladder (the server sends every sentence), so what is pinned here
// is the little that IS mirrored: the opcode, the result codes and where the
// command's two numbers ride.
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const sent: Record<string, unknown>[] = [];
vi.mock('../src/lib/net/connection', () => ({
  connection: {
    send: (draft: Record<string, unknown>) => {
      sent.push(draft);
    },
    currentPlayerId: 0,
  },
}));

const { startBossAscension } = await import('../src/lib/net/commands');
const { CommandType } = await import('../src/lib/net/protocol.generated');
const { COMMAND_RESULT_MESSAGES, COMMAND_RESULT_OK_CODES, shouldPlayErrorTone } = await import('../src/lib/stores/commandResults');

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, '..', '..', 'server', 'FolkIdle.Server');

describe('boss ascension', () => {
  beforeEach(() => {
    sent.length = 0;
  });

  it('sends the region on TargetId and the step on SecondaryId', () => {
    const outcome = startBossAscension(3, 7);
    expect(outcome.ok).toBe(true);
    expect(sent).toEqual([{ Command: CommandType.StartBossAscension, TargetId: 3, SecondaryId: 7 }]);
  });

  it('does not send what could never be a button', () => {
    for (const [region, step] of [[0, 1], [1, 0], [1.5, 1], [1, NaN]] as const) {
      expect(startBossAscension(region, step).ok).toBe(false);
    }
    expect(sent).toHaveLength(0);
  });

  it('uses the opcode and result codes the server declares', () => {
    const packet = readFileSync(join(server, 'Network', 'ClientCommandPacket.cs'), 'utf8');
    expect(packet.match(/StartBossAscension = (\d+)/)?.[1]).toBe(String(CommandType.StartBossAscension));

    const state = readFileSync(join(server, 'Network', 'StateUpdatePacket.cs'), 'utf8');
    for (const name of ['AscensionBossNotDefeated', 'AscensionStepLocked', 'AscensionStepCleared', 'AscensionTooSlow']) {
      const code = Number(state.match(new RegExp(`${name} = (\\d+)`))?.[1]);
      expect(code, name).toBeGreaterThan(45);
      expect(COMMAND_RESULT_MESSAGES[code], name).toMatch(/\w{4,}/);
    }
  });

  it('says a cleared step is good news, a slow kill is quiet, and a refusal is a refusal', () => {
    expect(COMMAND_RESULT_OK_CODES.has(48)).toBe(true);
    expect(shouldPlayErrorTone([48])).toBe(false);
    expect(shouldPlayErrorTone([49])).toBe(false);
    expect(shouldPlayErrorTone([46])).toBe(true);
    expect(shouldPlayErrorTone([47])).toBe(true);
  });
});
