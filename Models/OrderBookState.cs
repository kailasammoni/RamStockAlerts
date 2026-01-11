using System.Collections.Generic;
using System.Linq;

namespace RamStockAlerts.Models;

/*
REVIEW ANSWERS
1. Depth Semantics
- Ordered list (not dictionary): YES
- Insert shifts down: YES
- Delete shifts up: YES
- Trim to DepthRows after each update: YES

2. Book Reset Handling
- Reset condition exists for suspected refresh: YES
- Stale levels survive reset: NO

3. Validity Gate
- Single authoritative IsBookValid(out reason): YES
- Checked before scoring: YES
- Checked before blueprint creation: NO
- Checked before alerting: PARTIAL
- Blocks crossed OR locked books: YES

4. Exception Hygiene
- Depth callbacks wrapped so one bad event cannot kill stream: NO
- Malformed events logged and skipped (not thrown): YES (in replay only)
*/

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
    private const long StaleDepthThresholdMs = 2000;
    private const int PositionSlack = 5;

    private readonly List<DepthLevel> _bidLevels = new();
    private readonly List<DepthLevel> _askLevels = new();
    private readonly Queue<TradePrint> _recentTrades;
    private Action<string>? _logger;
    private Action<string, DepthSide, decimal, decimal, int, long>? _onBookReset;

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
    /// Maximum number of depth levels to maintain per side. Defaults to 10.
    /// </summary>
    public int MaxDepthRows { get; set; } = 10;

    /// <summary>
    /// Total count of book resets detected (Insert at position 0 with empty or large price jump).
    /// </summary>
    public int ResetCount { get; private set; }

    /// <summary>
    /// Timestamp (ms) of the most recent book reset event.
    /// </summary>
    public long LastResetMs { get; private set; }

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
    /// Set an optional logger for depth operation tracing.
    /// </summary>
    public void SetLogger(Action<string> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Set an optional callback for BookReset events. Called when Insert at position 0 triggers a reset.
    /// Receives: Symbol, Side, CurrentBestPrice, NewPrice, ResetCount, TimestampMs
    /// </summary>
    public void SetBookResetCallback(Action<string, DepthSide, decimal, decimal, int, long>? callback)
    {
        _onBookReset = callback;
    }

    /// <summary>
    /// Apply a deterministic depth update to the book using IBKR position-based semantics.
    /// Position-based operations: Insert shifts down, Delete shifts up, Update replaces.
    /// Position extension fills gaps with placeholder levels (0,0,ts) to maintain alignment.
    /// Delete semantics: only (price==0 && size==0) or operation==Delete triggers removal.
    /// </summary>
    public void ApplyDepthUpdate(DepthUpdate update)
    {
        var levels = update.Side == DepthSide.Bid ? _bidLevels : _askLevels;
        var size = update.Size;
        var price = update.Price;

        if (update.Position < 0 || update.Position > MaxDepthRows + PositionSlack)
        {
            _logger?.Invoke($"[{update.Side}] Invalid position {update.Position}; skipping");
            return;
        }

        if (price <= 0m)
        {
            _logger?.Invoke($"[{update.Side}] Invalid price {price}; skipping");
            return;
        }

        if (size < 0m)
        {
            _logger?.Invoke($"[{update.Side}] Invalid size {size}; skipping");
            return;
        }

        if (update.Position == 0)
        {
            if (update.Side == DepthSide.Bid && BestAsk > 0m && price >= BestAsk)
            {
                _logger?.Invoke($"[{update.Side}] Crossed bid {price} >= best ask {BestAsk}; skipping");
                return;
            }

            if (update.Side == DepthSide.Ask && BestBid > 0m && price <= BestBid)
            {
                _logger?.Invoke($"[{update.Side}] Crossed ask {price} <= best bid {BestBid}; skipping");
                return;
            }
        }

        // Book reset: if Insert at position 0 and (empty book OR large price jump), clear and rebuild.
        if (update.Operation == DepthOperation.Insert && update.Position == 0)
        {
            var currentBest = levels.Count > 0 ? levels[0].Price : 0m;
            // Trigger reset if: empty book (currentBest<=0) OR large price jump (>=10 cents)
            if (currentBest <= 0m || Math.Abs(price - currentBest) >= 0.10m)
            {
                levels.Clear();
                ResetCount++;
                LastResetMs = update.TimestampMs;
                var resetMsg = $"[{update.Side}] Book reset at Insert/pos0: currentBest=${currentBest}, newPrice=${price}, ResetCount={ResetCount}, ts={update.TimestampMs}ms";
                _logger?.Invoke(resetMsg);
                _onBookReset?.Invoke(update.Symbol, update.Side, currentBest, price, ResetCount, update.TimestampMs);
            }
        }

        // Determine if this is a delete: either Delete operation or (price==0 && size==0).
        bool isDelete = update.Operation == DepthOperation.Delete || (price == 0m && size == 0m);

        // Apply operation.
        if (isDelete)
        {
            // Delete: remove at position if exists.
            if (update.Position >= 0 && update.Position < levels.Count)
            {
                var removed = levels[update.Position];
                levels.RemoveAt(update.Position);
                _logger?.Invoke($"[{update.Side}] Deleted at pos {update.Position}: {removed.Price}@{removed.Size}");
            }
            else if (update.Position >= 0)
            {
                _logger?.Invoke($"[{update.Side}] Delete pos {update.Position} out of range (count={levels.Count})");
            }
        }
        else if (update.Operation == DepthOperation.Insert)
        {
            // Insert: add at position, shift down.
            if (update.Position < 0)
            {
                _logger?.Invoke($"[{update.Side}] Invalid Insert position {update.Position}; skipping");
            }
            else if (update.Position >= levels.Count)
            {
                // Extend with placeholder levels up to position, then add the new level.
                while (levels.Count < update.Position)
                {
                    levels.Add(new DepthLevel(0m, 0m, update.TimestampMs));
                }
                levels.Add(new DepthLevel(price, size, update.TimestampMs));
                _logger?.Invoke($"[{update.Side}] Extended and inserted at pos {update.Position}: {price}@{size}");
            }
            else
            {
                // Insert at position, shift existing down.
                levels.Insert(update.Position, new DepthLevel(price, size, update.TimestampMs));
                _logger?.Invoke($"[{update.Side}] Inserted at pos {update.Position}: {price}@{size}");
            }
        }
        else if (update.Operation == DepthOperation.Update)
        {
            // Update: replace at position if exists, else extend with placeholders.
            if (update.Position < 0)
            {
                _logger?.Invoke($"[{update.Side}] Invalid Update position {update.Position}; skipping");
            }
            else if (update.Position >= levels.Count)
            {
                // Extend with placeholder levels up to position, then add the new level.
                while (levels.Count < update.Position)
                {
                    levels.Add(new DepthLevel(0m, 0m, update.TimestampMs));
                }
                levels.Add(new DepthLevel(price, size, update.TimestampMs));
                _logger?.Invoke($"[{update.Side}] Extended and updated at pos {update.Position}: {price}@{size}");
            }
            else
            {
                // Replace at position.
                var oldLevel = levels[update.Position];
                levels[update.Position] = new DepthLevel(price, size, update.TimestampMs);
                _logger?.Invoke($"[{update.Side}] Updated pos {update.Position}: {oldLevel.Price}@{oldLevel.Size} -> {price}@{size}");
            }
        }

        // Trim to max depth rows (configurable).
        while (levels.Count > MaxDepthRows)
        {
            var removed = levels[levels.Count - 1];
            levels.RemoveAt(levels.Count - 1);
            _logger?.Invoke($"[{update.Side}] Trimmed: removed {removed.Price}@{removed.Size}");
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
