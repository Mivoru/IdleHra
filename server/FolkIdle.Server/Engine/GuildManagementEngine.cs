using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    // Modul: the membership-mutation engine the guild subsystem previously
    // lacked entirely - every other guild engine (contribution, logistics,
    // raid, war) only ever READS an existing PlayerRecord.GuildId; nothing
    // in the codebase wrote one outside DB seeding. All four operations
    // follow the codebase-standard Serializable + FOR UPDATE transaction
    // pattern, write BOTH membership representations (the GuildMembers row
    // and PlayerRecord.GuildId) atomically in the same transaction, and on
    // commit enqueue a GuildMembershipChangeNotification so the tick thread
    // updates its _guildMembersIndex and live TickStatePayload.GuildId and
    // pushes a ReloadState packet to the affected player (see
    // SimulationEngine's membership-change drain). The engine itself never
    // touches SimulationEngine state directly - the index and payload are
    // tick-thread-owned, and the queue is the only legal crossing point.
    public class GuildManagementEngine
    {
        private readonly RetryingDbContextOptions _retryingDbOptions;
        private readonly PlayerSessionRegistry _playerRegistry;

        public GuildManagementEngine(RetryingDbContextOptions retryingDbOptions, PlayerSessionRegistry playerRegistry)
        {
            _retryingDbOptions = retryingDbOptions;
            _playerRegistry = playerRegistry;
        }

        public const int RoleMember = 0;
        public const int RoleLeader = 1;

        // Creates a new guild with the caller as its sole member and Leader.
        // Returns the new guild's id, or 0 if rejected (caller already in a
        // guild, empty/overlong name, or duplicate guild name).
        public async Task<long> CreateGuildAsync(long playerId, string guildName)
        {
            if (string.IsNullOrWhiteSpace(guildName) || guildName.Length > 100)
            {
                return 0;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                long createdGuildId = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var profile = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (profile == null || profile.GuildId > 0)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    // Serializable isolation turns this check-then-insert
                    // into a genuine uniqueness guard - a concurrent create
                    // with the same name serializes against this read and
                    // one of the two transactions aborts with a
                    // serialization failure (retried by the execution
                    // strategy, then rejected here on the re-run).
                    bool nameTaken = await context.GuildRecords.AnyAsync(g => g.Name == guildName);
                    if (nameTaken)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    var guild = new GuildRecord
                    {
                        Name = guildName,
                        ActiveMembers = 1
                    };
                    context.GuildRecords.Add(guild);
                    await context.SaveChangesAsync();

                    context.GuildMembers.Add(new GuildMember
                    {
                        PlayerId = playerId,
                        GuildId = guild.Id,
                        ContributionPoints = 0,
                        Role = RoleLeader
                    });
                    profile.GuildId = guild.Id;

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return guild.Id;
                });

                if (createdGuildId > 0)
                {
                    _playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
                    {
                        PlayerId = playerId,
                        OldGuildId = 0,
                        NewGuildId = createdGuildId
                    });
                }

                return createdGuildId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild creation failed - PlayerId {playerId}, Name '{guildName}': {ex.Message}");
                return 0;
            }
        }

        // Joins an existing guild as a regular Member. Rejected if the
        // caller is already in a guild, the guild does not exist, or the
        // guild is at its MaxMembers capacity.
        public async Task<bool> JoinGuildAsync(long playerId, long guildId)
        {
            if (guildId <= 0)
            {
                return false;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                bool joined = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    // Guild row locked first: ActiveMembers is the capacity
                    // counter, so concurrent joins against the same guild
                    // serialize here instead of both passing the capacity
                    // check.
                    var guild = await context.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", guildId)
                        .SingleOrDefaultAsync();

                    if (guild == null || guild.ActiveMembers >= guild.MaxMembers)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    var profile = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (profile == null || profile.GuildId > 0)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    context.GuildMembers.Add(new GuildMember
                    {
                        PlayerId = playerId,
                        GuildId = guildId,
                        ContributionPoints = 0,
                        Role = RoleMember
                    });
                    profile.GuildId = guildId;
                    guild.ActiveMembers++;

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });

                if (joined)
                {
                    _playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
                    {
                        PlayerId = playerId,
                        OldGuildId = 0,
                        NewGuildId = guildId
                    });
                }

                return joined;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild join failed - PlayerId {playerId}, GuildId {guildId}: {ex.Message}");
                return false;
            }
        }

        // Leaves the caller's current guild. If the caller is the Leader
        // and other members remain, leadership transfers to the remaining
        // member with the highest ContributionPoints (lowest PlayerId on a
        // tie, so the outcome is deterministic). If the caller was the last
        // member, the guild record itself is deleted.
        //
        // Modul: lock-order normalization. Every other guild-mutating
        // method (JoinGuildAsync, KickMemberAsync) locks GuildRecords
        // before PlayerRecords; this method previously locked PlayerRecords
        // first (needed the profile row to discover which guild to act on)
        // then GuildRecords second - the exact reverse order, which is a
        // genuine Postgres Serializable deadlock hazard the moment a leave
        // and a concurrent join/kick target the same guild. Resolving the
        // target guild id from the GuildMembers row via an unlocked,
        // AsNoTracking read BEFORE taking any FOR UPDATE lock breaks that
        // chicken-and-egg problem, letting this method lock in the same
        // Guild-then-Player order as its siblings. The profile's GuildId is
        // re-checked against the resolved guildId after both locks are
        // held, since the unlocked lookup could be stale by the time the
        // locks are acquired (the player left/rejoined a different guild
        // in a concurrent, already-committed transaction) - a mismatch
        // means this attempt is stale and must fail cleanly rather than
        // mutate the wrong guild.
        public async Task<bool> LeaveGuildAsync(long playerId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                long leftGuildId = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var membershipLookup = await context.GuildMembers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(m => m.PlayerId == playerId);

                    if (membershipLookup == null)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    long guildId = membershipLookup.GuildId;

                    var guild = await context.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", guildId)
                        .SingleOrDefaultAsync();

                    var profile = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (profile == null || profile.GuildId != guildId)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    var membership = await context.GuildMembers
                        .FirstOrDefaultAsync(m => m.PlayerId == playerId && m.GuildId == guildId);

                    if (membership == null)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    bool wasLeader = membership.Role == RoleLeader;
                    context.GuildMembers.Remove(membership);
                    profile.GuildId = 0;

                    if (guild != null)
                    {
                        guild.ActiveMembers = Math.Max(0, guild.ActiveMembers - 1);

                        if (guild.ActiveMembers == 0)
                        {
                            context.GuildRecords.Remove(guild);
                        }
                        else if (wasLeader)
                        {
                            var successor = await context.GuildMembers
                                .Where(m => m.GuildId == guildId && m.PlayerId != playerId)
                                .OrderByDescending(m => m.ContributionPoints)
                                .ThenBy(m => m.PlayerId)
                                .FirstOrDefaultAsync();

                            if (successor != null)
                            {
                                successor.Role = RoleLeader;
                            }
                        }
                    }

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return guildId;
                });

                if (leftGuildId > 0)
                {
                    _playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
                    {
                        PlayerId = playerId,
                        OldGuildId = leftGuildId,
                        NewGuildId = 0
                    });
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leave failed - PlayerId {playerId}: {ex.Message}");
                return false;
            }
        }

        // Removes targetPlayerId from the kicker's guild. Only the guild
        // Leader may kick, a Leader cannot kick themselves (use
        // LeaveGuildAsync, which handles succession), and both players must
        // be in the same guild.
        public async Task<bool> KickMemberAsync(long kickerPlayerId, long targetPlayerId)
        {
            if (kickerPlayerId == targetPlayerId)
            {
                return false;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                long kickedFromGuildId = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var kickerMembership = await context.GuildMembers
                        .FirstOrDefaultAsync(m => m.PlayerId == kickerPlayerId);

                    if (kickerMembership == null || kickerMembership.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    long guildId = kickerMembership.GuildId;

                    var guild = await context.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", guildId)
                        .SingleOrDefaultAsync();

                    var targetMembership = await context.GuildMembers
                        .FirstOrDefaultAsync(m => m.PlayerId == targetPlayerId && m.GuildId == guildId);

                    var targetProfile = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", targetPlayerId)
                        .SingleOrDefaultAsync();

                    if (guild == null || targetMembership == null || targetProfile == null)
                    {
                        await transaction.RollbackAsync();
                        return 0L;
                    }

                    context.GuildMembers.Remove(targetMembership);
                    targetProfile.GuildId = 0;
                    guild.ActiveMembers = Math.Max(0, guild.ActiveMembers - 1);

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return guildId;
                });

                if (kickedFromGuildId > 0)
                {
                    _playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
                    {
                        PlayerId = targetPlayerId,
                        OldGuildId = kickedFromGuildId,
                        NewGuildId = 0
                    });
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild kick failed - Kicker {kickerPlayerId}, Target {targetPlayerId}: {ex.Message}");
                return false;
            }
        }
    }
}
