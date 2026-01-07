using RamStockAlerts.Feeds;
using RamStockAlerts.Models;

namespace RamStockAlerts.Engine;

/// <summary>
/// Generates trade entry, stop, target, and liquidity score.
/// </summary>
public class TradeBlueprint
{
    private readonly IServiceProvider _serviceProvider;

    public TradeBlueprint(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Generate a trade blueprint from market data.
    /// </summary>
    /// <param name="ticker">Symbol ticker</param>
    /// <param name="lastPrice">Current price</param>
    /// <param name="lastAsk">Current ask price for entry</param>
    /// <param name="vwap">VWAP price</param>
    /// <param name="spread">Bid-ask spread</param>
    /// <param name="liquidityScore">Pre-calculated liquidity score</param>
    /// <returns>TradeSignal with entry, stop, target</returns>
    /// <exception cref="InvalidOperationException">Thrown when liquidityScore is below 7.5 or price <= vwap</exception>
    /// <exception cref="ArgumentException">Thrown when spread or price is invalid</exception>
    public TradeSignal Generate(string ticker, decimal lastPrice, decimal lastAsk, decimal vwap, decimal spread, decimal liquidityScore)
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

        // Reject entries below VWAP
        if (lastPrice <= vwap)
        {
            throw new InvalidOperationException($"Price {lastPrice} is below or equal to VWAP {vwap}");
        }

        // Reject low liquidity setups (production threshold)
        if (liquidityScore < 7.5m)
        {
            throw new InvalidOperationException($"Liquidity score {liquidityScore} is below minimum threshold of 7.5");
        }

        // Validate spread against rolling 95th percentile
        var alpacaClient = _serviceProvider.GetRequiredService<AlpacaStreamClient>();
        var symbolState = alpacaClient.GetSymbolState(ticker);
        if (symbolState != null)
        {
            var percentile95 = symbolState.GetSpread95thPercentile();
            if (percentile95.HasValue && spread > percentile95.Value)
            {
                throw new InvalidOperationException(
                    $"Spread {spread:P2} exceeds 95th percentile {percentile95.Value:P2} for {ticker}");
            }
        }

        // Calculate entry, stop, and target
        // Entry = Last Ask (not mid-price)
        // Stop = Entry - (spread * 4) - risk management
        // Target = Entry + (spread * 8) - 2:1 reward-to-risk
        var entry = lastAsk;
        var stop = entry - (spread * 4);
        var target = entry + (spread * 8);

        // Calculate position size with 0.25% account risk cap
        // TODO: Account balance should come from broker API or configuration
        // For now, use placeholder value
        decimal accountBalance = 50000m; // TODO: Get from configuration or broker API

        var riskPerShare = entry - stop;
        var maxRiskDollars = accountBalance * 0.0025m; // 0.25% of account
        var positionSize = riskPerShare > 0 
            ? (int)(maxRiskDollars / riskPerShare)
            : 0;

        return new TradeSignal
        {
            Ticker = ticker,
            Entry = entry,
            Stop = stop,
            Target = target,
            Score = liquidityScore,
            PositionSize = positionSize,
            Timestamp = DateTime.UtcNow
        };
    }
}
