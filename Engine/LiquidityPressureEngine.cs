using RamStockAlerts.Models;

namespace RamStockAlerts.Engine;

/// <summary>
/// Detects bid wall persistence and liquidity imbalance from OrderBook snapshots.
/// All functions are pure and deterministic with no IO or static state.
/// </summary>
public class LiquidityPressureEngine
{
    private readonly List<OrderBookSnapshot> _snapshots = new();

    /// <summary>
    /// Represents a timestamped OrderBook snapshot.
    /// </summary>
    public class OrderBookSnapshot
    {
        public OrderBook Book { get; init; } = null!;
        public DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Add an OrderBook snapshot to the stream for analysis.
    /// </summary>
    /// <param name="book">Current order book state</param>
    /// <param name="timestamp">UTC timestamp of the snapshot</param>
    public void AddSnapshot(OrderBook book, DateTime timestamp)
    {
        _snapshots.Add(new OrderBookSnapshot
        {
            Book = book,
            Timestamp = timestamp
        });
    }

    /// <summary>
    /// Detect if a bid wall has persisted for at least the specified threshold.
    /// A bid wall is defined as BidAskRatio >= 3.0
    /// </summary>
    /// <param name="msThreshold">Minimum persistence time in milliseconds</param>
    /// <returns>True if bid wall has persisted for >= msThreshold</returns>
    public bool HasBidWall(int msThreshold)
    {
        if (_snapshots.Count == 0)
        {
            return false;
        }

        if (msThreshold < 0)
        {
            throw new ArgumentException("Threshold must be non-negative", nameof(msThreshold));
        }

        // Find the most recent continuous sequence of bid walls
        var now = _snapshots[^1].Timestamp;
        var persistenceStartTime = now;

        // Walk backwards from most recent snapshot
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            var snapshot = _snapshots[i];

            // Check if this snapshot has a bid wall (BidAskRatio >= 3.0)
            if (snapshot.Book.BidAskRatio >= 3.0m)
            {
                persistenceStartTime = snapshot.Timestamp;
            }
            else
            {
                // Bid wall broken, stop looking backwards
                break;
            }
        }

        // Calculate how long the bid wall has persisted
        var persistenceDuration = now - persistenceStartTime;
        return persistenceDuration.TotalMilliseconds >= msThreshold;
    }

    /// <summary>
    /// Calculate a bid wall persistence score from 0 to 10.
    /// Based on continuous bid wall duration and strength.
    /// </summary>
    /// <returns>Score from 0 to 10</returns>
    public decimal BidWallPersistenceScore()
    {
        if (_snapshots.Count == 0)
        {
            return 0m;
        }

        var now = _snapshots[^1].Timestamp;
        var persistenceStartTime = now;
        decimal avgRatio = 0m;
        int bidWallCount = 0;

        // Walk backwards to find continuous bid wall sequence
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            var snapshot = _snapshots[i];

            if (snapshot.Book.BidAskRatio >= 3.0m)
            {
                persistenceStartTime = snapshot.Timestamp;
                avgRatio += snapshot.Book.BidAskRatio;
                bidWallCount++;
            }
            else
            {
                break;
            }
        }

        if (bidWallCount == 0)
        {
            return 0m;
        }

        avgRatio /= bidWallCount;
        var persistenceDuration = now - persistenceStartTime;
        var persistenceSeconds = (decimal)persistenceDuration.TotalSeconds;

        // Score components:
        // - Duration: 0-5 points (1 point per second, max 5)
        // - Ratio strength: 0-5 points (ratio >= 3 = 1pt, >= 4 = 2pt, >= 5 = 3pt, >= 6 = 4pt, >= 7 = 5pt)
        decimal durationScore = Math.Min(5m, persistenceSeconds);
        
        decimal ratioScore = avgRatio switch
        {
            >= 7m => 5m,
            >= 6m => 4m,
            >= 5m => 3m,
            >= 4m => 2m,
            >= 3m => 1m,
            _ => 0m
        };

        return Math.Min(10m, durationScore + ratioScore);
    }

    /// <summary>
    /// Clear all snapshots from memory.
    /// </summary>
    public void Clear()
    {
        _snapshots.Clear();
    }
}
