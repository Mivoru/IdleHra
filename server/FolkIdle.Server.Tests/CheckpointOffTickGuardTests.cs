using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 43: no synchronous checkpoint on the tick thread.
    ///
    /// Modul: FlushStateAndAdvance runs a Serializable FOR UPDATE transaction
    /// and blocks until it commits. It was called from ten sites, nine of them
    /// on the 10 Hz thread that simulates every player. All of those queue on
    /// CheckpointWriter now; the only call left is the login path, which runs
    /// inside its own SafeDispatchAsync task. This greps the domain and the
    /// engine so a new tick-side call cannot come back quietly.
    /// </summary>
    public class CheckpointOffTickGuardTests
    {
        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "FolkIdle.Server");
        }

        [Fact]
        public void OnlyTheLoginPathStillFlushesSynchronously()
        {
            string root = ServerRoot();
            var files = Directory.EnumerateFiles(Path.Combine(root, "Domain"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "Engine"), "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "Network"), "*.cs", SearchOption.AllDirectories));

            var calls = new List<(string File, int Line)>();
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i].TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("///") || code.StartsWith("*")) continue;
                    if (!code.Contains("FlushStateAndAdvance(")) continue;
                    if (code.Contains("public bool FlushStateAndAdvance(")) continue; // the definition
                    calls.Add((file, i));
                }
            }

            var call = Assert.Single(calls);
            Assert.EndsWith("SimulationEngine.cs", call.File);

            // It must sit inside the Login dispatch lambda, which runs on the
            // thread pool, not in the tick's own command loop.
            var source = File.ReadAllLines(call.File);
            int loginDispatch = -1;
            for (int i = call.Line; i >= Math.Max(0, call.Line - 80); i--)
            {
                if (source[i].Contains("SafeDispatchAsync(\"Login\"")) { loginDispatch = i; break; }
            }
            Assert.True(loginDispatch >= 0,
                $"FlushStateAndAdvance at SimulationEngine.cs:{call.Line + 1} is not inside the Login dispatch - " +
                "a checkpoint on the tick thread blocks every player. Use StateCheckpointManager.RequestFlush.");
        }
    }
}
