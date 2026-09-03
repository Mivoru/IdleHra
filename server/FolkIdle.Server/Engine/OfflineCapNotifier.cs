using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Tells a player, once per absence, that their offline progress has
    /// stopped accruing - and only if they asked to be told.
    /// </summary>
    /// <remarks>
    /// Modul: THE CAP THIS IS ABOUT IS THE TWELVE-HOUR EARNING WINDOW, not the
    /// old 500-drop equipment cap, which was a bug and is gone. Offline catch-up
    /// runs in full up to
    /// <see cref="OfflineSimulationEngine.MaxOfflineSeconds"/> and everything
    /// past it is discarded, deliberately - so there is a real moment, knowable
    /// server-side, after which staying away earns nothing at all. That moment
    /// is worth telling a player about and is the only cap left to tell them
    /// about.
    ///
    /// Three rules this will not bend on, because email is the one thing this
    /// server sends to a person rather than to a client:
    ///
    /// 1. CONSENT IS OPT-IN. PlayerRecord.EmailNotificationsConsented starts
    ///    false and is only ever set by the player, from Settings.
    /// 2. ONCE PER ABSENCE. OfflineCapEmailSentEpoch is compared against
    ///    LastLogoutTimestamp, not against now, so a player away for a week is
    ///    told once rather than every night.
    /// 3. IT FAILS QUIET AND IT FAILS CLOSED. With no provider configured
    ///    IEmailSender is the disabled one and returns false; the send is then
    ///    NOT recorded, so nothing is silently marked as delivered.
    /// </remarks>
    public sealed class OfflineCapNotifier
    {
        // Long enough that this is cheap, short enough that the mail is about
        // something that just happened. The cap itself is twelve hours.
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

        // One pass will not mail more than this. A backlog drains over
        // subsequent passes rather than handing the provider thousands of
        // messages the first time this ships.
        private const int MaxSendsPerPass = 200;

        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource? _cts;

        public OfflineCapNotifier(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        public void Stop() => _cts?.Cancel();

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);

                    using var senderScope = _serviceProvider.CreateScope();
                    var sender = senderScope.ServiceProvider.GetRequiredService<IEmailSender>();
                    await RunOnceAsync(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), sender, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A notifier must never take the server down with it.
                    Console.WriteLine($"Offline-cap notifier pass failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// One pass. Public, and takes its sender, so a test can drive it
        /// without waiting on the timer and without a live mail provider.
        /// Returns how many mails were actually handed over.
        /// </summary>
        public async Task<int> RunOnceAsync(long nowEpoch, IEmailSender email, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            // The base window is the SHORTEST cap any player can have, so this
            // query is a superset - Vodnik's extension is applied per player
            // below, where the mastery level is known.
            long earliestEligibleLogout = nowEpoch - OfflineSimulationEngine.MaxOfflineSeconds;

            var candidates = await db.PlayerRecords
                .Where(p => p.EmailNotificationsConsented
                    && p.Email != null
                    && p.LastLogoutTimestamp > 0
                    && p.LastLogoutTimestamp <= earliestEligibleLogout
                    // Once per absence: a mail already sent for THIS logout has
                    // an epoch at or after it.
                    && p.OfflineCapEmailSentEpoch < p.LastLogoutTimestamp)
                .OrderBy(p => p.LastLogoutTimestamp)
                .Take(MaxSendsPerPass)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return 0;

            // Vodnik mastery 25 raises the cap to eighteen hours. Mailing those
            // players at twelve would be telling them something untrue about
            // their own account, so their level is read and their real cap
            // applied.
            var candidateIds = candidates.Select(p => p.Id).ToList();
            var vodnikLevels = await db.PlayerRaceMasteries
                .Where(m => candidateIds.Contains(m.PlayerId) && m.RaceId == RaceIds.Vodnik)
                .ToDictionaryAsync(m => m.PlayerId, m => m.MasteryLevel, cancellationToken);

            int sent = 0;
            foreach (var player in candidates)
            {
                vodnikLevels.TryGetValue(player.Id, out int vodnikLevel);
                long capSeconds = RaceMasteryResolver.GetVodnikExtendedOfflineSeconds(
                    vodnikLevel, OfflineSimulationEngine.MaxOfflineSeconds);

                if (nowEpoch - player.LastLogoutTimestamp < capSeconds) continue;

                long hours = capSeconds / 3600L;
                bool delivered = await email.SendAsync(
                    player.Email!,
                    "Your FolkIdle village has stopped earning",
                    $"Your characters have been working for {hours} hours, which is as long as "
                    + "FolkIdle simulates while you are away. Everything up to that point is "
                    + "banked and waiting for you - gold, experience, materials and any gear "
                    + "that dropped - but nothing further is accruing until you sign in.\n\n"
                    + "Sign in to collect it and set your characters going again:\n"
                    + "https://folkidle.duckdns.org\n\n"
                    + "You are receiving this because you turned on email notifications in "
                    + "Settings. You can turn them off there at any time.");

                // Only a delivered mail is recorded. A failed send leaves the
                // player eligible, so a provider outage delays the mail rather
                // than cancelling it - and the disabled sender (no provider
                // configured) therefore never marks anybody as notified.
                if (!delivered) continue;

                player.OfflineCapEmailSentEpoch = nowEpoch;
                sent++;
            }

            if (sent > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return sent;
        }
    }
}
