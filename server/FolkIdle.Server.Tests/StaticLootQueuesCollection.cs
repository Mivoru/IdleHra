using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Test classes outside the Postgres collection that FILL or DRAIN the
    /// process-wide loot queues.
    /// </summary>
    /// <remarks>
    /// Modul: THE QUEUES ARE STATIC AND THE COLLECTIONS RUN IN PARALLEL
    /// (roadmap 2.2, 2026-10-30). <c>CombatLootEngine.DropRequestQueue</c> and
    /// <c>GatheringGrantQueue</c> are shared by every test in the process.
    /// <c>AutomationAutoFuseTests.TheWorker_FusesTheStacksARequestsDropsLandedIn</c>
    /// failed every full local run and never alone ("fused nothing within 60
    /// seconds"), with its own request gone from the queue and no piece
    /// granted. The first suspects were the classes that drain the queues; moving
    /// them here did not fix it. The worker's own report did: "tick saw 12723
    /// kills, enqueued 12723, worker drained 158 requests ... 239 still queued".
    /// Live-tick tests running in parallel (RunCombatTick enqueues one request
    /// per kill) buried the test's single request under thousands, and the worker
    /// did not reach it in time.
    ///
    /// A collection with parallelisation disabled runs after the parallel ones
    /// finish, one class at a time, so nothing here can fill or empty the queues
    /// while a Postgres-collection test waits on the worker. A new class that runs
    /// RunCombatTick, RunGatheringTick, ProcessSubTick, the offline combat
    /// projection, or touches the queues directly belongs here or in the
    /// Postgres collection; never leave it free-standing.
    /// </remarks>
    [CollectionDefinition("Static loot queues", DisableParallelization = true)]
    public class StaticLootQueuesCollection
    {
    }
}
