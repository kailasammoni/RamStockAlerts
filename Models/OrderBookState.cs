namespace RamStockAlerts.Models;

/// <summary>
/// Real-time order book state for a single symbol
/// Maintains bid/ask depth with timestamp tracking for wall persistence detection
/// </summary>
public class OrderBookState
{
    public string Symbol { get; set; } = string.Empty;
    
    /// <summary>
    /// Bid side depth: price -> (size, timestamp)
    /// Index 0 = best bid, Index 1-9 = deeper levels
    /// </summary>
    public SortedDictionary<decimal, (decimal Size, long TimestampMs)> BidDepth { get; set; } = new();
    
    /// <summary>
    /// Ask side depth: price -> (size, timestamp)
    /// Index 0 = best ask, Index 1-9 = deeper levels
    /// </summary>
    public SortedDictionary<decimal, (decimal Size, long TimestampMs)> AskDepth { get; set; } = new();
    
    /// <summary>
    /// Recent trades: timestamp -> (price, size)
    /// Keep last 100 ticks for acceleration detection
    /// </summary>
    public Queue<(long TimestampMs, double Price, decimal Size)> RecentTrades { get; set; } = new(100);
    
    /// <summary>
    /// Best bid price
    /// </summary>
    public decimal BestBid => BidDepth.Any() ? BidDepth.Keys.Max() : 0m;
    
    /// <summary>
    /// Best ask price
    /// </summary>
    public decimal BestAsk => AskDepth.Any() ? AskDepth.Keys.Min() : 0m;
    
    /// <summary>
    /// Spread in cents
    /// </summary>
    public decimal Spread => BestAsk > 0 && BestBid > 0 ? (BestAsk - BestBid) * 100 : 0m;
    
    /// <summary>
    /// Last update timestamp
    /// </summary>
    public long LastUpdateMs { get; set; }
    
    /// <summary>
    /// Sum of bid sizes at best 4 levels
    /// </summary>
    public decimal TotalBidSize4Level
    {
        get
        {
            return BidDepth.Values
                .Take(4)
                .Sum(x => x.Size);
        }
    }
    
    /// <summary>
    /// Sum of ask sizes at best 4 levels
    /// </summary>
    public decimal TotalAskSize4Level
    {
        get
        {
            return AskDepth.Values
                .Take(4)
                .Sum(x => x.Size);
        }
    }
    
    /// <summary>
    /// Update bid depth at a specific level
    /// </summary>
    public void UpdateBidDepth(decimal price, decimal size, long timestampMs)
    {
        if (size <= 0)
        {
            BidDepth.Remove(price);
        }
        else
        {
            BidDepth[price] = (size, timestampMs);
        }
        LastUpdateMs = timestampMs;
    }
    
    /// <summary>
    /// Update ask depth at a specific level
    /// </summary>
    public void UpdateAskDepth(decimal price, decimal size, long timestampMs)
    {
        if (size <= 0)
        {
            AskDepth.Remove(price);
        }
        else
        {
            AskDepth[price] = (size, timestampMs);
        }
        LastUpdateMs = timestampMs;
    }
    
    /// <summary>
    /// Record a trade execution
    /// </summary>
    public void RecordTrade(long timestampMs, double price, decimal size)
    {
        RecentTrades.Enqueue((timestampMs, price, size));
        
        // Keep only last 100 trades
        while (RecentTrades.Count > 100)
        {
            RecentTrades.Dequeue();
        }
        
        LastUpdateMs = timestampMs;
    }
    
    /// <summary>
    /// Age of best bid in milliseconds
    /// </summary>
    public long BestBidAgeMs
    {
        get
        {
            if (!BidDepth.Any())
                return long.MaxValue;
            
            var bestBidTimestamp = BidDepth.Values.First().TimestampMs;
            return LastUpdateMs - bestBidTimestamp;
        }
    }
    
    /// <summary>
    /// Age of best ask in milliseconds
    /// </summary>
    public long BestAskAgeMs
    {
        get
        {
            if (!AskDepth.Any())
                return long.MaxValue;
            
            var bestAskTimestamp = AskDepth.Values.First().TimestampMs;
            return LastUpdateMs - bestAskTimestamp;
        }
    }
    
    /// <summary>
    /// Clear old trades outside the window
    /// </summary>
    public void PruneTrades(long windowStartMs)
    {
        while (RecentTrades.Count > 0 && RecentTrades.Peek().TimestampMs < windowStartMs)
        {
            RecentTrades.Dequeue();
        }
    }
}
