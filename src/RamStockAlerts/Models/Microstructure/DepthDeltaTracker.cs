using System;
using System.Collections.Generic;
using RamStockAlerts.Models;

namespace RamStockAlerts.Models.Microstructure;

/// <summary>
/// Observes depth deltas near touch without affecting strategy decisions or gating.
/// O(1) per update with bounded memory via windowed queues.
/// </summary>
public sealed class DepthDeltaTracker
{
    private const int DefaultTrackTopNLevels = 5;
    private static readonly int[] DefaultWindowsMs = { 1_000, 3_000, 10_000 };

    private readonly WindowState[] _bidWindows;
    private readonly WindowState[] _askWindows;

    public DepthDeltaTracker(int trackTopNLevels = DefaultTrackTopNLevels, int[]? windowsMs = null)
    {
        TrackTopNLevels = trackTopNLevels;
        WindowsMs = windowsMs ?? DefaultWindowsMs;
        _bidWindows = CreateWindows();
        _askWindows = CreateWindows();
    }

    public int TrackTopNLevels { get; }

    public IReadOnlyList<int> WindowsMs { get; }

    /// <summary>
    /// Record a depth delta for near-touch positions. Purely observational.
    /// </summary>
    public void OnDepthUpdate(
        DepthSide side,
        DepthOperation operation,
        int position,
        decimal price,
        decimal size,
        decimal previousSize,
        long timestampMs)
    {
        if (position < 0 || position >= TrackTopNLevels)
        {
            return;
        }

        var windows = side == DepthSide.Bid ? _bidWindows : _askWindows;
        for (var i = 0; i < windows.Length; i++)
        {
            var windowMs = WindowsMs[i];
            TrimExpired(windows[i], timestampMs, windowMs);

            var evt = BuildEvent(operation, size, previousSize, timestampMs);
            if (evt == null)
            {
                continue;
            }

            ApplyEvent(windows[i], evt.Value);
        }
    }

    public DepthDeltaSnapshot GetSnapshot(long nowMs)
    {
        var bid = new DepthDeltaWindowSnapshot[_bidWindows.Length];
        var ask = new DepthDeltaWindowSnapshot[_askWindows.Length];

        for (var i = 0; i < _bidWindows.Length; i++)
        {
            TrimExpired(_bidWindows[i], nowMs, WindowsMs[i]);
            bid[i] = _bidWindows[i].AsSnapshot();

            TrimExpired(_askWindows[i], nowMs, WindowsMs[i]);
            ask[i] = _askWindows[i].AsSnapshot();
        }

        // Windows are fixed to 1s, 3s, 10s order by default.
        return new DepthDeltaSnapshot(
            bid.Length > 0 ? bid[0] : DepthDeltaWindowSnapshot.Empty,
            ask.Length > 0 ? ask[0] : DepthDeltaWindowSnapshot.Empty,
            bid.Length > 1 ? bid[1] : DepthDeltaWindowSnapshot.Empty,
            ask.Length > 1 ? ask[1] : DepthDeltaWindowSnapshot.Empty,
            bid.Length > 2 ? bid[2] : DepthDeltaWindowSnapshot.Empty,
            ask.Length > 2 ? ask[2] : DepthDeltaWindowSnapshot.Empty);
    }

    private WindowState[] CreateWindows()
    {
        var windows = new WindowState[WindowsMs.Count];
        for (var i = 0; i < windows.Length; i++)
        {
            windows[i] = new WindowState();
        }
        return windows;
    }

    private static void TrimExpired(WindowState state, long nowMs, int windowMs)
    {
        while (state.Events.Count > 0 && nowMs - state.Events.Peek().TimestampMs >= windowMs)
        {
            var evt = state.Events.Dequeue();
            state.Counters.AddCount -= evt.AddCount;
            state.Counters.CancelCount -= evt.CancelCount;
            state.Counters.UpdateCount -= evt.UpdateCount;
            state.Counters.TotalAddedSize -= evt.TotalAddedSize;
            state.Counters.TotalCanceledSize -= evt.TotalCanceledSize;
            state.Counters.TotalAbsSizeDelta -= evt.TotalAbsSizeDelta;
        }
    }

    private static DeltaEvent? BuildEvent(DepthOperation operation, decimal size, decimal previousSize, long timestampMs)
    {
        if (timestampMs <= 0)
        {
            return null;
        }

        switch (operation)
        {
            case DepthOperation.Insert:
                return new DeltaEvent(timestampMs, 1, 0, 0, size, 0m, 0m);
            case DepthOperation.Delete:
                // Use previous size to capture cancellation magnitude when provided.
                return new DeltaEvent(timestampMs, 0, 1, 0, 0m, previousSize > 0m ? previousSize : size, 0m);
            case DepthOperation.Update:
                var absDelta = Math.Abs(size - previousSize);
                return new DeltaEvent(timestampMs, 0, 0, 1, 0m, 0m, absDelta);
            default:
                return null;
        }
    }

    private static void ApplyEvent(WindowState state, DeltaEvent evt)
    {
        state.Events.Enqueue(evt);
        state.Counters.AddCount += evt.AddCount;
        state.Counters.CancelCount += evt.CancelCount;
        state.Counters.UpdateCount += evt.UpdateCount;
        state.Counters.TotalAddedSize += evt.TotalAddedSize;
        state.Counters.TotalCanceledSize += evt.TotalCanceledSize;
        state.Counters.TotalAbsSizeDelta += evt.TotalAbsSizeDelta;
    }

    private sealed class WindowState
    {
        public Queue<DeltaEvent> Events { get; } = new();
        public Counters Counters { get; } = new();

        public DepthDeltaWindowSnapshot AsSnapshot()
        {
            var ratio = Counters.AddCount > 0 ? Counters.CancelCount / (decimal)Counters.AddCount : 0m;
            return new DepthDeltaWindowSnapshot(
                Counters.AddCount,
                Counters.CancelCount,
                Counters.UpdateCount,
                Counters.TotalAddedSize,
                Counters.TotalCanceledSize,
                Counters.TotalAbsSizeDelta,
                ratio);
        }
    }

    private sealed class Counters
    {
        public int AddCount;
        public int CancelCount;
        public int UpdateCount;
        public decimal TotalAddedSize;
        public decimal TotalCanceledSize;
        public decimal TotalAbsSizeDelta;
    }

    private readonly record struct DeltaEvent(
        long TimestampMs,
        int AddCount,
        int CancelCount,
        int UpdateCount,
        decimal TotalAddedSize,
        decimal TotalCanceledSize,
        decimal TotalAbsSizeDelta);
}

public readonly record struct DepthDeltaSnapshot(
    DepthDeltaWindowSnapshot Bid1s,
    DepthDeltaWindowSnapshot Ask1s,
    DepthDeltaWindowSnapshot Bid3s,
    DepthDeltaWindowSnapshot Ask3s,
    DepthDeltaWindowSnapshot Bid10s,
    DepthDeltaWindowSnapshot Ask10s);

public readonly record struct DepthDeltaWindowSnapshot(
    int AddCount,
    int CancelCount,
    int UpdateCount,
    decimal TotalAddedSize,
    decimal TotalCanceledSize,
    decimal TotalAbsSizeDelta,
    decimal CancelToAddRatio)
{
    public static readonly DepthDeltaWindowSnapshot Empty = new(0, 0, 0, 0m, 0m, 0m, 0m);
}
