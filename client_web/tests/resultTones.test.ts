import { describe, it, expect } from 'vitest';
import { shouldPlayErrorTone, COMMAND_RESULT_SILENT_CODES } from '../src/lib/stores/commandResults';

describe('which command results make the error tone', () => {
  it('stays silent for the Forge (owner, 2026-09-28)', () => {
    // Auto-reroll: stop condition met, attempts used up, impossible stat.
    expect(shouldPlayErrorTone([23])).toBe(false);
    expect(shouldPlayErrorTone([24])).toBe(false);
    expect(shouldPlayErrorTone([25])).toBe(false);
    for (const code of COMMAND_RESULT_SILENT_CODES) expect(shouldPlayErrorTone([code])).toBe(false);
  });

  it('stays silent for good news and plays for a real refusal', () => {
    expect(shouldPlayErrorTone([0])).toBe(false);
    expect(shouldPlayErrorTone([35])).toBe(false); // a child was born
    expect(shouldPlayErrorTone([5])).toBe(true); // not enough gold
    expect(shouldPlayErrorTone([23, 5])).toBe(true);
  });
});
