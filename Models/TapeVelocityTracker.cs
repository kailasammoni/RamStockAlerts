using System;
using System.Collections.Generic;
using System.Linq;

namespace RamStockAlerts.Models;

/// <summary>
/// Aggregates tape velocity metrics over a rolling window.
/// Computes trades-per-second and volume-per-second without applying thresholds.
/// </summary>
public sealed class TapeVelocityTracker
{
    private readonly TimeSpan _window;
    private readonly Queue<TradePrint> _prints = new();

    public TapeVelocityTracker(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(3);
    }

    public TimeSpan Window => _window;

    public IReadOnlyCollection<TradePrint> WindowTrades => _prints.ToArray();

    public decimal TradesPerSecond => _window.TotalSeconds <= 0 ? 0m : (decimal)_prints.Count / (decimal)_window.TotalSeconds;

    public decimal VolumePerSecond => _window.TotalSeconds <= 0 ? 0m : _prints.Sum(p => p.Size) / (decimal)_window.TotalSeconds;

    /// <summary>
    /// Add a trade print into the rolling window.
    /// </summary>
    public void AddTrade(long receiptTimestampMs, double price, decimal size)
    {
        _prints.Enqueue(new TradePrint(receiptTimestampMs, receiptTimestampMs, price, size));
        Prune(receiptTimestampMs);
    }

    private void Prune(long nowMs)
    {
        var cutoff = nowMs - (long)_window.TotalMilliseconds;
        while (_prints.Count > 0 && _prints.Peek().ReceiptTimestampMs < cutoff)
        {
            _prints.Dequeue();
        }
    }
}
