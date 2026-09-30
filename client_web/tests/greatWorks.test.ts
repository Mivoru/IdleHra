// Modul: TASK 84 - the Great Works, client side. The client keeps no copy of the
// monuments (names, costs, bonuses and both ceilings arrive with the answer), so
// what is pinned here is the little that IS mirrored: the opcode, the result
// codes, where the command's three numbers ride, and the small arithmetic the
// panel does on the server's figures.
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

const { depositGreatWork } = await import('../src/lib/net/commands');
const { CommandType } = await import('../src/lib/net/protocol.generated');
const { COMMAND_RESULT_MESSAGES, COMMAND_RESULT_OK_CODES, shouldPlayErrorTone } = await import('../src/lib/stores/commandResults');
const { monumentFraction, stageFraction, isComplete } = await import('../src/lib/net/greatWorks');
import type { GreatWork } from '../src/lib/net/greatWorks';

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, '..', '..', 'server', 'FolkIdle.Server');

function work(stage: number, progress: number): GreatWork {
  const costs = [50_000, 150_000, 400_000, 1_000_000, 2_000_000];
  return {
    Region: 1,
    Name: 'Test Cairn',
    Stage: stage,
    Progress: progress,
    NextCost: stage >= 5 ? 0 : costs[stage],
    LogItem: 'birch_log',
    OreItem: 'copper_ore',
    HeldLog: 0,
    HeldOre: 0,
    BonusPerStage: '+1% gathering yield',
    Stages: costs.map((cost, i) => ({ Stage: i + 1, Name: `s${i + 1}`, Cost: cost, Built: i < stage })),
    CompletionReward: 'Test Cairn Frame',
    FrameId: 'frame_monument_r1',
    FrameOwned: stage >= 5,
  };
}

describe('great works', () => {
  beforeEach(() => {
    sent.length = 0;
  });

  it('sends the region, the material and the quantity on the fields the server reads', () => {
    expect(depositGreatWork(3, 1, 500).ok).toBe(true);
    expect(sent).toEqual([{ Command: CommandType.DepositGreatWork, TargetId: 3, SecondaryId: 1, DepositQuantity: 500 }]);
  });

  it('a quantity of 0 means "everything I hold"', () => {
    depositGreatWork(1, 0);
    expect(sent[0]).toMatchObject({ DepositQuantity: 0 });
  });

  it('does not send what could never be a button', () => {
    for (const [region, quantity] of [[0, 0], [6, 0], [1.5, 0], [1, -1], [1, 2.5]] as const) {
      expect(depositGreatWork(region, 0, quantity).ok).toBe(false);
    }
    expect(sent).toHaveLength(0);
  });

  it('uses the opcode and result codes the server declares, and words every one', () => {
    const packet = readFileSync(join(server, 'Network', 'ClientCommandPacket.cs'), 'utf8');
    expect(packet.match(/DepositGreatWork = (\d+)/)?.[1]).toBe(String(CommandType.DepositGreatWork));

    const state = readFileSync(join(server, 'Network', 'StateUpdatePacket.cs'), 'utf8');
    for (const name of ['GreatWorkDeposited', 'GreatWorkStageBuilt', 'GreatWorkComplete']) {
      const code = Number(state.match(new RegExp(`${name} = (\\d+)`))?.[1]);
      expect(Number.isInteger(code), name).toBe(true);
      expect(COMMAND_RESULT_MESSAGES[code], name).toBeTruthy();
      // Good news, never in the failure colour and never with the error tone.
      expect(COMMAND_RESULT_OK_CODES.has(code), name).toBe(true);
      expect(shouldPlayErrorTone([code]), name).toBe(false);
    }
  });

  it('measures a monument and a stage from the server figures alone', () => {
    expect(stageFraction(work(0, 0))).toBe(0);
    expect(stageFraction(work(0, 25_000))).toBe(0.5);
    expect(stageFraction(work(5, 0))).toBe(1);
    expect(isComplete(work(4, 0))).toBe(false);
    expect(isComplete(work(5, 0))).toBe(true);

    // Whole monument: 3,600,000 in all; stage 1 built plus half of stage 2.
    expect(monumentFraction(work(1, 75_000))).toBeCloseTo((50_000 + 75_000) / 3_600_000, 10);
    expect(monumentFraction(work(5, 0))).toBe(1);
  });
});
