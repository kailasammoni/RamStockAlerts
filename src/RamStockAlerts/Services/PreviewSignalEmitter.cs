using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Notifications;
using RamStockAlerts.Services.Signals;

namespace RamStockAlerts.Services;

public sealed class PreviewSignalEmitter
{
    private const int TapePresenceWindowMs = 3000;
    private readonly OrderFlowMetrics _metrics;
    private readonly OrderFlowSignalValidator _validator;
    private readonly ILogger<PreviewSignalEmitter> _logger;
    private readonly DiscordNotificationService _discordNotificationService;
    private readonly bool _enabled;
    private readonly decimal _minScore;
    private readonly int _maxSignalsPerMinute;
    private readonly int _perSymbolCooldownSeconds;
    private readonly bool _requireBookValid;
    private readonly bool _requireTapeRecent;
    private readonly SignalHelpers.TapeGateConfig _tapeGateConfig;
    private readonly bool _discordEnabled;
    private readonly string _discordChannelTag;
    private readonly int _dedupWindowSeconds;
    private readonly Queue<DateTimeOffset> _recentSignals = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSymbolEmit = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentPreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dedupLock = new();
    private readonly object _rateLock = new();
    private DateTimeOffset _lastPreviewCacheCleanup = DateTimeOffset.MinValue;

    public PreviewSignalEmitter(
        IConfiguration configuration,
        OrderFlowMetrics metrics,
        OrderFlowSignalValidator validator,
        DiscordNotificationService discordNotificationService,
        ILogger<PreviewSignalEmitter> logger)
    {
        _metrics = metrics;
        _validator = validator;
        _discordNotificationService = discordNotificationService;
        _logger = logger;

        var previewEnabled = configuration.GetValue("Preview:Enabled", true);

        _enabled = previewEnabled;
        _minScore = configuration.GetValue("Preview:MinScore", 5.0m);
        _maxSignalsPerMinute = configuration.GetValue("Preview:MaxSignalsPerMinute", 30);
        _perSymbolCooldownSeconds = configuration.GetValue("Preview:PerSymbolCooldownSeconds", 0);
        _requireBookValid = configuration.GetValue("Preview:RequireBookValid", true);
        _requireTapeRecent = configuration.GetValue("Preview:RequireTapeRecent", false);
        _tapeGateConfig = SignalHelpers.ReadTapeGateConfig(configuration);
        _discordEnabled = configuration.GetValue("Preview:DiscordEnabled", true);
        _discordChannelTag = configuration.GetValue<string>("Preview:DiscordChannelTag") ?? "PREVIEW";
        _dedupWindowSeconds = configuration.GetValue("Preview:DedupWindowSeconds", 0);

        if (_enabled)
        {
            _logger.LogInformation(
                "[Preview] PreviewSignalEmitter enabled (MinScore={MinScore}, MaxSignalsPerMinute={MaxSignalsPerMinute}, DedupWindowSeconds={DedupWindowSeconds})",
                _minScore,
                _maxSignalsPerMinute,
                _dedupWindowSeconds);
        }
    }

    public async Task ProcessSnapshotAsync(OrderBookState book, long nowMs)
    {
        if (!_enabled)
        {
            return;
        }

        if (_requireBookValid && !book.IsBookValid(out _, nowMs))
        {
            return;
        }

        if (_requireTapeRecent && !SignalHelpers.HasRecentTape(book, nowMs, _tapeGateConfig))
        {
            return;
        }

        var decision = _validator.EvaluateDecision(book, nowMs);
        if (!decision.HasCandidate || decision.Signal == null)
        {
            return;
        }

        var score = decision.Signal.Confidence;
        if (score < _minScore)
        {
            return;
        }

        if (!TryBuildBlueprint(book, decision.Direction, out var entry, out var stop, out var target, out _))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (IsRateLimited(book.Symbol, now))
        {
            _logger.LogInformation("[PREVIEW] suppressed rateLimit symbol={Symbol}", book.Symbol);
            return;
        }

        var signature = BuildPreviewSignature(book.Symbol, decision.Direction, entry, stop, target);
        if (ShouldSuppressDuplicate(signature, now, out var lastEmit))
        {
            _logger.LogInformation(
                "[PREVIEW] suppressed duplicate symbol={Symbol} direction={Direction} entry={Entry} stop={Stop} target={Target} lastEmit={LastEmit:O}",
                book.Symbol,
                decision.Direction,
                entry,
                stop,
                target,
                lastEmit);
            return;
        }

        RecordSignalEmit(book.Symbol, now);

        _logger.LogInformation(
            "[PREVIEW] symbol={Symbol} score={Score} entry={Entry} stop={Stop} target={Target}",
            book.Symbol,
            score,
            entry,
            stop,
            target);

        if (_discordEnabled)
        {
            try
            {
                await SendDiscordAsync(book, decision, score, entry, stop, target, nowMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PREVIEW] Discord notification failed for {Symbol}", book.Symbol);
            }
        }
    }

    private bool IsRateLimited(string symbol, DateTimeOffset now)
    {
        if (_perSymbolCooldownSeconds > 0 &&
            _lastSymbolEmit.TryGetValue(symbol, out var lastEmit) &&
            (now - lastEmit).TotalSeconds < _perSymbolCooldownSeconds)
        {
            return true;
        }

        if (_maxSignalsPerMinute > 0)
        {
            lock (_rateLock)
            {
                var windowStart = now.AddSeconds(-60);
                while (_recentSignals.Count > 0 && _recentSignals.Peek() < windowStart)
                {
                    _recentSignals.Dequeue();
                }

                if (_recentSignals.Count >= _maxSignalsPerMinute)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RecordSignalEmit(string symbol, DateTimeOffset now)
    {
        if (_maxSignalsPerMinute > 0)
        {
            lock (_rateLock)
            {
                _recentSignals.Enqueue(now);
            }
        }

        _lastSymbolEmit[symbol] = now;
    }

    private bool ShouldSuppressDuplicate(string signature, DateTimeOffset now, out DateTimeOffset lastEmit)
    {
        lastEmit = DateTimeOffset.MinValue;

        if (_dedupWindowSeconds <= 0)
        {
            return false;
        }

        var windowStart = now.AddSeconds(-_dedupWindowSeconds);
        PrunePreviewCache(windowStart, now);

        if (_recentPreviewCache.TryGetValue(signature, out lastEmit) && lastEmit >= windowStart)
        {
            return true;
        }

        _recentPreviewCache[signature] = now;
        return false;
    }

    private void PrunePreviewCache(DateTimeOffset windowStart, DateTimeOffset now)
    {
        if (_dedupWindowSeconds <= 0)
        {
            return;
        }

        lock (_dedupLock)
        {
            if (now - _lastPreviewCacheCleanup < TimeSpan.FromSeconds(_dedupWindowSeconds))
            {
                return;
            }

            foreach (var entry in _recentPreviewCache)
            {
                if (entry.Value < windowStart)
                {
                    _recentPreviewCache.TryRemove(entry.Key, out _);
                }
            }

            _lastPreviewCacheCleanup = now;
        }
    }

    private static string BuildPreviewSignature(
        string symbol,
        string? direction,
        decimal entry,
        decimal stop,
        decimal target)
    {
        var side = string.IsNullOrWhiteSpace(direction) ? "UNKNOWN" : direction.Trim().ToUpperInvariant();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{symbol}|{side}|{entry:F4}|{stop:F4}|{target:F4}");
    }

    private async Task SendDiscordAsync(
        OrderBookState book,
        OrderFlowSignalValidator.OrderFlowSignalDecision decision,
        int score,
        decimal entry,
        decimal stop,
        decimal target,
        long nowMs)
    {
        var snapshot = decision.Snapshot ?? _metrics.GetLatestSnapshot(book.Symbol);
        var spread = snapshot?.Spread ?? book.Spread;
        var bidAskRatio = snapshot?.AskDepth4Level > 0m
            ? snapshot.BidDepth4Level / snapshot.AskDepth4Level
            : 0m;

        var tapeStats = BuildTapeStats(book, nowMs);
        var intendedAction = BuildIntendedAction(decision.Direction);
        var details = BuildDetails(
            decision.Direction,
            score,
            entry,
            stop,
            target,
            spread,
            bidAskRatio,
            tapeStats.Velocity);

        var options = new DiscordNotificationSendOptions
        {
            EnabledOverride = _discordEnabled ? null : false,
            ChannelTagOverride = _discordChannelTag
        };

        await _discordNotificationService.SendAlertAsync(
            book.Symbol,
            "Liquidity Setup",
            DateTimeOffset.UtcNow,
            DiscordNotificationMode.Preview,
            intendedAction,
            details,
            options);
    }

    private static Dictionary<string, string> BuildDetails(
        string? direction,
        int score,
        decimal entry,
        decimal stop,
        decimal target,
        decimal spread,
        decimal bidAskRatio,
        decimal? tapeVelocity)
    {
        var details = new Dictionary<string, string>
        {
            ["Score"] = score.ToString("F0"),
            ["Entry"] = entry.ToString("F2"),
            ["Stop"] = stop.ToString("F2"),
            ["Target"] = target.ToString("F2"),
            ["Spread"] = spread.ToString("F4"),
            ["BidAskRatio"] = bidAskRatio.ToString("F2"),
            ["TapeVelocityProxy"] = tapeVelocity.HasValue ? tapeVelocity.Value.ToString("F2") : "N/A"
        };

        if (!string.IsNullOrWhiteSpace(direction))
        {
            details["Side"] = string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase) ? "Long" : "Short";
        }

        return details;
    }

    private static string? BuildIntendedAction(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return null;
        }

        return string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase) ? "Long" : "Short";
    }

    private sealed record TapeStats(decimal? Velocity);

    private static TapeStats BuildTapeStats(OrderBookState book, long nowMs)
    {
        var windowStart = nowMs - TapePresenceWindowMs;
        var trades = book.RecentTrades.Where(t => t.TimestampMs >= windowStart).ToList();
        if (trades.Count == 0)
        {
            return new TapeStats(null);
        }

        var velocity = TapePresenceWindowMs > 0
            ? trades.Count / (decimal)(TapePresenceWindowMs / 1000m)
            : 0m;

        return new TapeStats(velocity);
    }

    private static bool TryBuildBlueprint(
        OrderBookState book,
        string? direction,
        out decimal entry,
        out decimal stop,
        out decimal target,
        out string? rejectionReason)
    {
        entry = 0m;
        stop = 0m;
        target = 0m;
        rejectionReason = null;

        var spread = book.Spread;
        if (spread <= 0m)
        {
            rejectionReason = "InvalidSpread";
            return false;
        }

        var isBuy = string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase);
        if (isBuy)
        {
            entry = book.BestAsk;
            if (entry <= 0m)
            {
                rejectionReason = "InvalidAsk";
                return false;
            }

            stop = entry - (spread * 4m);
            target = entry + (spread * 8m);
        }
        else
        {
            entry = book.BestBid;
            if (entry <= 0m)
            {
                rejectionReason = "InvalidBid";
                return false;
            }

            stop = entry + (spread * 4m);
            target = entry - (spread * 8m);
        }

        return true;
    }
}
