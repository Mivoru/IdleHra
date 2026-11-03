/**
 * The four guild ranks (owner, 2026-11-03), by the number the server stores in
 * GuildMembers.Role - GuildManagementEngine.RoleMember..RoleChieftain. Only
 * the NAMES live here; what each rank may do is the server's, sent per member
 * on the roster (CanPromote, CanDemote, CanKick, CanTransfer).
 */
export const RANK_MEMBER = 0;
export const RANK_WARDEN = 1;
export const RANK_ELDER = 2;
export const RANK_CHIEFTAIN = 3;

export const RANK_NAMES: Record<number, string> = {
  [RANK_MEMBER]: 'Member',
  [RANK_WARDEN]: 'Warden',
  [RANK_ELDER]: 'Elder',
  [RANK_CHIEFTAIN]: 'Chieftain',
};

export function rankName(role: number | undefined | null): string {
  return RANK_NAMES[role ?? 0] ?? 'Member';
}
