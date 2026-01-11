using System;
using System.Collections.Generic;
using System.Linq;

namespace RamStockAlerts.Models;

/// <summary>
/// Tracks how long individual price levels persist before removal and how their size evolves.
/// Pure measurement: no thresholds or scoring.
/// </summary>
public sealed class BidWallTracker
{
    private readonly Dictionary<(DepthSide Side, decimal Price), LevelState> _activeLevels = new();
    private readonly List<BidWallPersistenceRecord> _completed = new();

    /// <summary>
    /// Most recent completed persistence records (append-only until flushed).
    /// </summary>
    public IReadOnlyCollection<BidWallPersistenceRecord> CompletedRecords => _completed;

    /// <summary>
    /// Snapshot of currently active price levels with their running duration.
    /// </summary>
    public IEnumerable<BidWallActiveLevel> GetActiveLevels(long nowMs)
    {
        return _activeLevels.Values.Select(level => level.ToActive(nowMs));
    }

    /// <summary>
    /// Apply a depth change. Returns a completed persistence record when a level is removed.
    /// </summary>
    public BidWallPersistenceRecord? ApplyDepthUpdate(DepthUpdate update)
    {
        var key = (update.Side, update.Price);

        if (update.Operation == DepthOperation.Delete || update.Size <= 0m)
        {
            if (_activeLevels.Remove(key, out var state))
            {
                var completed = state.ToRecord(update.TimestampMs);
                _completed.Add(completed);
                return completed;
            }

            return null;
        }

        if (!_activeLevels.TryGetValue(key, out var existing))
        {
            existing = new LevelState(update.Symbol, update.Side, update.Price, update.TimestampMs, update.Size);
        }
        else
        {
            existing = existing with
            {
                LastUpdateMs = update.TimestampMs,
                LastSize = update.Size,
                MinSize = Math.Min(existing.MinSize, update.Size),
                MaxSize = Math.Max(existing.MaxSize, update.Size)
            };
        }

        _activeLevels[key] = existing;
        return null;
    }

    /// <summary>
    /// Finalizes all currently active levels at the specified timestamp and clears them.
    /// Useful at the end of a replay window.
    /// </summary>
    public IReadOnlyCollection<BidWallPersistenceRecord> CloseOpenLevels(long timestampMs)
    {
        foreach (var state in _activeLevels.Values)
        {
            _completed.Add(state.ToRecord(timestampMs));
        }

        _activeLevels.Clear();
        return _completed.ToList();
    }

    /// <summary>
    /// Returns completed records and resets the internal list.
    /// </summary>
    public IReadOnlyCollection<BidWallPersistenceRecord> FlushCompleted()
    {
        var snapshot = _completed.ToList();
        _completed.Clear();
        return snapshot;
    }

    private record struct LevelState(string Symbol, DepthSide Side, decimal Price, long StartedAtMs, decimal InitialSize)
    {
        public long LastUpdateMs { get; set; } = StartedAtMs;
        public decimal LastSize { get; set; } = InitialSize;
        public decimal MinSize { get; set; } = InitialSize;
        public decimal MaxSize { get; set; } = InitialSize;

        public BidWallPersistenceRecord ToRecord(long endMs)
        {
            var duration = endMs - StartedAtMs;
            return new BidWallPersistenceRecord(
                Symbol,
                Side,
                Price,
                StartedAtMs,
                endMs,
                duration < 0 ? 0 : duration,
                InitialSize,
                LastSize,
                MinSize,
                MaxSize);
        }

        public BidWallActiveLevel ToActive(long nowMs)
        {
            var duration = nowMs - StartedAtMs;
            return new BidWallActiveLevel(
                Symbol,
                Side,
                Price,
                StartedAtMs,
                duration < 0 ? 0 : duration,
                InitialSize,
                LastSize,
                MinSize,
                MaxSize);
        }
    }
}

/// <summary>
/// Completed persistence measurement for a single price level.
/// </summary>
public readonly record struct BidWallPersistenceRecord(
    string Symbol,
    DepthSide Side,
    decimal Price,
    long StartedAtMs,
    long EndedAtMs,
    long DurationMs,
    decimal InitialSize,
    decimal FinalSize,
    decimal MinSize,
    decimal MaxSize)
{
    public decimal SizeRange => MaxSize - MinSize;
    public decimal SizeChange => FinalSize - InitialSize;
}

/// <summary>
/// Active (not yet removed) price level state with running duration.
/// </summary>
public readonly record struct BidWallActiveLevel(
    string Symbol,
    DepthSide Side,
    decimal Price,
    long StartedAtMs,
    long DurationMs,
    decimal InitialSize,
    decimal CurrentSize,
    decimal MinSize,
    decimal MaxSize)
{
    public decimal SizeRange => MaxSize - MinSize;
    public decimal SizeChange => CurrentSize - InitialSize;
}
