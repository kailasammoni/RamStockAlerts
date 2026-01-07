using RamStockAlerts.Models;

namespace RamStockAlerts.Engine;

/// <summary>
/// Generates trade entry, stop, target, and liquidity score.
/// </summary>
public class TradeBlueprint
{
    /// <summary>
    /// Generate a trade blueprint from market data.
    /// </summary>
    /// <param name="lastPrice">Current price</param>
    /// <param name="vwap">VWAP price</param>
    /// <param name="spread">Bid-ask spread</param>
    /// <param name="liquidityScore">Pre-calculated liquidity score</param>
    /// <returns>TradeSignal with entry, stop, target</returns>
    /// <exception cref="InvalidOperationException">Thrown when liquidityScore is below 7.5</exception>
    /// <exception cref="ArgumentException">Thrown when spread or price is invalid</exception>
    public TradeSignal Generate(string ticker, decimal lastPrice, decimal vwap, decimal spread, decimal liquidityScore)
    {
        // Validate inputs
        if (spread <= 0)
        {
            throw new ArgumentException("Spread must be greater than 0", nameof(spread));
        }

        if (lastPrice <= 0)
        {
            throw new ArgumentException("Last price must be greater than 0", nameof(lastPrice));
        }

        // Reject low liquidity setups
        if (liquidityScore < 7.5m)
        {
            throw new InvalidOperationException($"Liquidity score {liquidityScore} is below minimum threshold of 7.5");
        }

        // Calculate entry, stop, and target
        // Entry = current price
        // Stop = Entry - (spread * 4) - risk management
        // Target = Entry + (spread * 8) - 2:1 reward-to-risk
        var entry = lastPrice;
        var stop = entry - (spread * 4);
        var target = entry + (spread * 8);

        return new TradeSignal
        {
            Ticker = ticker,
            Entry = entry,
            Stop = stop,
            Target = target,
            Score = liquidityScore,
            Timestamp = DateTime.UtcNow
        };
    }
}
