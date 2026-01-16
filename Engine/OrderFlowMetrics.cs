using RamStockAlerts.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace RamStockAlerts.Engine;

/// <summary>
/// Computes microstructure metrics from order book and tape data
/// 
/// Metrics:
/// - QueueImbalance: Σ(BidDepth[0..3]) / Σ(AskDepth[0..3])
/// - WallPersistence: Time best level unchanged (≥1000ms = persistent wall)
/// - AbsorptionRate: How quickly orders are filled (Δ(Size)/Δ(Time))
/// - SpoofScore: CancelRate / AddRate (0.0-1.0, <0.3 = spoofing)
/// - TapeAcceleration: Δ(trades/sec) over 3-second window (≥2.0 = sudden acceleration)
/// </summary>
public class OrderFlowMetrics
{
    private readonly ILogger<OrderFlowMetrics> _logger;
    
    private readonly Dictionary<string, MetricSnapshot> _snapshots = new();
    private readonly Dictionary<string, TapeWindow> _tapeWindows = new();
    private readonly ConcurrentDictionary<string, OrderBookState> _orderBooks = new();
    
    private const long WALL_PERSISTENCE_MS = 1000; // 1 second
    private const int TAPE_WINDOW_MS = 3000;       // 3 second acceleration window
    private const decimal TAPE_ACCELERATION_THRESHOLD = 2.0m; // 2x increase in trade rate
    
    public OrderFlowMetrics(ILogger<OrderFlowMetrics> logger)
    {
        _logger = logger;
    }
    
    public class MetricSnapshot
    {
        public string Symbol { get; set; } = string.Empty;
        public long TimestampMs { get; set; }
        
        // Queue Imbalance (bid depth / ask depth)
        public decimal QueueImbalance { get; set; }
        public decimal BidDepth4Level { get; set; }
        public decimal AskDepth4Level { get; set; }
        
        // Wall Persistence
        public long BidWallAgeMs { get; set; }
        public long AskWallAgeMs { get; set; }
        public bool HasPersistentBidWall => BidWallAgeMs >= WALL_PERSISTENCE_MS;
        public bool HasPersistentAskWall => AskWallAgeMs >= WALL_PERSISTENCE_MS;
        
        // Absorption Rate
        public decimal BidAbsorptionRate { get; set; } // units/sec at bid
        public decimal AskAbsorptionRate { get; set; } // units/sec at ask
        
        // Spoof Score
        public decimal SpoofScore { get; set; } // 0.0-1.0, <0.3 = likely spoofing
        
        // Tape Acceleration
        public decimal TapeAcceleration { get; set; } // trade rate ratio
        public int TradesIn3Sec { get; set; }
        
        // Spread
        public decimal Spread { get; set; }
        public decimal MidPrice { get; set; }
    }
    
    private class TapeWindow
    {
        public long StartMs { get; set; }
        public int TradeCount { get; set; }
        public decimal TotalVolume { get; set; }
        public int CancelCount { get; set; }
        public int AddCount { get; set; }
    }
    
    /// <summary>
    /// Update metrics based on current order book state
    /// MANDATORY FIX #1: Hard gate enforces that metrics are NEVER emitted for invalid books.
    /// Any invalid book returns a zeroed snapshot. Callers must not use invalid snapshots for signal generation.
    /// </summary>
    public MetricSnapshot UpdateMetrics(OrderBookState book, long currentTimeMs)
    {
        // HARD GATE: Absolute check - no exceptions
        if (!book.IsBookValid(out var validityReason, currentTimeMs))
        {
            _logger.LogWarning("[Metrics GATE] Blocking metrics for {Symbol}: {Reason} (timestamp={TimestampMs})", 
                book.Symbol, validityReason, currentTimeMs);
            
            // Return zeroed snapshot - safe for consumers
            var invalidSnapshot = new MetricSnapshot
            {
                Symbol = book.Symbol,
                TimestampMs = currentTimeMs,
                QueueImbalance = 0m,
                BidDepth4Level = 0m,
                AskDepth4Level = 0m,
                Spread = 0m,
                MidPrice = 0m,
                SpoofScore = 0m,
                TapeAcceleration = 0m
            };
            
            // DO NOT store invalid snapshot - prevent downstream signal generation
            // Also clear any stale cached snapshot for this symbol to prevent reuse
            _snapshots.Remove(book.Symbol, out _);
            
            return invalidSnapshot;
        }

        // Store the order book state for external access
        _orderBooks[book.Symbol] = book;
        
        var snapshot = new MetricSnapshot
        {
            Symbol = book.Symbol,
            TimestampMs = currentTimeMs
        };
        
        // Compute queue imbalance
        snapshot.BidDepth4Level = book.TotalBidSize4Level;
        snapshot.AskDepth4Level = book.TotalAskSize4Level;
        
        if (snapshot.AskDepth4Level > 0)
        {
            snapshot.QueueImbalance = snapshot.BidDepth4Level / snapshot.AskDepth4Level;
        }
        else
        {
            snapshot.QueueImbalance = decimal.MaxValue; // Infinite imbalance if no ask side
        }
        
        // Wall persistence
        snapshot.BidWallAgeMs = book.BestBidAgeMs;
        snapshot.AskWallAgeMs = book.BestAskAgeMs;
        
        // Tape acceleration
        snapshot.TradesIn3Sec = book.RecentTrades.Count;
        snapshot.TapeAcceleration = ComputeTapeAcceleration(book);
        
        // Spread
        snapshot.Spread = book.Spread;
        snapshot.MidPrice = (book.BestBid + book.BestAsk) / 2;
        
        // Absorption rate (estimate from recent trades)
        ComputeAbsorptionRates(book, snapshot);
        
        // Spoof score
        ComputeSpoofScore(book, snapshot);
        
        _snapshots[book.Symbol] = snapshot;
        
        return snapshot;
    }
    
    /// <summary>
    /// Check if this symbol has a buy-side liquidity failure signal
    /// </summary>
    public bool IsBuyLiquidityFailure(MetricSnapshot snapshot)
    {
        // Trigger when:
        // - QueueImbalance >= 2.8 (strong bid imbalance)
        // - BidWall persistent >= 1000ms
        // - BidAbsorption high (orders being filled quickly)
        // - TapeAcceleration >= 2.0 (sudden trade rate spike)
        
        bool queueImbalanceHigh = snapshot.QueueImbalance >= 2.8m;
        bool wallPersistent = snapshot.HasPersistentBidWall;
        bool tapeAccelerating = snapshot.TapeAcceleration >= TAPE_ACCELERATION_THRESHOLD;
        bool absorptionHigh = snapshot.BidAbsorptionRate > 10m; // >10 units/sec
        
        if (queueImbalanceHigh && wallPersistent && tapeAccelerating)
        {
            _logger.LogInformation(
                "[OrderFlow] BUY SIGNAL {Symbol}: QI={QueueImbalance:F2} WallAge={WallAge}ms TapeAccel={TapeAccel:F1}x",
                snapshot.Symbol, snapshot.QueueImbalance, snapshot.BidWallAgeMs, snapshot.TapeAcceleration);
            
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Check if this symbol has a sell-side liquidity failure signal
    /// </summary>
    public bool IsSellLiquidityFailure(MetricSnapshot snapshot)
    {
        // Trigger when:
        // - QueueImbalance <= 0.35 (strong ask imbalance)
        // - AskWall persistent >= 1000ms
        // - AskAbsorption high
        // - TapeAcceleration >= 2.0
        
        bool queueImbalanceLow = snapshot.QueueImbalance <= 0.35m;
        bool wallPersistent = snapshot.HasPersistentAskWall;
        bool tapeAccelerating = snapshot.TapeAcceleration >= TAPE_ACCELERATION_THRESHOLD;
        bool absorptionHigh = snapshot.AskAbsorptionRate > 10m;
        
        if (queueImbalanceLow && wallPersistent && tapeAccelerating)
        {
            _logger.LogInformation(
                "[OrderFlow] SELL SIGNAL {Symbol}: QI={QueueImbalance:F2} WallAge={WallAge}ms TapeAccel={TapeAccel:F1}x",
                snapshot.Symbol, snapshot.QueueImbalance, snapshot.AskWallAgeMs, snapshot.TapeAcceleration);
            
            return true;
        }
        
        return false;
    }
    
    private decimal ComputeTapeAcceleration(OrderBookState book)
    {
        if (book.RecentTrades.Count < 2)
            return 0m;
        
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window3SecAgo = now - TAPE_WINDOW_MS;
        
        // Count trades in current and previous window
        var tradesNow = book.RecentTrades.Where(t => t.TimestampMs >= window3SecAgo).Count();
        var tradesPrevious = book.RecentTrades.Count - tradesNow;
        
        if (tradesPrevious == 0)
            return tradesNow > 0 ? 1m : 0m;
        
        return (decimal)tradesNow / (decimal)tradesPrevious;
    }
    
    private void ComputeAbsorptionRates(OrderBookState book, MetricSnapshot snapshot)
    {
        if (book.RecentTrades.Count < 2)
            return;
        
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window1Sec = now - 1000;
        
        var recentTrades = book.RecentTrades.Where(t => t.TimestampMs >= window1Sec).ToList();
        
        if (recentTrades.Count == 0)
            return;
        
        var totalBidTradeSize = recentTrades.Where(t => t.Price <= (double)book.BestBid).Sum(t => (decimal)t.Size);
        var totalAskTradeSize = recentTrades.Where(t => t.Price >= (double)book.BestAsk).Sum(t => (decimal)t.Size);
        
        // Absorption rate in units/sec
        snapshot.BidAbsorptionRate = totalBidTradeSize; // Already over 1 second window
        snapshot.AskAbsorptionRate = totalAskTradeSize;
    }
    
    private void ComputeSpoofScore(OrderBookState book, MetricSnapshot snapshot)
    {
        if (book.RecentTrades.Count < 5)
        {
            snapshot.SpoofScore = 0.5m; // Neutral if insufficient data
            return;
        }
        
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window5Sec = now - 5000;
        
        var recentTrades = book.RecentTrades.Where(t => t.TimestampMs >= window5Sec).ToList();
        
        if (recentTrades.Count < 2)
        {
            snapshot.SpoofScore = 0.5m;
            return;
        }
        
        // Estimate: high spike followed by cancellation = spoofing
        // For now, use trade variance as proxy
        var sizes = recentTrades.Select(t => (double)t.Size).ToList();
        var avgSize = sizes.Average();
        var maxSize = sizes.Max();
        
        // If max size >> average, likely spoofing
        var sizeRatio = maxSize / avgSize;
        
        // Score: 0.0 = definitely spoofing, 1.0 = definitely real
        snapshot.SpoofScore = (decimal)Math.Max(0, Math.Min(1, 2.0 - sizeRatio));
    }
    
    public MetricSnapshot? GetLatestSnapshot(string symbol)
    {
        if (!_snapshots.TryGetValue(symbol, out var snap))
        {
            return null;
        }
        
        // HARD GATE FIX #1: Secondary safety check - verify snapshot is not zeroed (indicates invalid book)
        // Zeroed snapshots must never be used for signal generation
        if (snap.QueueImbalance == 0m && snap.TapeAcceleration == 0m && snap.Spread == 0m)
        {
            _logger.LogWarning("[Metrics Safety] Rejecting zeroed snapshot for {Symbol} - likely from invalid book", symbol);
            return null; // Prevent downstream signal generation
        }
        
        return snap;
    }
    
    /// <summary>
    /// Get order book snapshot for a symbol (for testing/monitoring)
    /// </summary>
    public OrderBookState? GetOrderBookSnapshot(string symbol)
    {
        return _orderBooks.TryGetValue(symbol, out var book) ? book : null;
    }
    
    /// <summary>
    /// Get list of all subscribed symbols with order book data
    /// </summary>
    public List<string> GetSubscribedSymbols()
    {
        return _orderBooks.Keys.OrderBy(x => x).ToList();
    }
}
