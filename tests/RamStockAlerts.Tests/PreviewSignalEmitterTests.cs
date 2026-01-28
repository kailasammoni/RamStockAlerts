using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Tests.TestDoubles;

namespace RamStockAlerts.Tests;

public class PreviewSignalEmitterTests
{
    [Fact]
    public async Task ProcessSnapshotAsync_SuppressesDuplicatePreviewSignatures()
    {
        var config = BuildConfig(dedupWindowSeconds: 120);
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
        var discordService = BuildDiscordService(config);
        var logger = new ListLogger<PreviewSignalEmitter>();
        var emitter = new PreviewSignalEmitter(config, metrics, validator, discordService, logger);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = BuildBook("AAPL", nowMs, 100.00m, 100.05m);
        metrics.UpdateMetrics(book, nowMs);
        var snapshot = metrics.GetLatestSnapshot(book.Symbol)!;
        ConfigureBuySnapshot(snapshot);

        await emitter.ProcessSnapshotAsync(book, nowMs);
        await emitter.ProcessSnapshotAsync(book, nowMs + 1000);

        var emissionCount = logger.Messages.Count(message => message.StartsWith("[PREVIEW] symbol="));
        Assert.Equal(1, emissionCount);
        Assert.Contains(logger.Messages, message => message.Contains("suppressed duplicate"));
    }

    [Fact]
    public async Task ProcessSnapshotAsync_AllowsDistinctBlueprints()
    {
        var config = BuildConfig(dedupWindowSeconds: 120);
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
        var discordService = BuildDiscordService(config);
        var logger = new ListLogger<PreviewSignalEmitter>();
        var emitter = new PreviewSignalEmitter(config, metrics, validator, discordService, logger);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = BuildBook("AAPL", nowMs, 100.00m, 100.05m);
        metrics.UpdateMetrics(book, nowMs);
        var snapshot = metrics.GetLatestSnapshot(book.Symbol)!;
        ConfigureBuySnapshot(snapshot);
        await emitter.ProcessSnapshotAsync(book, nowMs);

        var nextMs = nowMs + 1000;
        var book2 = BuildBook("AAPL", nextMs, 100.00m, 100.15m);
        metrics.UpdateMetrics(book2, nextMs);
        var snapshot2 = metrics.GetLatestSnapshot(book2.Symbol)!;
        ConfigureBuySnapshot(snapshot2);
        await emitter.ProcessSnapshotAsync(book2, nextMs);

        var emissionCount = logger.Messages.Count(message => message.StartsWith("[PREVIEW] symbol="));
        Assert.Equal(2, emissionCount);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("suppressed duplicate"));
    }

    private static IConfiguration BuildConfig(int dedupWindowSeconds)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Preview:Enabled"] = "true",
            ["Preview:MinScore"] = "0",
            ["Preview:MaxSignalsPerMinute"] = "0",
            ["Preview:PerSymbolCooldownSeconds"] = "0",
            ["Preview:RequireBookValid"] = "true",
            ["Preview:RequireTapeRecent"] = "false",
            ["Preview:DiscordEnabled"] = "false",
            ["Preview:DedupWindowSeconds"] = dedupWindowSeconds.ToString(),
            ["Signals:HardGates:MaxSpoofScore"] = "1.0",
            ["Signals:HardGates:MinTapeAcceleration"] = "0.0",
            ["Signals:HardGates:MinWallPersistenceMs"] = "0"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private static DiscordNotificationService BuildDiscordService(IConfiguration config)
    {
        var settingsStore = new DiscordNotificationSettingsStore(config);
        var statusStore = new DiscordDeliveryStatusStore();
        var httpClientFactory = new StubHttpClientFactory();
        return new DiscordNotificationService(
            settingsStore,
            statusStore,
            httpClientFactory,
            NullLogger<DiscordNotificationService>.Instance);
    }

    private static OrderBookState BuildBook(string symbol, long nowMs, decimal bid, decimal ask)
    {
        var book = new OrderBookState(symbol);
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, bid, 200m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, ask, 200m, 0, nowMs));
        book.RecordTrade(nowMs - 5000, nowMs - 5000, (double)((bid + ask) / 2m), 100m);
        return book;
    }

    private static void ConfigureBuySnapshot(OrderFlowMetrics.MetricSnapshot snapshot)
    {
        snapshot.QueueImbalance = 3.0m;
        snapshot.BidWallAgeMs = 2000;
        snapshot.AskWallAgeMs = 0;
        snapshot.TapeAcceleration = 3.0m;
        snapshot.BidAbsorptionRate = 20m;
        snapshot.AskAbsorptionRate = 0m;
        snapshot.SpoofScore = 0.0m;
        snapshot.BidDepth4Level = 1000m;
        snapshot.AskDepth4Level = 100m;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
