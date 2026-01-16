namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Request for building a bracket order with automatic sizing and template-based levels.
/// Used to convert high-level trading intent (symbol, side, risk budget) into a concrete BracketIntent.
/// </summary>
public sealed class ExecutionRequest
{
    /// <summary>
    /// Symbol to trade.
    /// </summary>
    public required string Symbol { get; init; }

    /// <summary>
    /// Order side (Buy or Sell).
    /// </summary>
    public required OrderSide Side { get; init; }

    /// <summary>
    /// Reference price for sizing and initial levels (e.g., last price, mark price).
    /// </summary>
    public required decimal ReferencePrice { get; init; }

    /// <summary>
    /// Account equity in USD for position sizing.
    /// </summary>
    public required decimal AccountEquityUsd { get; init; }

    /// <summary>
    /// Risk per trade as a percentage of account equity (e.g., 0.0025 for 0.25%).
    /// </summary>
    public decimal RiskPerTradePct { get; init; } = 0.0025m;

    /// <summary>
    /// Maximum notional value as a percentage of account equity (e.g., 0.10 for 10%).
    /// </summary>
    public decimal MaxNotionalPct { get; init; } = 0.10m;

    /// <summary>
    /// Bracket template to use ("VOL_A" or "VOL_B").
    /// </summary>
    public required string Template { get; init; }

    /// <summary>
    /// Optional volatility proxy (e.g., spread, ATR proxy, tape acceleration).
    /// Used as "Spread" in template calculations.
    /// </summary>
    public decimal? VolatilityProxy { get; init; }

    /// <summary>
    /// Stop loss model ("FixedPct", "SpreadMultiple", or "DepthWall").
    /// Currently only "FixedPct" and "SpreadMultiple" are implemented.
    /// </summary>
    public string StopModel { get; init; } = "SpreadMultiple";
}
