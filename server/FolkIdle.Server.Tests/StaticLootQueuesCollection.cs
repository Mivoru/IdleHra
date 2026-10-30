using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Test classes that DRAIN the process-wide loot queues and are not in the
    /// Postgres collection.
    /// </summary>
    /// <remarks>
    /// Modul: A DRAIN TAKES EVERYONE'S ITEMS, NOT JUST ITS OWN (roadmap 2.2,
    /// 2026-10-30). <c>CombatLootEngine.DropRequestQueue</c> and
    /// <c>GatheringGrantQueue</c> are static, and xUnit runs collections in
    /// parallel. These classes empty the queues to keep their own runs clean,
    /// so a run that overlapped the Postgres collection could take a request
    /// another test had just enqueued:
    /// <c>AutomationAutoFuseTests.TheWorker_FusesTheStacksARequestsDropsLandedIn</c>
    /// then "fused nothing within 60 seconds", on every full local run and
    /// never alone. A collection with parallelisation disabled runs after the
    /// parallel ones finish, one class at a time, so no drain can overlap a
    /// test that is waiting on the queue. Put a new draining class here, or in
    /// the Postgres collection; never leave it free-standing.
    /// </remarks>
    [CollectionDefinition("Static loot queues", DisableParallelization = true)]
    public class StaticLootQueuesCollection
    {
    }
}
