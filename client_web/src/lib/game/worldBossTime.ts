// Modul: "97h 33m left" IS A SUM, NOT A TIME (task 105). A week-long
// encounter counted in hours made the player divide by 24 to learn it ends on
// Thursday. Days first, then the one unit under it: "4d 1h", "3h 12m",
// "12m 30s". Two units at most - the second is only there to say the first is
// not exact.
export function bossTimeLeft(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const days = Math.floor(s / 86_400);
  const hours = Math.floor((s % 86_400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m ${s % 60}s`;
}
