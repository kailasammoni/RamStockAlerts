using System.Collections.Generic;
using System.Linq;

namespace RamStockAlerts.Models;

/// <summary>
/// Side of the depth book.
/// </summary>
public enum DepthSide
{
    Bid,
    Ask
}

/// <summary>
/// Operation type emitted by the depth feed.
/// </summary>
public enum DepthOperation
{
    Insert = 0,
    Update = 1,
    Delete = 2
}

/// <summary>
/// Depth event shape used by the deterministic state model.
/// </summary>
public readonly record struct DepthUpdate(
    string Symbol,
    DepthSide Side,
    DepthOperation Operation,
    decimal Price,
    decimal Size,
    int Position,
    long TimestampMs);

/// <summary>
/// Trade print used for optional tape bookkeeping.
/// </summary>
public readonly record struct TradePrint(long TimestampMs, double Price, decimal Size);

/// <summary>
/// Single depth level: price with size and timestamp.
/// </summary>
public readonly record struct DepthLevel(decimal Price, decimal Size, long TimestampMs);

/// <summary>
/// Deterministic in-memory order book state for a single symbol.
/// Maintains position-based bid/ask lists per IBKR semantics, best prices, spread, and rolling trade prints.
/// </summary>
public sealed class OrderBookState
{
    private const int DefaultMaxTrades = 512;
    private const int DefaultMaxDepthRows = 10;
    private const long StaleDepthThresholdMs = 2000;

    private readonly List<DepthLevel> _bidLevels = new();
    private readonly List<DepthLevel> _askLevels = new();
    private readonly Queue<TradePrint> _recentTrades;

    public OrderBookState(string symbol = "")
    {
        Symbol = symbol ?? string.Empty;
        _recentTrades = new Queue<TradePrint>(DefaultMaxTrades);
        LastDepthUpdateUtcMs = 0;
    }

    /// <summary>
    /// Symbol for this book.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the last applied update or trade (ms since epoch).
    /// </summary>
    public long LastUpdateMs { get; private set; }

    /// <summary>
    /// Timestamp of the last applied depth update (ms since epoch). Used for staleness check.
    /// </summary>
    public long LastDepthUpdateUtcMs { get; private set; }

    /// <summary>
    /// Read-only bid levels in position order (index 0 = best bid).
    /// </summary>
    public IReadOnlyList<DepthLevel> BidLevels => _bidLevels.AsReadOnly();

    /// <summary>
    /// Read-only ask levels in position order (index 0 = best ask).
    /// </summary>
    public IReadOnlyList<DepthLevel> AskLevels => _askLevels.AsReadOnly();

    /// <summary>
    /// Rolling tape buffer (bounded) for compatibility with existing metrics.
    /// </summary>
    public IReadOnlyCollection<TradePrint> RecentTrades => _recentTrades;

    /// <summary>
    /// Best bid price (0 when empty or invalid).
    /// </summary>
    public decimal BestBid => _bidLevels.Count > 0 && _bidLevels[0].Size > 0m ? _bidLevels[0].Price : 0m;

    /// <summary>
    /// Best ask price (0 when empty or invalid).
    /// </summary>
    public decimal BestAsk => _askLevels.Count > 0 && _askLevels[0].Size > 0m ? _askLevels[0].Price : 0m;

    /// <summary>
    /// Spread in price units (ask - bid). Returns 0 when either side is empty or invalid.
    /// </summary>
    public decimal Spread => BestBid > 0m && BestAsk > 0m ? BestAsk - BestBid : 0m;

    /// <summary>
    /// Age of the current best bid level in milliseconds (MaxValue if unavailable).
    /// </summary>
    public long BestBidAgeMs
    {
        get
        {
            if (_bidLevels.Count == 0 || LastUpdateMs == 0)
            {
                return long.MaxValue;
            }

            return LastUpdateMs - _bidLevels[0].TimestampMs;
        }
    }

    /// <summary>
    /// Age of the current best ask level in milliseconds (MaxValue if unavailable).
    /// </summary>
    public long BestAskAgeMs
    {
        get
        {
            if (_askLevels.Count == 0 || LastUpdateMs == 0)
            {
                return long.MaxValue;
            }

            return LastUpdateMs - _askLevels[0].TimestampMs;
        }
    }

    /// <summary>
    /// Sum of bid sizes across the top N price levels.
    /// </summary>
    public decimal TotalBidSize(int levels)
    {
        if (levels <= 0)
        {
            return 0m;
        }

        return _bidLevels
            .Take(levels)
            .Sum(x => x.Size);
    }

    /// <summary>
    /// Sum of ask sizes across the top N price levels.
    /// </summary>
    public decimal TotalAskSize(int levels)
    {
        if (levels <= 0)
        {
            return 0m;
        }

        return _askLevels
            .Take(levels)
            .Sum(x => x.Size);
    }

    /// <summary>
    /// Convenience accessors for existing consumers.
    /// </summary>
    public decimal TotalBidSize4Level => TotalBidSize(4);
    public decimal TotalAskSize4Level => TotalAskSize(4);

    /// <summary>
    /// Check if the book is valid for trading.
    /// </summary>
    public bool IsBookValid(out string reason, long nowUtcMs = 0)
    {
        reason = string.Empty;

        if (nowUtcMs == 0)
        {
            nowUtcMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        if (BestBid <= 0m)
        {
            reason = "BestBidZeroOrNegative";
            return false;
        }

        if (BestAsk <= 0m)
        {
            reason = "BestAskZeroOrNegative";
            return false;
        }

        if (BestBid >= BestAsk)
        {
            reason = BestBid > BestAsk ? "CrossedBook" : "LockedBook";
            return false;
        }

        if (Spread <= 0m || Spread > 0.20m)
        {
            reason = $"SpreadOutOfBounds:{Spread}";
            return false;
        }

        if (_bidLevels.Count == 0 || _bidLevels[0].Size <= 0m)
        {
            reason = "BestBidSizeZeroOrNegative";
            return false;
        }

        if (_askLevels.Count == 0 || _askLevels[0].Size <= 0m)
        {
            reason = "BestAskSizeZeroOrNegative";
            return false;
        }

        if (nowUtcMs - LastDepthUpdateUtcMs > StaleDepthThresholdMs)
        {
            reason = "StaleDepthData";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Apply a deterministic depth update to the book using IBKR position-based semantics.
    /// </summary>
    public void ApplyDepthUpdate(DepthUpdate update)
    {
        var levels = update.Side == DepthSide.Bid ? _bidLevels : _askLevels;
        var size = update.Size;

        // Clamp negative sizes to 0 and log warning.
        if (size < 0m)
        {
            size = 0m;
        }

        // Book reset: if Insert at position 0 and large jump in price, clear side and rebuild.
        if (update.Operation == DepthOperation.Insert && update.Position == 0 && levels.Count > 0)
        {
            var currentBest = levels[0].Price;
            if (currentBest > 0m && Math.Abs(update.Price - currentBest) >= 0.10m)
            {
                levels.Clear();
            }
        }

        // Apply operation.
        if (update.Operation == DepthOperation.Delete || size == 0m)
        {
            // Delete: remove at position if exists.
            if (update.Position >= 0 && update.Position < levels.Count)
            {
                levels.RemoveAt(update.Position);
            }
        }
        else if (update.Operation == DepthOperation.Insert)
        {
            // Insert: add at position, shift down, trim.
            if (update.Position < 0)
            {
                // Invalid position; skip.
            }
            else if (update.Position >= levels.Count)
            {
                // Extend with this level.
                levels.Add(new DepthLevel(update.Price, size, update.TimestampMs));
            }
            else
            {
                // Insert at position, shift existing down.
                levels.Insert(update.Position, new DepthLevel(update.Price, size, update.TimestampMs));
            }
        }
        else if (update.Operation == DepthOperation.Update)
        {
            // Update: replace at position if exists, else extend.
            if (update.Position < 0)
            {
                // Invalid position; skip.
            }
            else if (update.Position >= levels.Count)
            {
                // Extend with this level.
                levels.Add(new DepthLevel(update.Price, size, update.TimestampMs));
            }
            else
            {
                // Replace at position.
                levels[update.Position] = new DepthLevel(update.Price, size, update.TimestampMs);
            }
        }

        // Trim to max depth rows.
        while (levels.Count > DefaultMaxDepthRows)
        {
            levels.RemoveAt(levels.Count - 1);
        }

        LastUpdateMs = Math.Max(LastUpdateMs, update.TimestampMs);
        LastDepthUpdateUtcMs = Math.Max(LastDepthUpdateUtcMs, update.TimestampMs);
    }

    /// <summary>
    /// Backwards-compatible helper for bid depth mutations.
    /// </summary>
    public void UpdateBidDepth(decimal price, decimal size, long timestampMs, int position = -1)
    {
        var op = size <= 0m ? DepthOperation.Delete : DepthOperation.Update;
        ApplyDepthUpdate(new DepthUpdate(Symbol, DepthSide.Bid, op, price, size, position, timestampMs));
    }

    /// <summary>
    /// Backwards-compatible helper for ask depth mutations.
    /// </summary>
    public void UpdateAskDepth(decimal price, decimal size, long timestampMs, int position = -1)
    {
        var op = size <= 0m ? DepthOperation.Delete : DepthOperation.Update;
        ApplyDepthUpdate(new DepthUpdate(Symbol, DepthSide.Ask, op, price, size, position, timestampMs));
    }

    /// <summary>
    /// Record a trade print for optional downstream metrics.
    /// </summary>
    public void RecordTrade(long timestampMs, double price, decimal size)
    {
        _recentTrades.Enqueue(new TradePrint(timestampMs, price, size));
        TrimTrades();
        LastUpdateMs = Math.Max(LastUpdateMs, timestampMs);
    }

    /// <summary>
    /// Remove trades older than a provided timestamp (inclusive).
    /// </summary>
    public void PruneTrades(long windowStartMs)
    {
        while (_recentTrades.Count > 0 && _recentTrades.Peek().TimestampMs < windowStartMs)
        {
            _recentTrades.Dequeue();
        }
    }

    private void TrimTrades()
    {
        while (_recentTrades.Count > DefaultMaxTrades)
        {
            _recentTrades.Dequeue();
        }
    }

    /// <summary>
    /// Immutable depth level payload.
    /// </summary>
    public readonly record struct BookLevel(decimal Size, long LastUpdateMs);
}
