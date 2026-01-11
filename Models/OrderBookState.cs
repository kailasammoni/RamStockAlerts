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
/// Deterministic in-memory order book state for a single symbol.
/// Maintains bid/ask levels, best prices, spread, and rolling trade prints.
/// </summary>
public sealed class OrderBookState
{
    private const int DefaultMaxTrades = 512;

    private static readonly IComparer<decimal> BidComparer = Comparer<decimal>.Create((a, b) => b.CompareTo(a));

    private readonly SortedDictionary<decimal, BookLevel> _bidLevels = new(BidComparer);
    private readonly SortedDictionary<decimal, BookLevel> _askLevels = new();
    private readonly Queue<TradePrint> _recentTrades;

    public OrderBookState(string symbol = "")
    {
        Symbol = symbol ?? string.Empty;
        _recentTrades = new Queue<TradePrint>(DefaultMaxTrades);
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
    /// Read-only bid levels (price -> level state). Highest price first.
    /// </summary>
    public IEnumerable<KeyValuePair<decimal, BookLevel>> BidLevels => _bidLevels;

    /// <summary>
    /// Read-only ask levels (price -> level state). Lowest price first.
    /// </summary>
    public IEnumerable<KeyValuePair<decimal, BookLevel>> AskLevels => _askLevels;

    /// <summary>
    /// Rolling tape buffer (bounded) for compatibility with existing metrics.
    /// </summary>
    public IReadOnlyCollection<TradePrint> RecentTrades => _recentTrades;

    /// <summary>
    /// Best bid price (0 when empty).
    /// </summary>
    public decimal BestBid => _bidLevels.Count > 0 ? _bidLevels.First().Key : 0m;

    /// <summary>
    /// Best ask price (0 when empty).
    /// </summary>
    public decimal BestAsk => _askLevels.Count > 0 ? _askLevels.First().Key : 0m;

    /// <summary>
    /// Spread in price units (ask - bid). Returns 0 when either side is empty.
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

            var best = _bidLevels.First().Value;
            return LastUpdateMs - best.LastUpdateMs;
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

            var best = _askLevels.First().Value;
            return LastUpdateMs - best.LastUpdateMs;
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
            .Sum(kv => kv.Value.Size);
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
            .Sum(kv => kv.Value.Size);
    }

    /// <summary>
    /// Convenience accessors for existing consumers.
    /// </summary>
    public decimal TotalBidSize4Level => TotalBidSize(4);
    public decimal TotalAskSize4Level => TotalAskSize(4);

    /// <summary>
    /// Apply a deterministic depth update to the book.
    /// </summary>
    public void ApplyDepthUpdate(DepthUpdate update)
    {
        var levels = update.Side == DepthSide.Bid ? _bidLevels : _askLevels;

        if (update.Operation == DepthOperation.Delete || update.Size <= 0m)
        {
            levels.Remove(update.Price);
        }
        else
        {
            levels[update.Price] = new BookLevel(update.Size, update.TimestampMs);
        }

        LastUpdateMs = Math.Max(LastUpdateMs, update.TimestampMs);
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
