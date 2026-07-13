using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class EcoTelemetryEngine
    {
        private const double DiamondGoldEquivalent = 500.0;
        private const double LowerParityBound = 0.85;
        private const double UpperParityBound = 1.15;
        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        public EcoTelemetryEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ExecuteAuditAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Eco telemetry audit failed: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }

        private async Task ExecuteAuditAsync(CancellationToken stoppingToken)
        {
            long totalGoldMinted;
            long totalGoldConsumed;
            long totalDiamondsMinted;
            long totalDiamondsConsumed;

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var readTx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, stoppingToken);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", stoppingToken);

                long goldBalances = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.ItemId == "gold")
                    .SumAsync(c => (long?)c.Quantity, stoppingToken) ?? 0L;

                long mailboxGold = await db.MailboxInstances
                    .AsNoTracking()
                    .Where(m => !m.IsClaimed)
                    .SumAsync(m => (long?)m.GoldAttachment, stoppingToken) ?? 0L;

                long guildGoldSinks = await db.GuildMaterialSinkLedgers
                    .AsNoTracking()
                    .Where(g => g.CommodityId == "gold")
                    .SumAsync(g => (long?)g.TotalAmountContributed, stoppingToken) ?? 0L;

                long filledMarketVolume = await db.MarketOrderRecords
                    .AsNoTracking()
                    .Where(o => o.Status == 1)
                    .SumAsync(o => (long?)o.Price, stoppingToken) ?? 0L;

                long estimatedMarketFees = filledMarketVolume / 20L;
                long premiumBalances = await db.PlayerRecords
                    .AsNoTracking()
                    .SumAsync(p => (long?)p.PremiumDiamonds, stoppingToken) ?? 0L;

                long purchaseDiamonds = await db.PrimaryPurchaseLedgers
                    .AsNoTracking()
                    .LongCountAsync(stoppingToken) * 100L;

                totalGoldConsumed = guildGoldSinks + estimatedMarketFees;
                totalGoldMinted = goldBalances + mailboxGold + totalGoldConsumed;
                totalDiamondsConsumed = 0L;
                totalDiamondsMinted = Math.Max(premiumBalances, purchaseDiamonds);

                await readTx.CommitAsync(stoppingToken);
            }

            double numerator = totalGoldMinted + (totalDiamondsMinted * DiamondGoldEquivalent);
            double denominator = totalGoldConsumed + (totalDiamondsConsumed * DiamondGoldEquivalent);
            double ratio = denominator <= 0.0 ? numerator : numerator / denominator;

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var writeTx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);
                db.EcoTelemetryLedgers.Add(new EcoTelemetryLedger
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    TotalGoldMinted = totalGoldMinted,
                    TotalGoldConsumed = totalGoldConsumed,
                    TotalDiamondsMinted = totalDiamondsMinted,
                    TotalDiamondsConsumed = totalDiamondsConsumed,
                    CalculatedRatio = ratio
                });
                await db.SaveChangesAsync(stoppingToken);
                await writeTx.CommitAsync(stoppingToken);
            }

            if (ratio < LowerParityBound || ratio > UpperParityBound)
            {
                GlobalEngineState.GlobalGoldDropMultiplier = 75;
                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = 0,
                    EventType = 6,
                    Value1 = 44,
                    Value2 = (int)Math.Clamp(ratio * 1000.0, 0.0, int.MaxValue),
                    Timestamp = Environment.TickCount64
                });
            }
            else
            {
                GlobalEngineState.GlobalGoldDropMultiplier = 100;
            }
        }
    }
}
