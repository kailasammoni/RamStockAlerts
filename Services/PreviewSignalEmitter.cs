using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public sealed class PreviewSignalEmitter
{
    private const int TapePresenceWindowMs = 3000;
    private readonly OrderFlowMetrics _metrics;
    private readonly OrderFlowSignalValidator _validator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PreviewSignalEmitter> _logger;
    private readonly bool _enabled;
    private readonly decimal _minScore;
    private readonly int _maxSignalsPerMinute;
    private readonly int _perSymbolCooldownSeconds;
    private readonly bool _requireBookValid;
    private readonly bool _requireTapeRecent;
    private readonly ShadowTradingHelpers.TapeGateConfig _tapeGateConfig;
    private readonly bool _discordEnabled;
    private readonly string _discordChannelTag;
    private readonly string? _webhookUrl;
    private readonly Queue<DateTimeOffset> _recentSignals = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSymbolEmit = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rateLock = new();

    public PreviewSignalEmitter(
        IConfiguration configuration,
        OrderFlowMetrics metrics,
        OrderFlowSignalValidator validator,
        IHttpClientFactory httpClientFactory,
        ILogger<PreviewSignalEmitter> logger)
    {
        _metrics = metrics;
        _validator = validator;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var tradingMode = configuration.GetValue<string>("TradingMode") ?? string.Empty;
        var previewEnabled = configuration.GetValue("Preview:Enabled", true);

        _enabled = string.Equals(tradingMode, "Preview", StringComparison.OrdinalIgnoreCase) && previewEnabled;
        _minScore = configuration.GetValue("Preview:MinScore", 5.0m);
        _maxSignalsPerMinute = configuration.GetValue("Preview:MaxSignalsPerMinute", 30);
        _perSymbolCooldownSeconds = configuration.GetValue("Preview:PerSymbolCooldownSeconds", 0);
        _requireBookValid = configuration.GetValue("Preview:RequireBookValid", true);
        _requireTapeRecent = configuration.GetValue("Preview:RequireTapeRecent", false);
        _tapeGateConfig = ShadowTradingHelpers.ReadTapeGateConfig(configuration);
        _discordEnabled = configuration.GetValue("Preview:DiscordEnabled", true);
        _discordChannelTag = configuration.GetValue<string>("Preview:DiscordChannelTag") ?? "PREVIEW";
        _webhookUrl = configuration["Discord:WebhookUrl"];

        if (_enabled)
        {
            _logger.LogInformation(
                "[Preview] PreviewSignalEmitter enabled (MinScore={MinScore}, MaxSignalsPerMinute={MaxSignalsPerMinute})",
                _minScore,
                _maxSignalsPerMinute);
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

        if (_requireTapeRecent && !ShadowTradingHelpers.HasRecentTape(book, nowMs, _tapeGateConfig))
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

        if (IsRateLimited(book.Symbol))
        {
            _logger.LogInformation("[PREVIEW] suppressed rateLimit symbol={Symbol}", book.Symbol);
            return;
        }

        _logger.LogInformation(
            "[PREVIEW] symbol={Symbol} score={Score} entry={Entry} stop={Stop} target={Target}",
            book.Symbol,
            score,
            entry,
            stop,
            target);

        if (_discordEnabled && !string.IsNullOrWhiteSpace(_webhookUrl))
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

    private bool IsRateLimited(string symbol)
    {
        var now = DateTimeOffset.UtcNow;

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

                _recentSignals.Enqueue(now);
            }
        }

        _lastSymbolEmit[symbol] = now;
        return false;
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
        var fields = BuildFields(
            book.Symbol,
            decision.Direction,
            score,
            entry,
            stop,
            target,
            spread,
            bidAskRatio,
            tapeStats.Velocity);

        var embed = new
        {
            title = $"[{_discordChannelTag}] Liquidity Setup",
            fields,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsync(_webhookUrl!, content);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "[PREVIEW] Discord webhook failed: {StatusCode} - {Error}",
                response.StatusCode,
                errorContent);
        }
    }

    private static object[] BuildFields(
        string symbol,
        string? direction,
        int score,
        decimal entry,
        decimal stop,
        decimal target,
        decimal spread,
        decimal bidAskRatio,
        decimal? tapeVelocity)
    {
        var fields = new List<object>
        {
            new { name = "Symbol", value = symbol, inline = true }
        };

        if (!string.IsNullOrWhiteSpace(direction))
        {
            var side = string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase) ? "Long" : "Short";
            fields.Add(new { name = "Side", value = side, inline = true });
        }

        fields.AddRange(new[]
        {
            new { name = "Score", value = score.ToString("F0"), inline = true },
            new { name = "Entry", value = entry.ToString("F2"), inline = true },
            new { name = "Stop", value = stop.ToString("F2"), inline = true },
            new { name = "Target", value = target.ToString("F2"), inline = true },
            new { name = "Spread", value = spread.ToString("F4"), inline = true },
            new { name = "BidAskRatio", value = bidAskRatio.ToString("F2"), inline = true },
            new
            {
                name = "TapeVelocityProxy",
                value = tapeVelocity.HasValue ? tapeVelocity.Value.ToString("F2") : "N/A",
                inline = true
            },
            new { name = "Timestamp", value = DateTimeOffset.UtcNow.ToString("u"), inline = true }
        });

        return fields.ToArray();
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
