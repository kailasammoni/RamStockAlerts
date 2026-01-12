using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Hosted service that replays recorded IBKR depth/tape logs deterministically into in-memory trackers.
/// </summary>
public sealed class IbkrReplayHostedService : BackgroundService
{
    private readonly ILogger<IbkrReplayHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public IbkrReplayHostedService(
        ILogger<IbkrReplayHostedService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var symbol = ResolveSymbol();
            var depthPath = ResolveLatestPath("Replay:DepthFile", $"logs{Path.DirectorySeparatorChar}ibkr-depth-{symbol}-*.jsonl");
            var tapePath = ResolveLatestPath("Replay:TapeFile", $"logs{Path.DirectorySeparatorChar}ibkr-tape-{symbol}-*.jsonl");

            if (depthPath == null || tapePath == null)
            {
                _logger.LogError("Replay files missing for {Symbol}. Depth={Depth} Tape={Tape}", symbol, depthPath, tapePath);
                return;
            }

            var events = LoadEvents(depthPath, tapePath);
            if (events.Count == 0)
            {
                _logger.LogError("No replayable events found for {Symbol}", symbol);
                return;
            }

            var orderBook = new OrderBookState(symbol);
            var bidWallTracker = new BidWallTracker();
            var tapeVelocityTracker = new TapeVelocityTracker(TimeSpan.FromSeconds(3));
            var velocityWindow = new Queue<TradePrint>();

            var summaryPath = Path.Combine("logs", $"replay-summary-{symbol}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");

            _logger.LogInformation("Starting deterministic replay for {Symbol}. Depth={DepthFile} Tape={TapeFile}", symbol, depthPath, tapePath);

            await RunReplay(events, orderBook, bidWallTracker, tapeVelocityTracker, velocityWindow, summaryPath, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during replay");
            Environment.ExitCode = 1;
        }
    }

    private async Task RunReplay(
        List<ReplayEvent> events,
        OrderBookState orderBook,
        BidWallTracker bidWallTracker,
        TapeVelocityTracker tapeVelocityTracker,
        Queue<TradePrint> velocityWindow,
        string summaryPath,
        CancellationToken cancellationToken)
    {
        var ordered = events
            .OrderBy(e => e.TimestampUtc)
            .ThenBy(e => e.Kind)
            .ToList();

        var currentSecond = ordered[0].TimestampUtc.ToUnixTimeSeconds();
        var endSecond = ordered[^1].TimestampUtc.ToUnixTimeSeconds();
        var lastTapeSecond = (long?)null;
        decimal lastBestBid = orderBook.BestBid;
        decimal lastBestAsk = orderBook.BestAsk;

        // Replay counters for safety validation
        long invalidBookCount = 0;
        long crossedBookCount = 0;
        long exceptionsCount = 0;
        long validSeconds = 0;
        long invalidSeconds = 0;
        long crossedSeconds = 0;
        long exceptionsSeconds = 0;
        long totalSecondsReplayed = 0;
        bool tapePresentThisSecond = false;
        bool exceptionThisSecond = false;

        await using var writer = new StreamWriter(
            new FileStream(summaryPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        void FinalizeSecond(long second)
        {
            var nowMs = (second * 1000) + 999;
            var isValidThisSecond = orderBook.IsBookValid(out _, nowMs) && tapePresentThisSecond;

            if (isValidThisSecond)
            {
                validSeconds++;
            }
            else
            {
                invalidSeconds++;
            }

            if (orderBook.BestBid > 0m && orderBook.BestAsk > 0m && orderBook.BestBid >= orderBook.BestAsk)
            {
                crossedSeconds++;
            }

            if (exceptionThisSecond)
            {
                exceptionsSeconds++;
            }

            EmitSummary(
                second,
                orderBook,
                tapeVelocityTracker,
                velocityWindow,
                writer,
                invalidBookCount,
                crossedBookCount,
                exceptionsCount,
                validSeconds,
                invalidSeconds,
                crossedSeconds,
                exceptionsSeconds);

            totalSecondsReplayed++;
        }

        foreach (var evt in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var eventSecond = evt.TimestampUtc.ToUnixTimeSeconds();

            while (eventSecond > currentSecond)
            {
                FinalizeSecond(currentSecond);
                currentSecond++;
                tapePresentThisSecond = false;
                exceptionThisSecond = false;
            }

            if (evt.Kind == ReplayEventKind.Depth)
            {
                if (!evt.DepthPayload.HasValue)
                {
                    Fail("Missing depth payload", evt);
                    return;
                }

                var depthPayload = evt.DepthPayload.Value;

                if (depthPayload.Size < 0m)
                {
                    Fail("Negative depth size", evt);
                    return;
                }

                var preBestBid = orderBook.BestBid;
                var preBestAsk = orderBook.BestAsk;

                var depthUpdate = new DepthUpdate(
                    evt.Symbol,
                    depthPayload.Side,
                    depthPayload.Operation,
                    depthPayload.Price,
                    depthPayload.Size,
                    depthPayload.Position,
                    evt.TimestampUtc.ToUnixTimeMilliseconds());

                // Fix 4: Wrap depth application in try-catch to prevent one bad event from failing replay
                try
                {
                    orderBook.ApplyDepthUpdate(depthUpdate);
                    
                    // Fix 3: Only apply to bidWallTracker if book is valid
                    var depthTimestampMs = evt.TimestampUtc.ToUnixTimeMilliseconds();
                    if (orderBook.IsBookValid(out var depthValidityReason, depthTimestampMs))
                    {
                        bidWallTracker.ApplyDepthUpdate(depthUpdate);
                    }
                    else
                    {
                        _logger.LogDebug("[Replay] Skipped bidWallTracker update: {Reason}", depthValidityReason);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Replay] Error applying depth update at {Timestamp}", evt.TimestampUtc);
                    exceptionsCount++;
                    exceptionThisSecond = true;
                    // Continue with next event
                    continue;
                }

                var postBestBid = orderBook.BestBid;
                var postBestAsk = orderBook.BestAsk;

                if (!HasTapeThisSecond(lastTapeSecond, eventSecond) && JumpExceeded(preBestBid, postBestBid))
                {
                    Fail("Best bid jump >5% without tape", evt);
                    return;
                }

                if (!HasTapeThisSecond(lastTapeSecond, eventSecond) && JumpExceeded(preBestAsk, postBestAsk))
                {
                    Fail("Best ask jump >5% without tape", evt);
                    return;
                }

                lastBestBid = postBestBid;
                lastBestAsk = postBestAsk;
            }
            else if (evt.Kind == ReplayEventKind.Tape)
            {
                if (!evt.TapePayload.HasValue)
                {
                    Fail("Missing tape payload", evt);
                    return;
                }

                var tapePayload = evt.TapePayload.Value;

                if (tapePayload.Size <= 0m)
                {
                    Fail("Non-positive tape size", evt);
                    return;
                }

                orderBook.RecordTrade(evt.TimestampUtc.ToUnixTimeMilliseconds(), (double)tapePayload.Price, tapePayload.Size);
                tapeVelocityTracker.AddTrade(evt.TimestampUtc.ToUnixTimeMilliseconds(), (double)tapePayload.Price, tapePayload.Size);
                velocityWindow.Enqueue(tapePayload);
                PruneVelocityWindow(velocityWindow, eventSecond, tapeVelocityTracker.Window);
                lastTapeSecond = eventSecond;
                tapePresentThisSecond = true;
            }

            // Check book validity after each event
            var nowMs = evt.TimestampUtc.ToUnixTimeMilliseconds();
            if (!orderBook.IsBookValid(out var validityReason, nowMs))
            {
                invalidBookCount++;

                // Hard gate: fail on crossed book or exceptions
                if (validityReason == "CrossedBook")
                {
                    crossedBookCount++;
                    Fail($"Book invalid: {validityReason}", evt);
                    return;
                }
            }
        }

        FinalizeSecond(currentSecond);

        // Emit final readiness checklist
        var validBookPercent = totalSecondsReplayed > 0
            ? (validSeconds / (decimal)totalSecondsReplayed) * 100m
            : 0m;

        var tapePresent = orderBook.RecentTrades.Count > 0;
        var replayPass = exceptionsSeconds == 0 && crossedSeconds == 0 && validBookPercent >= 95m;

        var readinessChecklist = new
        {
            timestampUtc = DateTimeOffset.UtcNow.ToString("o"),
            symbol = orderBook.Symbol,
            eventType = "ShadowTradingReadiness",
            validBookPercent = Math.Round(validBookPercent, 2),
            tapePresent = tapePresent,
            replayPass = replayPass,
            validSeconds = validSeconds,
            invalidSeconds = invalidSeconds,
            crossedSeconds = crossedSeconds,
            exceptionsSeconds = exceptionsSeconds,
            invalidBookCount = invalidBookCount,
            crossedBookCount = crossedBookCount,
            exceptionsCount = exceptionsCount,
            totalSecondsReplayed = totalSecondsReplayed,
            recommendation = replayPass ? "Ready for shadow trading" : "Not ready - review failures above"
        };

        var checklistLine = JsonSerializer.Serialize(readinessChecklist);
        writer.WriteLine(checklistLine);
        _logger.LogInformation("[Replay] READINESS_CHECKLIST: {Recommendation} ValidBook={ValidPercent}% Tape={Tape} Pass={Pass}",
            readinessChecklist.recommendation,
            readinessChecklist.validBookPercent,
            readinessChecklist.tapePresent,
            readinessChecklist.replayPass);

        if (!replayPass)
        {
            _logger.LogError("Shadow trading not ready. ReplayPass=false. InvalidSeconds={Invalid} CrossedSeconds={Crossed} ExceptionsSeconds={Exc}",
                invalidSeconds, crossedSeconds, exceptionsSeconds);
            Environment.ExitCode = 1;
        }
        else
        {
            _logger.LogInformation("Shadow trading readiness PASSED.");
        }
    }

    private void EmitSummary(
        long second,
        OrderBookState book,
        TapeVelocityTracker tapeVelocityTracker,
        Queue<TradePrint> velocityWindow,
        StreamWriter writer,
        long invalidBookCount,
        long crossedBookCount,
        long exceptionsCount,
        long validSeconds,
        long invalidSeconds,
        long crossedSeconds,
        long exceptionsSeconds)
    {
        var ts = DateTimeOffset.FromUnixTimeSeconds(second).ToUniversalTime();
        PruneVelocityWindow(velocityWindow, second, tapeVelocityTracker.Window);

        var summary = new
        {
            timestampUtc = ts.ToString("o", CultureInfo.InvariantCulture),
            symbol = book.Symbol,
            eventType = "ReplaySummary",
            bestBid = book.BestBid,
            bestAsk = book.BestAsk,
            spread = book.Spread,
            totalBidSizeTop5 = book.TotalBidSize(5),
            totalAskSizeTop5 = book.TotalAskSize(5),
            tapeVelocityTradesPerSec = velocityWindow.Count / (decimal)tapeVelocityTracker.Window.TotalSeconds,
            validSeconds = validSeconds,
            invalidSeconds = invalidSeconds,
            crossedSeconds = crossedSeconds,
            exceptionsSeconds = exceptionsSeconds,
            invalidBookCount = invalidBookCount,
            crossedBookCount = crossedBookCount,
            exceptionsCount = exceptionsCount
        };

        var line = JsonSerializer.Serialize(summary);
        writer.WriteLine(line);
        _logger.LogInformation("[Replay] {Timestamp} {Symbol} BB={BestBid} BA={BestAsk} Spr={Spread} Bid5={Bid5} Ask5={Ask5} TV={TV} Invalid={Invalid} Crossed={Crossed} Ex={Ex}",
            summary.timestampUtc,
            summary.symbol,
            summary.bestBid,
            summary.bestAsk,
            summary.spread,
            summary.totalBidSizeTop5,
            summary.totalAskSizeTop5,
            summary.tapeVelocityTradesPerSec,
            invalidBookCount,
            crossedBookCount,
            exceptionsCount);
    }

    private void PruneVelocityWindow(Queue<TradePrint> window, long currentSecond, TimeSpan span)
    {
        var cutoffMs = (currentSecond * 1000) - (long)span.TotalMilliseconds;
        while (window.Count > 0 && window.Peek().TimestampMs < cutoffMs)
        {
            window.Dequeue();
        }
    }

    private static bool HasTapeThisSecond(long? lastTapeSecond, long currentSecond) => lastTapeSecond.HasValue && lastTapeSecond.Value == currentSecond;

    private static bool JumpExceeded(decimal previous, decimal current)
    {
        if (previous <= 0m || current <= 0m)
        {
            return false;
        }

        var change = Math.Abs((current - previous) / previous);
        return change > 0.05m;
    }

    private List<ReplayEvent> LoadEvents(string depthPath, string tapePath)
    {
        var result = new List<ReplayEvent>();
        result.AddRange(LoadEventsFromJsonl(depthPath));
        result.AddRange(LoadEventsFromJsonl(tapePath));
        return result;
    }

    internal IEnumerable<ReplayEvent> LoadEventsFromJsonl(string path)
    {
        var events = new List<ReplayEvent>();
        if (!File.Exists(path))
        {
            _logger.LogError("Replay file not found: {Path}", path);
            return events;
        }

        int total = 0;
        int skipped = 0;

        foreach (var line in File.ReadLines(path))
        {
            total++;

            if (string.IsNullOrWhiteSpace(line))
            {
                skipped++;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("eventType", out var eventTypeProp))
                {
                    LogSkip(path, total, "missing eventType");
                    skipped++;
                    continue;
                }

                var eventType = eventTypeProp.GetString();
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    LogSkip(path, total, "empty eventType");
                    skipped++;
                    continue;
                }

                if (!root.TryGetProperty("timestampUtc", out var tsProp) || tsProp.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
                {
                    LogSkip(path, total, "invalid timestampUtc");
                    skipped++;
                    continue;
                }

                if (!root.TryGetProperty("symbol", out var symProp) || symProp.ValueKind != JsonValueKind.String)
                {
                    LogSkip(path, total, "missing symbol");
                    skipped++;
                    continue;
                }

                var symbol = symProp.GetString() ?? string.Empty;

                if (string.Equals(eventType, "Depth", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetDepth(root, out var depth))
                    {
                        LogSkip(path, total, "invalid depth payload");
                        skipped++;
                        continue;
                    }

                    events.Add(new ReplayEvent(ts, symbol, eventType, ReplayEventKind.Depth, depth, null));
                }
                else if (string.Equals(eventType, "Tape", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetTape(root, ts, out var tape))
                    {
                        LogSkip(path, total, "invalid tape payload");
                        skipped++;
                        continue;
                    }

                    events.Add(new ReplayEvent(ts, symbol, eventType, ReplayEventKind.Tape, null, tape));
                }
                else
                {
                    LogSkip(path, total, "unknown eventType");
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse line {Line} in {Path}", total, path);
                skipped++;
            }
        }

        if (total > 0 && skipped > total * 0.001)
        {
            _logger.LogError("Replay file {Path} skipped too many lines ({Skipped}/{Total}); failing for suspected corruption", path, skipped, total);
            Environment.ExitCode = 1;
            return Array.Empty<ReplayEvent>();
        }

        return events;
    }

    private void LogSkip(string path, int lineNumber, string reason)
    {
        _logger.LogError("Skipping line {Line} in {Path}: {Reason}", lineNumber, path, reason);
    }

    private bool TryGetDepth(JsonElement root, out DepthPayload payload)
    {
        payload = default;

        if (!root.TryGetProperty("side", out var sideProp) || !TryParseSide(sideProp.GetString(), out var side))
        {
            return false;
        }

        if (!root.TryGetProperty("operation", out var opProp) || opProp.ValueKind != JsonValueKind.Number || !opProp.TryGetInt32(out var op))
        {
            return false;
        }

        if (!root.TryGetProperty("price", out var priceProp) || priceProp.ValueKind != JsonValueKind.Number || !priceProp.TryGetDouble(out var price))
        {
            return false;
        }

        if (!root.TryGetProperty("size", out var sizeProp) || sizeProp.ValueKind != JsonValueKind.Number || !sizeProp.TryGetDouble(out var size))
        {
            return false;
        }

        if (!root.TryGetProperty("position", out var posProp) || posProp.ValueKind != JsonValueKind.Number || !posProp.TryGetInt32(out var position))
        {
            return false;
        }

        payload = new DepthPayload(side, (DepthOperation)op, (decimal)price, (decimal)size, position);
        return true;
    }

    private bool TryGetTape(JsonElement root, DateTimeOffset ts, out TradePrint payload)
    {
        payload = default;

        if (!root.TryGetProperty("price", out var priceProp) || priceProp.ValueKind != JsonValueKind.Number || !priceProp.TryGetDouble(out var price))
        {
            return false;
        }

        if (!root.TryGetProperty("size", out var sizeProp) || sizeProp.ValueKind != JsonValueKind.Number || !sizeProp.TryGetDouble(out var size))
        {
            return false;
        }

        payload = new TradePrint(ts.ToUnixTimeMilliseconds(), price, (decimal)size);
        return true;
    }

    private string ResolveSymbol()
    {
        var envSymbol = Environment.GetEnvironmentVariable("SYMBOL");
        if (!string.IsNullOrWhiteSpace(envSymbol))
        {
            return envSymbol.ToUpperInvariant();
        }

        var cfgSymbol = _configuration["Ibkr:Symbol"];
        return string.IsNullOrWhiteSpace(cfgSymbol) ? "AAPL" : cfgSymbol.ToUpperInvariant();
    }

    private string? ResolveLatestPath(string configKey, string fallbackPattern)
    {
        var configured = _configuration[configKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var directory = Path.GetDirectoryName(fallbackPattern);
        var pattern = Path.GetFileName(fallbackPattern);
        directory ??= ".";

        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.GetFiles(directory, pattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (value != null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
        {
            timestamp = dto;
            return true;
        }

        timestamp = default;
        return false;
    }

    private static bool TryParseSide(string? side, out DepthSide parsed)
    {
        if (string.Equals(side, "Bid", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DepthSide.Bid;
            return true;
        }

        if (string.Equals(side, "Ask", StringComparison.OrdinalIgnoreCase))
        {
            parsed = DepthSide.Ask;
            return true;
        }

        parsed = DepthSide.Bid;
        return false;
    }

    private void Fail(string reason, ReplayEvent evt)
    {
        _logger.LogError("Replay invariant failed: {Reason} at {Timestamp} Event={@Event}", reason, evt.TimestampUtc.ToString("o"), evt);
        Environment.ExitCode = 1;
    }

    private sealed class DepthLogLine
    {
        public string? TimestampUtc { get; set; }
        public string? Symbol { get; set; }
        public string? EventType { get; set; }
        public string? Side { get; set; }
        public int Position { get; set; }
        public int Operation { get; set; }
        public double Price { get; set; }
        public double Size { get; set; }
    }

    private sealed class TapeLogLine
    {
        public string? TimestampUtc { get; set; }
        public string? Symbol { get; set; }
        public string? EventType { get; set; }
        public double Price { get; set; }
        public double Size { get; set; }
        public string? Exchange { get; set; }
    }

    public readonly record struct DepthPayload(
        DepthSide Side,
        DepthOperation Operation,
        decimal Price,
        decimal Size,
        int Position);

    public readonly record struct ReplayEvent(
        DateTimeOffset TimestampUtc,
        string Symbol,
        string EventType,
        ReplayEventKind Kind,
        DepthPayload? DepthPayload,
        TradePrint? TapePayload);

    public enum ReplayEventKind
    {
        Depth = 0,
        Tape = 1
    }
}
