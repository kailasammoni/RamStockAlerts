using System.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Universe;
using RamStockAlerts.Feeds;

namespace RamStockAlerts.Services.Signals;

public sealed class TradeJournalHeartbeatService : BackgroundService
{
    private readonly TradeJournal _journal;
    private readonly MarketDataSubscriptionManager _subscriptionManager;
    private readonly UniverseService _universeService;
    private readonly OrderFlowMetrics _metrics;
    private readonly ILogger<TradeJournalHeartbeatService> _logger;
    private readonly IBkrMarketDataClient _ibkrClient;
    private readonly SignalCoordinator _coordinator;
    private readonly SignalHelpers.TapeGateConfig _tapeGateConfig;
    private readonly double _disconnectThresholdSeconds;
    private readonly TimeSpan _disconnectCheckInterval;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan HeartbeatDelayTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UniverseRefreshWarningThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CoordinatorInactivityThreshold = TimeSpan.FromMinutes(5);

    public TradeJournalHeartbeatService(
        IConfiguration configuration,
        TradeJournal journal,
        MarketDataSubscriptionManager subscriptionManager,
        UniverseService universeService,
        OrderFlowMetrics metrics,
        IBkrMarketDataClient ibkrClient,
        SignalCoordinator signalCoordinator,
        ILogger<TradeJournalHeartbeatService> logger)
    {
        _journal = journal;
        _subscriptionManager = subscriptionManager;
        _universeService = universeService;
        _metrics = metrics;
        _ibkrClient = ibkrClient;
        _coordinator = signalCoordinator;
        _logger = logger;
        _tapeGateConfig = SignalHelpers.ReadTapeGateConfig(configuration);
        _disconnectThresholdSeconds = configuration.GetValue("IBKR:DisconnectThresholdSeconds", 30.0);
        _disconnectCheckInterval = TimeSpan.FromSeconds(configuration.GetValue("IBKR:DisconnectCheckIntervalSeconds", 10.0));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug(
            "[Heartbeat] Heartbeat service running. Heartbeat: 60s, Disconnect check: {CheckInterval}s (threshold: {Threshold}s)",
            _disconnectCheckInterval.TotalSeconds,
            _disconnectThresholdSeconds);

        // Start disconnect monitoring task
        var disconnectMonitorTask = Task.Run(() => MonitorIbkrConnectionAsync(stoppingToken), stoppingToken);

        var interval = TimeSpan.FromSeconds(60);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastHeartbeatUtc != DateTimeOffset.MinValue)
            {
                var gap = now - _lastHeartbeatUtc;
                var expectedThreshold = interval + HeartbeatDelayTolerance;
                if (gap > expectedThreshold)
                {
                    _logger.LogWarning(
                        "[Heartbeat] Heartbeat delay detected: last heartbeat was {Gap} ago (expected about {Expected}). Possible application stall.",
                        gap,
                        expectedThreshold);
                }
            }

            await WriteHeartbeatAsync(stoppingToken);
            _lastHeartbeatUtc = DateTimeOffset.UtcNow;
            CheckUniverseRefreshLiveness(_lastHeartbeatUtc);
            CheckCoordinatorLiveness(_lastHeartbeatUtc);
            await Task.Delay(interval, stoppingToken);
        }

        await disconnectMonitorTask;
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

            var entry = new TradeJournalEntry
            {
                SchemaVersion = 2,
                DecisionId = Guid.NewGuid(),
                SessionId = _journal.SessionId,
                Source = "System",
                EntryType = "Heartbeat",
                MarketTimestampUtc = now,
                DecisionTimestampUtc = now,
                TradingMode = "Signals",
                DecisionOutcome = "Cancelled",
                DecisionTrace = new List<string> { "Heartbeat" },
                DataQualityFlags = new List<string> { "HeartbeatNoDecision" },
                SystemMetrics = new TradeJournalEntry.SystemMetricsSnapshot
                {
                    UniverseCount = universe.Count,
                    ActiveSubscriptionsCount = stats.TotalSubscriptions,
                    DepthEnabledCount = stats.DepthEnabled,
                    TickByTickEnabledCount = stats.TickByTickEnabled,
                    DepthSubscribeAttempts = stats.DepthSubscribeAttempts,
                    DepthSubscribeSuccess = stats.DepthSubscribeUpdateReceived,
                    DepthSubscribeFailures = stats.DepthSubscribeErrors,
                    DepthSubscribeUpdateReceived = stats.DepthSubscribeUpdateReceived,
                    DepthSubscribeErrors = stats.DepthSubscribeErrors,
                    DepthSubscribeErrorsByCode = stats.DepthSubscribeErrorsByCode.ToDictionary(pair => pair.Key, pair => pair.Value),
                    DepthSubscribeLastErrorCode = stats.LastDepthErrorCode,
                    DepthSubscribeLastErrorMessage = stats.LastDepthErrorMessage,
                    LastDepthUpdateAgeMs = depthAge,
                    LastTapeUpdateAgeMs = tapeAge,
                    IsBookValidAny = bookValidAny,
                    TapeRecentAny = tapeRecentAny
                }
            };

            if (!_journal.TryEnqueue(entry))
            {
                _logger.LogWarning("[Heartbeat] Heartbeat entry dropped (journal unavailable).");
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[Heartbeat] Failed to record heartbeat.");
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

            if (SignalHelpers.HasRecentTape(book, nowMs, _tapeGateConfig))
            {
                tapeRecentAny = true;
            }
        }

        return (minDepthAge, minTapeAge, bookValidAny, tapeRecentAny);
    }

    private void CheckUniverseRefreshLiveness(DateTimeOffset now)
    {
        var lastRefresh = _universeService.LastRefreshUtc;
        if (!lastRefresh.HasValue)
        {
            return;
        }

        var age = now - lastRefresh.Value;
        if (age > UniverseRefreshWarningThreshold)
        {
            _logger.LogWarning(
                "[Heartbeat] Universe refresh overdue: last refresh was {Age} ago (at {LastRefresh:O}).",
                age,
                lastRefresh.Value);
        }
    }

    private void CheckCoordinatorLiveness(DateTimeOffset now)
    {
        var lastProcessed = _coordinator.LastSnapshotProcessedUtc;
        if (!lastProcessed.HasValue)
        {
            return;
        }

        var age = now - lastProcessed.Value;
        if (age > CoordinatorInactivityThreshold)
        {
            _logger.LogWarning(
                "[Heartbeat] Coordinator inactivity: no snapshots processed for {Age} (last at {LastProcessed:O}).",
                age,
                lastProcessed.Value);
        }
    }

    private async Task MonitorIbkrConnectionAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_disconnectCheckInterval, stoppingToken);
                await CheckIbkrHealthAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[Heartbeat] IBKR health check failed.");
            }
        }
    }

    private async Task CheckIbkrHealthAsync(CancellationToken stoppingToken)
    {
        // Check if we're connected
        var isConnected = _ibkrClient.IsConnected();
        if (!isConnected)
        {
            _logger.LogWarning("[Heartbeat] IBKR not connected. Triggering reconnect.");
            await TriggerReconnectAsync(stoppingToken);
            return;
        }

        // Skip staleness check outside market hours
        if (!IsMarketHours())
        {
            return;
        }

        // Check last tick age
        var lastTickAge = _ibkrClient.GetLastTickAgeSeconds();
        if (lastTickAge is null)
        {
            // No ticks received yet - normal on startup
            return;
        }

        if (lastTickAge.Value > _disconnectThresholdSeconds)
        {
            _logger.LogWarning(
                "[Heartbeat] IBKR data stale: last tick age={AgeSeconds:F1}s, threshold={Threshold}s. Triggering reconnect.",
                lastTickAge.Value,
                _disconnectThresholdSeconds);
            await TriggerReconnectAsync(stoppingToken);
        }
    }

    private bool IsMarketHours()
    {
        // US market hours: 9:30 AM - 4:00 PM ET (14:30-21:00 UTC)
        // Check Mon-Fri only
        var nowUtc = DateTime.UtcNow;
        if (nowUtc.DayOfWeek == DayOfWeek.Saturday || nowUtc.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        var hour = nowUtc.Hour;
        var minute = nowUtc.Minute;
        var totalMinutes = hour * 60 + minute;
        var marketOpenMinutes = 14 * 60 + 30;  // 14:30 UTC
        var marketCloseMinutes = 21 * 60;       // 21:00 UTC

        return totalMinutes >= marketOpenMinutes && totalMinutes < marketCloseMinutes;
    }

    private async Task TriggerReconnectAsync(CancellationToken stoppingToken)
    {
        if (_ibkrClient.IsReconnecting())
        {
            _logger.LogDebug("[Heartbeat] Reconnect already in progress.");
            return;
        }

        _logger.LogWarning("[Heartbeat] Triggering IBKR reconnect sequence...");

        // Disconnect
        var disconnected = await _ibkrClient.DisconnectAsync(stoppingToken);
        if (!disconnected)
        {
            _logger.LogError("[Heartbeat] Disconnect failed.");
            return;
        }

        // Reconnect with exponential backoff
        var connected = await _ibkrClient.ConnectAsync(stoppingToken);
        if (!connected)
        {
            _logger.LogError("[Heartbeat] Reconnect failed after max attempts.");
            return;
        }

        _logger.LogInformation("[Heartbeat] Reconnect succeeded. Re-subscribing active symbols...");

        // Re-subscribe to active symbols
        var resubscribed = await _ibkrClient.ReSubscribeActiveSymbolsAsync(stoppingToken);
        _logger.LogInformation("[Heartbeat] Re-subscription complete: {Count} symbols", resubscribed);
    }
}
