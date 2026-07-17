using System;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public readonly struct SyncMatchStateRequestBuffer
    {
        public readonly Guid MatchUuid;
        public readonly long AttackerAccountId;
        public readonly long InflictedDamageSum;
        public readonly bool IsFinalBlow;

        public SyncMatchStateRequestBuffer(Guid matchUuid, long attackerAccountId, long inflictedDamageSum, bool isFinalBlow)
        {
            MatchUuid = matchUuid;
            AttackerAccountId = attackerAccountId;
            InflictedDamageSum = inflictedDamageSum;
            IsFinalBlow = isFinalBlow;
        }
    }

    public readonly struct SyncMatchStateResponseBuffer
    {
        public readonly uint ProcessingStatus;
        public readonly long GlobalNodeRemainingHp;

        public SyncMatchStateResponseBuffer(uint processingStatus, long globalNodeRemainingHp)
        {
            ProcessingStatus = processingStatus;
            GlobalNodeRemainingHp = globalNodeRemainingHp;
        }
    }

    public sealed class GlobalTournamentMeshService
    {
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly DistributedLockManager _lockManager;

        public GlobalTournamentMeshService(IDbContextFactory<FolkIdleDbContext> contextFactory, DistributedLockManager lockManager)
        {
            _contextFactory = contextFactory;
            _lockManager = lockManager;
        }

        public async Task<SyncMatchStateResponseBuffer> SyncMatchStateAsync(SyncMatchStateRequestBuffer request)
        {
            if (request.MatchUuid == Guid.Empty || request.AttackerAccountId <= 0 || request.InflictedDamageSum <= 0)
            {
                return new SyncMatchStateResponseBuffer(1U, 0L);
            }

            var lease = await _lockManager.AcquireGuildMatchLockAsync(request.MatchUuid, 5000);
            if (!lease.Acquired)
            {
                return new SyncMatchStateResponseBuffer(3U, 0L);
            }

            try
            {
                await using var context = await _contextFactory.CreateDbContextAsync();
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var snapshot = await context.GuildMatchmakingSnapshots
                    .FromSqlRaw("SELECT * FROM \"GuildMatchmakingSnapshots\" WHERE \"MatchUuid\" = {0} FOR UPDATE", request.MatchUuid)
                    .FirstOrDefaultAsync();

                if (snapshot == null || snapshot.AttackerGuildId != request.AttackerAccountId)
                {
                    await transaction.RollbackAsync();
                    return new SyncMatchStateResponseBuffer(1U, 0L);
                }

                if (lease.FencingToken <= 0L || lease.FencingToken <= snapshot.FencingToken)
                {
                    Console.WriteLine($"[CRITICAL] Data desynchronization in Guild Matchmaking! MatchUuid: {request.MatchUuid}, Incoming Token: {lease.FencingToken}, DB Token: {snapshot.FencingToken}");
                    await transaction.RollbackAsync();
                    return new SyncMatchStateResponseBuffer(4U, Math.Max(0L, snapshot.GlobalNodeRemainingHp));
                }

                if (snapshot.IsComplete || snapshot.GlobalNodeRemainingHp <= 0)
                {
                    await transaction.RollbackAsync();
                    return new SyncMatchStateResponseBuffer(2U, Math.Max(0L, snapshot.GlobalNodeRemainingHp));
                }

                long nextHp = snapshot.GlobalNodeRemainingHp - request.InflictedDamageSum;
                if (nextHp <= 0L || request.IsFinalBlow)
                {
                    nextHp = 0L;
                    snapshot.IsComplete = true;
                }

                snapshot.GlobalNodeRemainingHp = nextHp;
                snapshot.FencingToken = lease.FencingToken;
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return new SyncMatchStateResponseBuffer(0U, nextHp);
            }
            finally
            {
                await _lockManager.ReleaseAsync(lease);
            }
        }
    }
}
