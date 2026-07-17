using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Social
{
    // Modul: Full-Stack Social Layer, Part 2. Friend/Block relationship
    // matrix - AddFriend, RemoveFriend, BlockPlayer, UnblockPlayer. Every
    // mutation runs inside its own Serializable, row-locked transaction
    // against "PlayerRelationships", matching the isolation guarantee
    // every other mutating engine in this codebase already provides
    // (GuildContributionEngine, RelationshipEngine's own sibling engines).
    // A directed edge is at most one row per (PlayerId, TargetPlayerId)
    // pair (enforced by FolkIdleDbContext's unique index) - Friend and
    // Blocked share that same uniqueness constraint, so blocking an
    // existing friend converts the row's RelationType in place rather
    // than inserting a second row for the same pair.
    public class RelationshipEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public RelationshipEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task AddFriendAsync(long playerId, long targetPlayerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                if (targetPlayerId <= 0 || targetPlayerId == playerId)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                    return;
                }

                bool targetExists = await db.PlayerRecords.AsNoTracking().AnyAsync(p => p.Id == targetPlayerId);
                if (!targetExists)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.TargetNotFound);
                    return;
                }

                var existing = await db.PlayerRelationships
                    .FromSqlInterpolated($"SELECT * FROM \"PlayerRelationships\" WHERE \"PlayerId\" = {playerId} AND \"TargetPlayerId\" = {targetPlayerId} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (existing != null)
                {
                    // Modul: the documented safe roll-back condition - a
                    // duplicate AddFriend (already Friend, or already
                    // Blocked - blocking takes precedence and is never
                    // silently overwritten by a friend request) is
                    // rejected without mutating the existing row.
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.RelationshipAlreadyExists);
                    return;
                }

                db.PlayerRelationships.Add(new PlayerRelationship
                {
                    PlayerId = playerId,
                    TargetPlayerId = targetPlayerId,
                    RelationType = RelationType.Friend
                });

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"AddFriend failed for player {playerId}: {ex.Message}");
            }
        }

        public async Task RemoveFriendAsync(long playerId, long targetPlayerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var existing = await db.PlayerRelationships
                    .FromSqlInterpolated($"SELECT * FROM \"PlayerRelationships\" WHERE \"PlayerId\" = {playerId} AND \"TargetPlayerId\" = {targetPlayerId} AND \"RelationType\" = {RelationType.Friend} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (existing == null)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.TargetNotFound);
                    return;
                }

                db.PlayerRelationships.Remove(existing);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"RemoveFriend failed for player {playerId}: {ex.Message}");
            }
        }

        public async Task BlockPlayerAsync(long playerId, long targetPlayerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                if (targetPlayerId <= 0 || targetPlayerId == playerId)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                    return;
                }

                bool targetExists = await db.PlayerRecords.AsNoTracking().AnyAsync(p => p.Id == targetPlayerId);
                if (!targetExists)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.TargetNotFound);
                    return;
                }

                var existing = await db.PlayerRelationships
                    .FromSqlInterpolated($"SELECT * FROM \"PlayerRelationships\" WHERE \"PlayerId\" = {playerId} AND \"TargetPlayerId\" = {targetPlayerId} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (existing != null)
                {
                    if (existing.RelationType == RelationType.Blocked)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.RelationshipAlreadyExists);
                        return;
                    }

                    // Modul: an existing Friend row converts to Blocked in
                    // place - the unique index forbids a second row for
                    // the same pair, and blocking should win outright over
                    // a prior friendship rather than being rejected as a
                    // duplicate.
                    existing.RelationType = RelationType.Blocked;
                }
                else
                {
                    db.PlayerRelationships.Add(new PlayerRelationship
                    {
                        PlayerId = playerId,
                        TargetPlayerId = targetPlayerId,
                        RelationType = RelationType.Blocked
                    });
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"BlockPlayer failed for player {playerId}: {ex.Message}");
            }
        }

        public async Task UnblockPlayerAsync(long playerId, long targetPlayerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var existing = await db.PlayerRelationships
                    .FromSqlInterpolated($"SELECT * FROM \"PlayerRelationships\" WHERE \"PlayerId\" = {playerId} AND \"TargetPlayerId\" = {targetPlayerId} AND \"RelationType\" = {RelationType.Blocked} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (existing == null)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.TargetNotFound);
                    return;
                }

                db.PlayerRelationships.Remove(existing);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"UnblockPlayer failed for player {playerId}: {ex.Message}");
            }
        }
    }
}
