using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The tick thread's friend and block-list commands.
    ///
    /// Modul: a COORDINATOR, not an engine - a static class with no fields,
    /// called synchronously on the 10Hz tick thread by SimulationEngine's
    /// command dispatch table after CommandGate has said Proceed. It owns no
    /// thread, timer or state; anything asynchronous goes through the
    /// SafeDispatch delegate it is handed, never a Task.Run of its own.
    /// </summary>
    internal static class RelationshipTickCoordinator
    {
        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.AddFriend)
        internal static void HandleAddFriend(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            long targetId = cmd.TargetPlayerId;
            if (ctx.RelationshipEngine != null)
            {
                var relationshipEngine = ctx.RelationshipEngine;
                ctx.SafeDispatch("Relationship.AddFriend", pId, async () => {
                    await relationshipEngine.AddFriendAsync(pId, targetId);
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RemoveFriend)
        internal static void HandleRemoveFriend(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            long targetId = cmd.TargetPlayerId;
            if (ctx.RelationshipEngine != null)
            {
                var relationshipEngine = ctx.RelationshipEngine;
                ctx.SafeDispatch("Relationship.RemoveFriend", pId, async () => {
                    await relationshipEngine.RemoveFriendAsync(pId, targetId);
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.BlockPlayer)
        internal static void HandleBlockPlayer(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            long targetId = cmd.TargetPlayerId;
            if (ctx.RelationshipEngine != null)
            {
                var relationshipEngine = ctx.RelationshipEngine;
                ctx.SafeDispatch("Relationship.BlockPlayer", pId, async () => {
                    await relationshipEngine.BlockPlayerAsync(pId, targetId);
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.UnblockPlayer)
        internal static void HandleUnblockPlayer(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long pId = currentPayload.PlayerId;
            long targetId = cmd.TargetPlayerId;
            if (ctx.RelationshipEngine != null)
            {
                var relationshipEngine = ctx.RelationshipEngine;
                ctx.SafeDispatch("Relationship.UnblockPlayer", pId, async () => {
                    await relationshipEngine.UnblockPlayerAsync(pId, targetId);
                });
            }
        }
    }
}
