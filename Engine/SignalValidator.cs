using RamStockAlerts.Models;

namespace RamStockAlerts.Engine;

/// <summary>
/// Computes liquidity confidence score for trade setups.
/// All functions are pure and deterministic.
/// </summary>
public class SignalValidator
{
    private readonly TimeZoneInfo _eastern;

    public SignalValidator()
    {
        _eastern = TryGetEasternTimeZone();
    }

    /// <summary>
    /// Calculate liquidity score from 0 to 10 based on market conditions.
    /// </summary>
    /// <param name="book">Order book data</param>
    /// <param name="tape">Tape/time and sales data</param>
    /// <param name="vwap">VWAP data</param>
    /// <param name="spread">Current bid-ask spread</param>
    /// <returns>Liquidity score between 0 and 10</returns>
    public decimal CalculateLiquidityScore(OrderBook book, TapeData tape, VwapData vwap, decimal spread)
    {
        // Edge case: zero sizes means no liquidity
        if (book.TotalAskSize == 0 || book.TotalBidSize == 0)
        {
            return 0m;
        }

        // Edge case: negative spread is invalid
        if (spread < 0)
        {
            return 0m;
        }

        // Edge case: very wide spread means poor liquidity
        if (spread > 0.06m)
        {
            return Math.Min(2m, CalculateBaseScore(book, tape, vwap, spread));
        }

        return CalculateBaseScore(book, tape, vwap, spread);
    }

    private decimal CalculateBaseScore(OrderBook book, TapeData tape, VwapData vwap, decimal spread)
    {
        decimal score = 0m;

        // Tight spread: +2 points (spread <= 0.03)
        if (spread <= 0.03m)
        {
            score += 2m;
        }

        // Strong bid/ask ratio: +3 points (ratio >= 3)
        if (book.BidAskRatio >= 3m)
        {
            score += 3m;
        }
        else if (book.BidAskRatio < 1m)
        {
            // Weak ratio penalty - cap score at 3
            return Math.Min(3m, score);
        }

        // Active tape: +2 points (prints per second >= 5)
        if (tape.PrintsPerSecond >= 5m)
        {
            score += 2m;
        }

        // VWAP reclaim: +2 points
        if (vwap.HasReclaim)
        {
            score += 2m;
        }

        // Bid dominance: +1 point
        if (book.TotalBidSize > book.TotalAskSize)
        {
            score += 1m;
        }

        return Math.Min(10m, score);
    }

    /// <summary>
    /// Determines if a score represents a valid liquidity setup.
    /// </summary>
    /// <param name="score">The liquidity score</param>
    /// <returns>True if score >= 7.5</returns>
    public bool IsValidSetup(decimal score) => score >= 7.5m;

    /// <summary>
    /// Time-aware validation with optional anti-spoofing heuristics.
    /// </summary>
    public bool IsValidSetup(OrderBook book, TapeData tape, VwapData vwap, decimal spread, DateTime utcNow, decimal? previousSpread = null)
    {
        if (IsSpoofing(book, tape, spread, previousSpread))
        {
            return false;
        }

        if (!IsWithinOperatingWindow(utcNow))
        {
            return false;
        }

        var score = CalculateLiquidityScore(book, tape, vwap, spread);
        return score >= GetThresholdForTime(utcNow);
    }

    private bool IsWithinOperatingWindow(DateTime utcNow)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _eastern);

        // Graceful shutdown after 15:45 ET
        if (eastern.Hour > 15 || (eastern.Hour == 15 && eastern.Minute >= 45)) return false;

        // Pre-market block
        if (eastern.Hour < 9 || (eastern.Hour == 9 && eastern.Minute < 25)) return false;

        return true;
    }

    private decimal GetThresholdForTime(DateTime utcNow)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _eastern);
        var minuteOfDay = eastern.Hour * 60 + eastern.Minute;

        // 09:30-11:30 high confidence window
        if (minuteOfDay is >= 570 and <= 690)
        {
            return 7.0m;
        }

        // 12:00-14:00 low-confidence window (require higher score)
        if (minuteOfDay is >= 720 and <= 840)
        {
            return 8.0m;
        }

        // Other allowed times
        return 7.5m;
    }

    private static TimeZoneInfo TryGetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private bool IsSpoofing(OrderBook book, TapeData tape, decimal spread, decimal? previousSpread)
    {
        // Rapid spread expansion vs previous tick suggests spoofing/liquidity pull
        if (previousSpread.HasValue && previousSpread.Value > 0)
        {
            var widenPct = (spread - previousSpread.Value) / previousSpread.Value;
            if (widenPct >= 0.5m)
            {
                return true;
            }
        }

        // Ask replenishment faster than fills: weak bid/ask ratio with slow tape
        if (book.BidAskRatio < 1m && tape.PrintsPerSecond < 1m)
        {
            return true;
        }

        // Extremely wide spread after trigger
        if (spread > 0.06m)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Convenience method to check if market conditions represent a valid setup.
    /// </summary>
    public bool IsLiquiditySetup(OrderBook book, TapeData tape, VwapData vwap, decimal spread)
    {
        var score = CalculateLiquidityScore(book, tape, vwap, spread);
        return IsValidSetup(score);
    }
}
