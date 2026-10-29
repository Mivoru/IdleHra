// Modul: the skill callouts on floating damage (docs/design/SKILL_TREE_ART.md
// section 2). The server names the skill that fired on each combat event
// (ResponseCombatEventPacket.Proc, one of its SkillProc constants); this file
// says how that skill LOOKS and SOUNDS. The ids are the server's, copied, and
// serverMirrors.test.ts compares the two lists element by element - an id that
// crosses the wire as a number is a two-sources-of-truth surface.
import type { ClipName } from './audio';

export const SKILL_PROC = {
  None: 0,
  CrushingBlow: 1,
  Echo: 2,
} as const;

export interface SkillCallout {
  /** The word over the number. */
  label: string;
  /** A CSS class on the floating number: colour and animation. */
  tone: string;
  clip: ClipName;
}

export const SKILL_CALLOUTS: Readonly<Record<number, SkillCallout>> = {
  [SKILL_PROC.CrushingBlow]: { label: 'CRUSH!', tone: 'crush', clip: 'skillCrush' },
  [SKILL_PROC.Echo]: { label: 'ECHO', tone: 'echo', clip: 'skillEcho' },
};

export function calloutFor(proc: number): SkillCallout | null {
  return SKILL_CALLOUTS[proc] ?? null;
}
