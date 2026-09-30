// Task 54: the sixteen frames, drawn rather than painted - the owner may
// replace them with art later, and then this table becomes a picture per id.
//
// Keyed by the SERVER's frame id (CosmeticRegistry.FramePools, slugged).
// tests/cosmetics.test.ts reads that registry and fails if a frame has no
// drawing here, or a drawing has no frame.
//
// Ornament grows with rarity: Common is a plain band, Rare adds studs, Epic
// a knotwork outer ring, Legendary a crown of points and a slow glow.
export type FrameStyle = 'band' | 'rope' | 'studs' | 'knot' | 'crown';

export interface FrameDrawing {
  style: FrameStyle;
  color: string;
  accent: string;
  glow?: boolean;
}

export const FRAME_DRAWINGS: Readonly<Record<string, FrameDrawing>> = {
  frame_oak_band: { style: 'band', color: '#8a5a2b', accent: '#5e3b1a' },
  frame_iron_band: { style: 'band', color: '#8c9299', accent: '#50555c' },
  frame_rope_knot: { style: 'rope', color: '#c9a66b', accent: '#7d6238' },
  frame_birch_ring: { style: 'band', color: '#e6dfd0', accent: '#3b3a36' },

  frame_studded_bronze: { style: 'studs', color: '#b0793a', accent: '#f0c27a' },
  frame_silver_rivets: { style: 'studs', color: '#c3c9d1', accent: '#ffffff' },
  frame_amber_ring: { style: 'studs', color: '#d9901a', accent: '#ffd27a' },
  frame_river_stone: { style: 'studs', color: '#4f7f8c', accent: '#a9dbe6' },

  frame_knotwork: { style: 'knot', color: '#7a4fb3', accent: '#c9a9ff' },
  frame_wyrm_coil: { style: 'knot', color: '#2f8f6b', accent: '#9ff0cf' },
  frame_frost_rune: { style: 'knot', color: '#5aa6d6', accent: '#e0f4ff' },
  frame_ember_rune: { style: 'knot', color: '#c2451f', accent: '#ffb27a' },

  frame_sun_crown: { style: 'crown', color: '#d4a017', accent: '#fff1a8', glow: true },
  frame_moon_crown: { style: 'crown', color: '#b9c3d6', accent: '#ffffff', glow: true },
  frame_storm_crown: { style: 'crown', color: '#3f63c9', accent: '#b7c9ff', glow: true },
  frame_worldtree: { style: 'crown', color: '#3f8f3a', accent: '#d5f5a0', glow: true },

  // Task 87: the Boss Ascension frames, one pair per boss - a laurel (knotwork)
  // at step 5 and a crown at step 10, in the boss's colour. Bound: earned on the
  // ladder, never in a chest, never on the market. Ids are
  // BossAscensionRegistry.FrameId(region, step); tests/cosmetics.test.ts reads
  // that registry and fails if one has no drawing.
  frame_ascent_r1_s5: { style: 'knot', color: '#8c96a3', accent: '#e4e9ef' },
  frame_ascent_r1_s10: { style: 'crown', color: '#aab4c2', accent: '#ffffff', glow: true },
  frame_ascent_r2_s5: { style: 'knot', color: '#6b4fa0', accent: '#d2bcff' },
  frame_ascent_r2_s10: { style: 'crown', color: '#8a63d1', accent: '#f0e3ff', glow: true },
  frame_ascent_r3_s5: { style: 'knot', color: '#b8451c', accent: '#ffb98a' },
  frame_ascent_r3_s10: { style: 'crown', color: '#e0611f', accent: '#ffe0a8', glow: true },
  frame_ascent_r4_s5: { style: 'knot', color: '#3d86b8', accent: '#d5f0ff' },
  frame_ascent_r4_s10: { style: 'crown', color: '#5fb0e6', accent: '#ffffff', glow: true },
  frame_ascent_r5_s5: { style: 'knot', color: '#8f1f2e', accent: '#ff9aa6' },
  frame_ascent_r5_s10: { style: 'crown', color: '#c2263c', accent: '#ffd0d6', glow: true },
};
