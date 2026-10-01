using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Social
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
        public const int RoleOfficer = 1;
        public const int RoleLeader = 2;

        // Modul: Advanced Economy Refactoring, Part 3.1. Universal
        // structural unlock gate - every guild interaction (creating,
        // joining, applying) requires CurrentLevel >= 20. Enforced here
        // rather than in SimulationEngine because this engine is the
        // single authoritative entry point for all guild membership
        // mutations (no guild-join wire command exists in
        // SimulationEngine's command loop to gate).
        // Modul: 20 -> 10. Twenty put guilds behind most of the early game, and
        // a guild is the trade licence - so a player could not use the market
        // either, for a long time, with nothing saying why.
        public const int MinGuildInteractionLevel = 10;

        public const int JoinTypeOpen = 0;
        public const int JoinTypeApplicationRequired = 1;

        // Modul: 32, not the column's 100. The client's input has stopped at
        // 32 since task 92 because a 100-character unbroken name overflows
        // toasts, rosters and the guild list - but a direct POST still minted
        // one, so the client's limit was a suggestion. The cap lives here, the
        // one place that writes a name. GuildRecord.Name keeps MaxLength(100):
        // guilds founded before this (if any are longer) stay joinable by
        // their exact name, and narrowing the column would be a migration that
        // truncates them.
        public const int MaxGuildNameLength = 32;

        // Creates a new guild with the caller as its sole member and Leader.
        // Returns the new guild's id, or 0 if rejected (caller already in a
        // guild, empty/overlong name, or duplicate guild name).
        /// <summary>
        /// Why a guild could not be created.
        ///
        /// Modul: this method returned a bare 0 for four completely different
        /// refusals - already in a guild, below the level gate, name taken,
        /// name malformed - and the endpoint turned all four into a 409 with
        /// no body. The player was told "Could not create" and nothing else,
        /// with no way to find out what the level requirement even was.
        /// </summary>
        public enum GuildCreateRefusal
        {
            None = 0,
            NameInvalid = 1,
            AlreadyInAGuild = 2,
            LevelTooLow = 3,
            NameTaken = 4,
        }

        public sealed class GuildCreateOutcome
        {
            public long GuildId;
            public GuildCreateRefusal Refusal;
            public int RequiredLevel;
            public int CurrentLevel;
        }

        public async Task<GuildCreateOutcome> CreateGuildAsync(long playerId, string guildName)
        {
            if (string.IsNullOrWhiteSpace(guildName) || guildName.Length > MaxGuildNameLength)
            {
                return new GuildCreateOutcome { Refusal = GuildCreateRefusal.NameInvalid };
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                var outcome = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var profile = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (profile == null || profile.GuildId > 0)
                    {
                        await transaction.RollbackAsync();
                        return new GuildCreateOutcome { Refusal = GuildCreateRefusal.AlreadyInAGuild };
                    }

                    // Modul: Advanced Economy Refactoring, Part 3.1 -
                    // universal level-20 structural gate.
                    if (profile.CurrentLevel < MinGuildInteractionLevel)
                    {
                        await transaction.RollbackAsync();
                        return new GuildCreateOutcome
                        {
                            Refusal = GuildCreateRefusal.LevelTooLow,
                            RequiredLevel = MinGuildInteractionLevel,
                            CurrentLevel = profile.CurrentLevel,
                        };
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
                        return new GuildCreateOutcome { Refusal = GuildCreateRefusal.NameTaken };
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
                    return new GuildCreateOutcome { GuildId = guild.Id };
                });

                if (outcome.GuildId > 0)
                {
                    PublishJoined(playerId, outcome.GuildId);
                }

                return outcome;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild creation failed - PlayerId {playerId}, Name '{guildName}': {ex.Message}");
                return new GuildCreateOutcome { Refusal = GuildCreateRefusal.NameInvalid };
            }
        }

        // Modul: THE ONE PLACE A COMMITTED JOIN IS ANNOUNCED. Three routes put
        // a player into a guild - founding one, an open join, and a leader
        // approving an application - and each used to enqueue its own
        // membership change. They share this now so funnel step 10
        // (joined_guild) has exactly one writer and cannot be forgotten by a
        // fourth route that copies only the notification. Call it after the
        // commit, never inside the transaction.
        private void PublishJoined(long playerId, long guildId)
        {
            _playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
            {
                PlayerId = playerId,
                OldGuildId = 0,
                NewGuildId = guildId
            });

            FunnelRecorder.Record(playerId, FunnelStep.JoinedGuild);
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

                    // Modul: Advanced Economy Refactoring, Part 3.1/3.3.
                    // Universal level-20 gate first, then the guild's own
                    // (potentially stricter) MinApplicationLevel - both
                    // apply identically to auto-joins and applications, so
                    // an under-leveled player can neither join an open
                    // guild nor spam pending applications at a gated one.
                    int effectiveMinLevel = Math.Max(MinGuildInteractionLevel, guild.MinApplicationLevel);
                    if (profile.CurrentLevel < effectiveMinLevel)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    // Modul: Advanced Economy Refactoring, Part 3.3.
                    // Application-required guilds route the request into
                    // the pending GuildApplications table for manual
                    // approval instead of joining immediately - returns
                    // false ("not joined") while the application row
                    // persists. Duplicate open applications from the same
                    // player are a no-op under this same Serializable
                    // transaction.
                    if (guild.JoinType == JoinTypeApplicationRequired)
                    {
                        bool alreadyApplied = await context.GuildApplications
                            .AnyAsync(a => a.GuildId == guildId && a.PlayerId == playerId);
                        if (!alreadyApplied)
                        {
                            context.GuildApplications.Add(new GuildApplication
                            {
                                GuildId = guildId,
                                PlayerId = playerId,
                                ApplicantLevel = profile.CurrentLevel,
                                CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                            await context.SaveChangesAsync();
                        }
                        await transaction.CommitAsync();
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
                    PublishJoined(playerId, guildId);
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
            return (await LeaveAsync(playerId)).Left;
        }

        /// <summary>
        /// What leaving did, so the route can tell the player rather than
        /// answer a bare 200.
        /// </summary>
        public sealed class GuildLeaveOutcome
        {
            public bool Left;
            public long GuildId;
            /// <summary>The leaver was the last member and the guild is gone.</summary>
            public bool ClosedGuild;
            /// <summary>Who leads now, when a leader left a guild with others in it; 0 otherwise.</summary>
            public long SuccessorPlayerId;
        }

        /// <summary>
        /// What leaving WOULD do, read before the player commits - the confirm
        /// names the next leader, or says the guild closes.
        /// </summary>
        public sealed class GuildLeavePreview
        {
            public bool InGuild;
            public long GuildId;
            public bool IsLeader;
            public bool ClosesGuild;
            public long SuccessorPlayerId;
            public int RemainingMembers;
        }

        // Modul: ONE SUCCESSION RULE, read by both the preview and the leave.
        // The confirm button promises a name before the player commits; if the
        // preview ordered candidates one way and the leave another, the promise
        // would be a guess. Highest ContributionPoints, lowest PlayerId on a
        // tie, so the answer is deterministic.
        private static IQueryable<GuildMember> SuccessionOrder(IQueryable<GuildMember> members, long guildId, long leavingPlayerId)
        {
            return members
                .Where(m => m.GuildId == guildId && m.PlayerId != leavingPlayerId)
                .OrderByDescending(m => m.ContributionPoints)
                .ThenBy(m => m.PlayerId);
        }

        public async Task<GuildLeavePreview> PreviewLeaveAsync(long playerId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);

            var membership = await context.GuildMembers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.PlayerId == playerId);

            if (membership == null)
            {
                return new GuildLeavePreview();
            }

            long guildId = membership.GuildId;
            int remaining = await context.GuildMembers
                .AsNoTracking()
                .CountAsync(m => m.GuildId == guildId && m.PlayerId != playerId);

            bool isLeader = membership.Role == RoleLeader;
            long successor = 0;
            if (isLeader && remaining > 0)
            {
                successor = await SuccessionOrder(context.GuildMembers.AsNoTracking(), guildId, playerId)
                    .Select(m => m.PlayerId)
                    .FirstOrDefaultAsync();
            }

            return new GuildLeavePreview
            {
                InGuild = true,
                GuildId = guildId,
                IsLeader = isLeader,
                ClosesGuild = remaining == 0,
                SuccessorPlayerId = successor,
                RemainingMembers = remaining,
            };
        }

        public async Task<GuildLeaveOutcome> LeaveAsync(long playerId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                var outcome = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var membershipLookup = await context.GuildMembers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(m => m.PlayerId == playerId);

                    if (membershipLookup == null)
                    {
                        await transaction.RollbackAsync();
                        return new GuildLeaveOutcome();
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
                        return new GuildLeaveOutcome();
                    }

                    var membership = await context.GuildMembers
                        .FirstOrDefaultAsync(m => m.PlayerId == playerId && m.GuildId == guildId);

                    if (membership == null)
                    {
                        await transaction.RollbackAsync();
                        return new GuildLeaveOutcome();
                    }

                    bool wasLeader = membership.Role == RoleLeader;
                    context.GuildMembers.Remove(membership);
                    profile.GuildId = 0;

                    var result = new GuildLeaveOutcome { Left = true, GuildId = guildId };

                    if (guild != null)
                    {
                        // Modul: COUNT THE ROWS, DO NOT TRUST THE COUNTER. This
                        // used to decrement ActiveMembers and delete the guild
                        // when it reached 0. Nothing reconciles that counter
                        // against GuildMembers, so one that had drifted low
                        // would delete a guild that still had people in it,
                        // leaving their GuildMembers rows and PlayerRecords.
                        // GuildId pointing at nothing. The guild row is locked
                        // FOR UPDATE above and every join locks it too, so this
                        // count cannot race a join. The counter is resynced
                        // from it, which also heals a drifted one.
                        int remaining = await context.GuildMembers
                            .CountAsync(m => m.GuildId == guildId && m.PlayerId != playerId);

                        if (remaining == 0)
                        {
                            // Modul: a closed guild's pending applications go
                            // with it - nothing can approve them, and the
                            // applicant would wait on a guild that no longer
                            // exists. The depot, buffs and war rows are left
                            // as they were (as before this route existed);
                            // they key on an id nothing will reuse.
                            var orphanedApplications = await context.GuildApplications
                                .Where(a => a.GuildId == guildId)
                                .ToListAsync();
                            context.GuildApplications.RemoveRange(orphanedApplications);
                            context.GuildRecords.Remove(guild);
                            result.ClosedGuild = true;
                        }
                        else
                        {
                            guild.ActiveMembers = remaining;

                            if (wasLeader)
                            {
                                var successor = await SuccessionOrder(context.GuildMembers, guildId, playerId)
                                    .FirstOrDefaultAsync();

                                if (successor != null)
                                {
                                    successor.Role = RoleLeader;
                                    result.SuccessorPlayerId = successor.PlayerId;
                                }
                            }
                        }
                    }

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return result;
                });

                if (outcome.Left)
                {
                    _playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
                    {
                        PlayerId = playerId,
                        OldGuildId = outcome.GuildId,
                        NewGuildId = 0
                    });
                }

                return outcome;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leave failed - PlayerId {playerId}: {ex.Message}");
                return new GuildLeaveOutcome();
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

                    // Only Leader and Officer can kick.
                    if (kickerMembership == null || kickerMembership.Role < RoleOfficer)
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

                    // Kicker must have a strictly higher role than the target (e.g. Leader can kick Officer, Officer can kick Member).
                    if (kickerMembership.Role <= targetMembership.Role)
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

        // Modul: Play Mode audit fix. JoinGuildAsync has always filed a
        // GuildApplication row for JoinType-Application-Required guilds
        // (see that method's own comment), but nothing anywhere - server
        // or client - ever read, approved, or rejected one. Any guild
        // gated behind "application required" was a black hole for new
        // members. Leader-only, matching KickMemberAsync's exact
        // permission-check shape.
        public async Task<System.Collections.Generic.List<GuildApplication>> ListPendingApplicationsAsync(long guildId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            return await context.GuildApplications
                .AsNoTracking()
                .Where(a => a.GuildId == guildId)
                .OrderBy(a => a.CreatedAtEpoch)
                .ToListAsync();
        }

        public async Task<bool> ApproveApplicationAsync(long leaderPlayerId, long applicationId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                (bool Approved, long ApplicantPlayerId, long GuildId) result = await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var leaderMembership = await context.GuildMembers
                        .FirstOrDefaultAsync(m => m.PlayerId == leaderPlayerId);

                    if (leaderMembership == null || leaderMembership.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return (false, 0L, 0L);
                    }

                    long guildId = leaderMembership.GuildId;

                    var application = await context.GuildApplications
                        .FromSqlRaw("SELECT * FROM \"GuildApplications\" WHERE \"Id\" = {0} FOR UPDATE", applicationId)
                        .SingleOrDefaultAsync();

                    if (application == null || application.GuildId != guildId)
                    {
                        await transaction.RollbackAsync();
                        return (false, 0L, 0L);
                    }

                    var guild = await context.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", guildId)
                        .SingleOrDefaultAsync();

                    var applicantProfile = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", application.PlayerId)
                        .SingleOrDefaultAsync();

                    // Modul: an applicant may have joined a different guild
                    // (or this one, via another pending application) or the
                    // guild may have filled up since the application was
                    // filed - either way the stale application is consumed
                    // here rather than left to be approved again later.
                    if (guild == null || applicantProfile == null || applicantProfile.GuildId > 0 || guild.ActiveMembers >= guild.MaxMembers)
                    {
                        context.GuildApplications.Remove(application);
                        await context.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return (false, 0L, 0L);
                    }

                    context.GuildMembers.Add(new GuildMember
                    {
                        PlayerId = application.PlayerId,
                        GuildId = guildId,
                        ContributionPoints = 0,
                        Role = RoleMember
                    });
                    applicantProfile.GuildId = guildId;
                    guild.ActiveMembers++;
                    context.GuildApplications.Remove(application);

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return (true, application.PlayerId, guildId);
                });

                if (result.Approved)
                {
                    PublishJoined(result.ApplicantPlayerId, result.GuildId);
                }

                return result.Approved;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild application approve failed - Leader {leaderPlayerId}, Application {applicationId}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RejectApplicationAsync(long leaderPlayerId, long applicationId)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var leaderMembership = await context.GuildMembers
                        .FirstOrDefaultAsync(m => m.PlayerId == leaderPlayerId);

                    if (leaderMembership == null || leaderMembership.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    var application = await context.GuildApplications
                        .FromSqlRaw("SELECT * FROM \"GuildApplications\" WHERE \"Id\" = {0} FOR UPDATE", applicationId)
                        .SingleOrDefaultAsync();

                    if (application == null || application.GuildId != leaderMembership.GuildId)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    context.GuildApplications.Remove(application);
                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild application reject failed - Leader {leaderPlayerId}, Application {applicationId}: {ex.Message}");
                return false;
            }
        }

        // Modul: Advanced Economy Refactoring, Part 2.4. Leader-only
        // setter for the guild sales tax rate, clamped strictly to
        // [GuildRecord.MinTaxRatePct, GuildRecord.MaxTaxRatePct] - an
        // out-of-range request is clamped, not rejected, so a Leader
        // sliding a UI control past the bounds lands on the nearest legal
        // rate rather than silently failing.
        public async Task<bool> SetGuildTaxRateAsync(long leaderPlayerId, int requestedRatePct)
        {
            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var membership = await context.GuildMembers
                        .AsNoTracking()
                        .SingleOrDefaultAsync(m => m.PlayerId == leaderPlayerId);

                    if (membership == null || membership.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    var guild = await context.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", membership.GuildId)
                        .SingleOrDefaultAsync();

                    if (guild == null)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    guild.TaxRatePct = Math.Clamp(requestedRatePct, GuildRecord.MinTaxRatePct, GuildRecord.MaxTaxRatePct);

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild tax rate change failed - Leader {leaderPlayerId}, Rate {requestedRatePct}: {ex.Message}");
                return false;
            }
        }

        // Modul: Advanced Economy Refactoring, Part 3.2. Leader-only
        // setter for the guild's access policy: JoinType (Open vs
        // Application Required) and MinApplicationLevel. The minimum
        // level floor is the universal MinGuildInteractionLevel gate - a
        // guild cannot configure itself to admit players the structural
        // unlock would block anyway.
        public async Task<bool> SetGuildAccessPolicyAsync(long leaderPlayerId, int joinType, int minApplicationLevel)
        {
            if (joinType != JoinTypeOpen && joinType != JoinTypeApplicationRequired)
            {
                return false;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var membership = await context.GuildMembers
                        .AsNoTracking()
                        .SingleOrDefaultAsync(m => m.PlayerId == leaderPlayerId);

                    if (membership == null || membership.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    var guild = await context.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", membership.GuildId)
                        .SingleOrDefaultAsync();

                    if (guild == null)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    guild.JoinType = joinType;
                    guild.MinApplicationLevel = Math.Max(MinGuildInteractionLevel, minApplicationLevel);

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild access policy change failed - Leader {leaderPlayerId}: {ex.Message}");
                return false;
            }
        }
        public async Task<bool> PromoteMemberAsync(long promoterId, long targetId)
        {
            if (promoterId == targetId) return false;

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var promoter = await context.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == promoterId);
                    if (promoter == null || promoter.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return false; // Only Leader can promote
                    }

                    var target = await context.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == targetId && m.GuildId == promoter.GuildId);
                    if (target == null || target.Role >= RoleOfficer)
                    {
                        await transaction.RollbackAsync();
                        return false; // Already officer or not in guild
                    }

                    target.Role = RoleOfficer;
                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild promote failed - PromoterId {promoterId}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DemoteMemberAsync(long demoterId, long targetId)
        {
            if (demoterId == targetId) return false;

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    var demoter = await context.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == demoterId);
                    if (demoter == null || demoter.Role != RoleLeader)
                    {
                        await transaction.RollbackAsync();
                        return false; // Only Leader can demote
                    }

                    var target = await context.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == targetId && m.GuildId == demoter.GuildId);
                    if (target == null || target.Role != RoleOfficer)
                    {
                        await transaction.RollbackAsync();
                        return false; // Not an officer or not in guild
                    }

                    target.Role = RoleMember;
                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild demote failed - DemoterId {demoterId}: {ex.Message}");
                return false;
            }
        }

    }
}
