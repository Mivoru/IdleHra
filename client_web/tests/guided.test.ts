import { describe, expect, it } from 'vitest';
import type { StateUpdate } from '../src/lib/net/protocol.generated';
import { guidedStage } from '../src/lib/stores/guided';

function snapshot(overrides: Partial<StateUpdate> = {}): StateUpdate {
  return {
    CurrentLevel: 1,
    EquippedWeaponId: 0,
    Food1_Count: 0,
    Food2_Count: 0,
    Food3_Count: 0,
    ...overrides,
  } as unknown as StateUpdate;
}

describe('the guided first minute', () => {
  it('guides the larder first, then the weapon', () => {
    expect(guidedStage(snapshot(), false)?.screen).toBe('larder');
    expect(guidedStage(snapshot({ Food1_Count: 10 }), false)?.screen).toBe('character');
  });

  it('lets go once both are done - the fight is advice, not a fence', () => {
    expect(guidedStage(snapshot({ Food1_Count: 10, EquippedWeaponId: 7 }), false)).toBeNull();
  });

  it('never guides a player who skipped the tutorial', () => {
    expect(guidedStage(snapshot(), true)).toBeNull();
  });

  it('lights "wear" before "open the slot" once both are on the page', () => {
    const stage = guidedStage(snapshot({ Food1_Count: 10 }), false)!;
    expect(stage.targets[0]).toBe('wear-first');
    expect(stage.captions.length).toBe(stage.targets.length);
  });
});
