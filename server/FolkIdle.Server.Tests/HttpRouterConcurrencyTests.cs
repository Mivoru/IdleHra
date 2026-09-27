using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 40 / audit plan item 1: the HTTP accept loop used to await every
    /// handler inline, so one slow request - or a body trickled one byte at a
    /// time, unauthenticated, at /api/v1/assets/handshake - stalled every
    /// login, every WebSocket upgrade and every health probe behind it.
    ///
    /// In the Postgres collection because it flips the static
    /// GlobalEngineState.IsColdBootRecoveryComplete, like every other test
    /// that starts a real listener (see E2EGameLoopTest's own note).
    /// </summary>
    [Collection("Postgres collection")]
    public class HttpRouterConcurrencyTests
    {
        private readonly PostgresTestFixture _fixture;

        public HttpRouterConcurrencyTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // Modul: THE REGRESSION ITSELF. A request that promises 100 bytes and
        // sends none holds its handler in the body read for as long as the
        // peer likes. On the old single-lane loop /healthz queued behind it
        // indefinitely; this test failed on main (2026-09-27) with the probe
        // timing out, which is the whole server frozen by one socket.
        [Fact]
        public async Task A_StalledRequestBody_DoesNotBlockHealthz()
        {
            int port = FreePort();
            var network = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, $"http://localhost:{port}/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            network.Start();

            using var staller = new TcpClient();
            try
            {
                await staller.ConnectAsync("localhost", port);
                var stream = staller.GetStream();
                byte[] head = Encoding.ASCII.GetBytes(
                    "POST /api/v1/assets/handshake HTTP/1.1\r\n" +
                    $"Host: localhost:{port}\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Content-Length: 100\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.FlushAsync();

                // Give the listener time to hand the stalled context to the
                // router, so the probe really queues behind it on a serial loop.
                await Task.Delay(300);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                HttpResponseMessage probe = await http.GetAsync($"http://localhost:{port}/healthz");
                sw.Stop();

                Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
                Assert.True(sw.ElapsedMilliseconds < 1000, $"/healthz took {sw.ElapsedMilliseconds} ms behind a stalled body");
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                network.Stop();
            }
        }

        // Modul: THE TRAP IN THE FIX. The serial loop was, by accident, what
        // stopped a double-tapped sale from racing itself. Two sales of the
        // same whole stack for one account, fired together: exactly one sells,
        // the other is told "nothing there" - never a 500, never gold twice.
        [Fact]
        public async Task B_ConcurrentChestSalesOfOneStack_SellExactlyOnce()
        {
            const string itemId = "iron_ore";
            const long stack = 40L;
            Guid accountId = Guid.NewGuid();
            long playerId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() };
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();
                playerId = player.Id;
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = itemId, Quantity = stack });
                await db.SaveChangesAsync();
            }

            int port = FreePort();
            var network = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, $"http://localhost:{port}/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            network.Start();

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2ETestHarness.MintTestJwt(accountId));

                Task<HttpResponseMessage> Sell() => http.PostAsync(
                    $"http://localhost:{port}/api/v1/chest/sell",
                    new StringContent(JsonSerializer.Serialize(new { itemId, quantity = stack }), Encoding.UTF8, "application/json"));

                var responses = await Task.WhenAll(Sell(), Sell());

                Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

                var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
                var parsed = bodies.Select(b => JsonDocument.Parse(b).RootElement).ToArray();
                Assert.Equal(1, parsed.Count(p => p.GetProperty("Success").GetBoolean()));
                Assert.Equal(1, parsed.Count(p => !p.GetProperty("Success").GetBoolean()));

                long expectedGold = VillageChestEngine.ValueMaterial(itemId, stack);
                Assert.Equal(expectedGold, parsed.Sum(p => p.GetProperty("GoldGained").GetInt64()));

                await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                long left = await check.CommodityRecords.Where(c => c.PlayerId == playerId && c.ItemId == itemId).SumAsync(c => c.Quantity);
                long gold = await check.CommodityRecords.Where(c => c.PlayerId == playerId && c.ItemId == "gold").SumAsync(c => c.Quantity);
                Assert.Equal(0L, left);
                Assert.Equal(expectedGold, gold);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                network.Stop();
            }
        }

        // Modul: B passes on the engine's own FOR UPDATE even without the
        // router lock, so this is the test that proves the lock EXISTS: one
        // request for an account parks inside its handler (a body that never
        // arrives), and a second mutating request for the same account waits
        // its 10 s and is refused with a reason the client can show - not
        // run alongside it, and not silently dropped.
        [Fact]
        public async Task D_SecondMutationForABusyAccount_Answers429WithReason()
        {
            Guid accountId = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            string jwt = E2ETestHarness.MintTestJwt(accountId);
            int port = FreePort();
            var network = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, $"http://localhost:{port}/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            network.Start();

            using var holder = new TcpClient();
            try
            {
                await holder.ConnectAsync("localhost", port);
                byte[] head = Encoding.ASCII.GetBytes(
                    "POST /api/v1/chest/sell HTTP/1.1\r\n" +
                    $"Host: localhost:{port}\r\n" +
                    $"Authorization: Bearer {jwt}\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Content-Length: 100\r\n\r\n");
                await holder.GetStream().WriteAsync(head);
                await Task.Delay(500);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var second = await http.PostAsync($"http://localhost:{port}/api/v1/chest/sell",
                    new StringContent("{\"itemId\":\"iron_ore\",\"quantity\":1}", Encoding.UTF8, "application/json"));
                sw.Stop();

                Assert.Equal((HttpStatusCode)429, second.StatusCode);
                Assert.Contains("AccountBusy", await second.Content.ReadAsStringAsync());
                Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(9), $"refused after {sw.ElapsedMilliseconds} ms - it did not wait for the stripe");

                // A GET for the same account is never queued behind it.
                var read = await http.GetAsync($"http://localhost:{port}/healthz");
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                network.Stop();
            }
        }

        [Fact]
        public async Task E_OversizedBody_Answers413()
        {
            int port = FreePort();
            var network = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, $"http://localhost:{port}/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            network.Start();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string huge = "{\"catalog.hash\":\"" + new string('a', 70 * 1024) + "\"}";
                var response = await http.PostAsync($"http://localhost:{port}/api/v1/assets/handshake",
                    new StringContent(huge, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                network.Stop();
            }
        }

        // Modul: an unbounded body read is how one socket froze the server.
        // Every read goes through ReadBodyAsync (a cap and a deadline) now, and
        // this is the grep that keeps it that way - the same kind of mechanical
        // guard as client_web/tests/runesMode.test.ts.
        [Fact]
        public void C_NoUnboundedBodyReadsInTheRouter()
        {
            string path = Path.Combine(FindServerRoot(), "FolkIdle.Server", "Network", "NetworkBroadcastSystem.cs");
            string source = File.ReadAllText(path);

            var offenders = source.Split('\n')
                .Select((line, i) => (line, number: i + 1))
                .Where(x => x.line.Contains("ReadToEndAsync", StringComparison.Ordinal)
                    && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(x => $"{x.number}: {x.line.Trim()}")
                .ToList();

            Assert.True(offenders.Count == 0,
                "NetworkBroadcastSystem.cs reads a request body without a cap - use ReadBodyAsync:\n" + string.Join("\n", offenders));

            // And the input stream is not read directly anywhere except the
            // helper itself and the fixed-size admin packet.
            int directReads = Regex.Matches(source, @"Request\.InputStream\.ReadAsync").Count;
            Assert.True(directReads <= 1, $"expected at most the admin packet's fixed-size read, found {directReads}");
        }

        private static string FindServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
