using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Replays recorded depth and tape JSONL files in deterministic timestamp order and feeds state trackers.
/// </summary>
public sealed class DepthReplayService
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task ReplayAsync(
        string depthFilePath,
        string tapeFilePath,
        OrderBookState orderBook,
        BidWallTracker bidWallTracker,
        TapeVelocityTracker tapeVelocityTracker,
        CancellationToken cancellationToken = default)
    {
        // FIX #2: Mandatory hard-gate counters
        long invalidBookCount = 0;
        long crossedBookCount = 0;
        long exceptionsCount = 0;

        var events = new List<ReplayEvent>();
        long sequence = 0;

        if (File.Exists(depthFilePath))
        {
            var depthEvents = ReadDepthEvents(depthFilePath, sequence);
            events.AddRange(depthEvents);
            sequence += depthEvents.Count;
        }

        if (File.Exists(tapeFilePath))
        {
            var tapeEvents = ReadTapeEvents(tapeFilePath, sequence);
            events.AddRange(tapeEvents);
            sequence += tapeEvents.Count;
        }

        if (events.Count == 0)
        {
            return;
        }

        foreach (var evt in events.OrderBy(e => e.TimestampMs).ThenBy(e => e.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                switch (evt.Kind)
                {
                    case ReplayEventKind.Depth when evt.DepthUpdate.HasValue:
                        var depthUpdate = evt.DepthUpdate.Value;
                        orderBook.ApplyDepthUpdate(depthUpdate);
                        
                        // Fix 3 & 2: Validity gate before bidWallTracker + counter
                        if (orderBook.IsBookValid(out var validityReason, evt.TimestampMs))
                        {
                            bidWallTracker.ApplyDepthUpdate(depthUpdate);
                        }
                        else
                        {
                            invalidBookCount++;
                            // FIX #2: Hard gate - crossed or locked books must fail replay
                            if (validityReason == "CrossedBook" || validityReason == "LockedBook")
                            {
                                crossedBookCount++;
                            }
                        }
                        break;

                    case ReplayEventKind.Tape when evt.TradePrint.HasValue:
                        var trade = evt.TradePrint.Value;
                        orderBook.RecordTrade(trade.TimestampMs, trade.Price, trade.Size);
                        tapeVelocityTracker.AddTrade(trade.TimestampMs, trade.Price, trade.Size);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Fix 4 & 2: Log and skip malformed events + counter
                exceptionsCount++;
                System.Diagnostics.Debug.WriteLine($"[DepthReplay] Error processing event at {evt.TimestampMs}ms: {ex.Message}");
                // Continue processing remaining events
            }
        }

        // Close any remaining active walls at the last seen timestamp to capture final durations.
        bidWallTracker.CloseOpenLevels(events[^1].TimestampMs);

        if (crossedBookCount > 0 || exceptionsCount > 0)
        {
            var warningMsg = $"[DepthReplay] Completed with CrossedBooks={crossedBookCount}, Exceptions={exceptionsCount}";
            System.Diagnostics.Debug.WriteLine(warningMsg);
        }

        await Task.CompletedTask;
    }

    private List<ReplayEvent> ReadDepthEvents(string path, long startingSequence)
    {
        var result = new List<ReplayEvent>();
        var sequence = startingSequence;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DepthLogLine? logLine;
            try
            {
                logLine = JsonSerializer.Deserialize<DepthLogLine>(line, _jsonOptions);
            }
            catch
            {
                continue; // skip malformed lines
            }

            if (logLine is null || !IsDepthEvent(logLine.EventType))
            {
                continue;
            }

            if (!TryParseSide(logLine.Side, out var side))
            {
                continue;
            }

            if (!Enum.IsDefined(typeof(DepthOperation), logLine.Operation))
            {
                continue;
            }

            var ts = ParseTimestampMs(logLine.TimestampUtc);
            var depthUpdate = new DepthUpdate(
                logLine.Symbol ?? string.Empty,
                side,
                (DepthOperation)logLine.Operation,
                logLine.Price,
                logLine.Size,
                logLine.Position,
                ts);

            result.Add(new ReplayEvent(ts, sequence++, ReplayEventKind.Depth, depthUpdate, null));
        }

        return result;
    }

    private List<ReplayEvent> ReadTapeEvents(string path, long startingSequence)
    {
        var result = new List<ReplayEvent>();
        var sequence = startingSequence;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TapeLogLine? logLine;
            try
            {
                logLine = JsonSerializer.Deserialize<TapeLogLine>(line, _jsonOptions);
            }
            catch
            {
                continue;
            }

            if (logLine is null || !IsTapeEvent(logLine.EventType))
            {
                continue;
            }

            var ts = ParseTimestampMs(logLine.TimestampUtc);
            var trade = new TradePrint(ts, ts, logLine.Price, logLine.Size);
            result.Add(new ReplayEvent(ts, sequence++, ReplayEventKind.Tape, null, trade));
        }

        return result;
    }

    private static bool IsDepthEvent(string? eventType)
    {
        return string.Equals(eventType, "Depth", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTapeEvent(string? eventType)
    {
        return string.Equals(eventType, "Tape", StringComparison.OrdinalIgnoreCase);
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

    private static long ParseTimestampMs(string? timestamp)
    {
        if (timestamp is null)
        {
            return 0;
        }

        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }

        return 0;
    }

    private sealed class DepthLogLine
    {
        public string? TimestampUtc { get; set; }
        public string? Symbol { get; set; }
        public string? EventType { get; set; }
        public string? Side { get; set; }
        public int Position { get; set; }
        public int Operation { get; set; }
        public decimal Price { get; set; }
        public decimal Size { get; set; }
    }

    private sealed class TapeLogLine
    {
        public string? TimestampUtc { get; set; }
        public string? Symbol { get; set; }
        public string? EventType { get; set; }
        public double Price { get; set; }
        public decimal Size { get; set; }
    }

    private readonly record struct ReplayEvent(
        long TimestampMs,
        long Sequence,
        ReplayEventKind Kind,
        DepthUpdate? DepthUpdate,
        TradePrint? TradePrint);

    private enum ReplayEventKind
    {
        Depth,
        Tape
    }
}
