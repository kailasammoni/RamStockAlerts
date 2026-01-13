using System.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Universe;

namespace RamStockAlerts.Services;

public sealed class ShadowJournalHeartbeatService : BackgroundService
{
    private readonly ShadowTradeJournal _journal;
    private readonly MarketDataSubscriptionManager _subscriptionManager;
    private readonly UniverseService _universeService;
    private readonly OrderFlowMetrics _metrics;
    private readonly ILogger<ShadowJournalHeartbeatService> _logger;
    private readonly bool _enabled;

    public ShadowJournalHeartbeatService(
        IConfiguration configuration,
        ShadowTradeJournal journal,
        MarketDataSubscriptionManager subscriptionManager,
        UniverseService universeService,
        OrderFlowMetrics metrics,
        ILogger<ShadowJournalHeartbeatService> logger)
    {
        _journal = journal;
        _subscriptionManager = subscriptionManager;
        _universeService = universeService;
        _metrics = metrics;
        _logger = logger;

        var tradingMode = configuration.GetValue<string>("TradingMode") ?? string.Empty;
        _enabled = string.Equals(tradingMode, "Shadow", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        _logger.LogInformation("[ShadowHeartbeat] Heartbeat service running every 60s.");

        var interval = TimeSpan.FromSeconds(60);
        while (!stoppingToken.IsCancellationRequested)
        {
            await WriteHeartbeatAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken stoppingToken)
    {
        try
        {
            var universe = await _universeService.GetUniverseAsync(stoppingToken);
            var stats = _subscriptionManager.GetSubscriptionStats();
            var now = DateTime.UtcNow;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (depthAge, tapeAge, bookValidAny, tapeRecentAny) = GatherBookStats(nowMs);

            var entry = new ShadowTradeJournalEntry
            {
                EntryType = "Heartbeat",
                Decision = "Heartbeat",
                Accepted = false,
                TimestampUtc = now,
                TradingMode = "Shadow",
                UniverseCount = universe.Count,
                ActiveSubscriptionsCount = stats.TotalSubscriptions,
                DepthEnabledCount = stats.DepthEnabled,
                TickByTickEnabledCount = stats.TickByTickEnabled,
                DepthSubscribeAttempts = stats.DepthSubscribeAttempts,
                DepthSubscribeSuccess = stats.DepthSubscribeSuccess,
                DepthSubscribeFailures = stats.DepthSubscribeFailures,
                DepthSubscribeLastErrorCode = stats.LastDepthErrorCode,
                DepthSubscribeLastErrorMessage = stats.LastDepthErrorMessage,
                LastDepthUpdateAgeMs = depthAge,
                LastTapeUpdateAgeMs = tapeAge,
                IsBookValidAny = bookValidAny,
                TapeRecentAny = tapeRecentAny
            };

            if (!_journal.TryEnqueue(entry))
            {
                _logger.LogWarning("[ShadowHeartbeat] Heartbeat entry dropped (journal disabled).");
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[ShadowHeartbeat] Failed to record heartbeat.");
        }
    }

    private (long? DepthAge, long? TapeAge, bool BookValidAny, bool TapeRecentAny) GatherBookStats(long nowMs)
    {
        long? minDepthAge = null;
        long? minTapeAge = null;
        var bookValidAny = false;
        var tapeRecentAny = false;

        foreach (var symbol in _metrics.GetSubscribedSymbols())
        {
            var book = _metrics.GetOrderBookSnapshot(symbol);
            if (book is null)
            {
                continue;
            }

            if (book.IsBookValid(out _, nowMs))
            {
                bookValidAny = true;
            }

            if (book.LastDepthUpdateUtcMs > 0)
            {
                var depthAge = nowMs - book.LastDepthUpdateUtcMs;
                minDepthAge = minDepthAge.HasValue ? Math.Min(minDepthAge.Value, depthAge) : depthAge;
            }

            if (book.RecentTrades.Count > 0)
            {
                var lastTrade = book.RecentTrades.Last();
                var tapeAge = nowMs - lastTrade.TimestampMs;
                minTapeAge = minTapeAge.HasValue ? Math.Min(minTapeAge.Value, tapeAge) : tapeAge;
            }

            if (ShadowTradingHelpers.HasRecentTape(book, nowMs))
            {
                tapeRecentAny = true;
            }
        }

        return (minDepthAge, minTapeAge, bookValidAny, tapeRecentAny);
    }
}
