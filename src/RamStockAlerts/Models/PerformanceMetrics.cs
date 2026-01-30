namespace RamStockAlerts.Models;

/// <summary>
/// Performance metrics calculated from labeled trade outcomes.
/// Used in daily rollup reports to show trading edge metrics.
/// </summary>
public sealed class PerformanceMetrics
{
    /// <summary>
    /// Number of trades that hit the target.
    /// </summary>
    public int WinCount { get; set; }

    /// <summary>
    /// Number of trades that hit the stop loss.
    /// </summary>
    public int LossCount { get; set; }

    /// <summary>
    /// Win rate: WinCount / (WinCount + LossCount).
    /// Range [0, 1]. Null if no closed trades.
    /// </summary>
    public decimal? WinRate => (WinCount + LossCount) > 0 
        ? (decimal)WinCount / (WinCount + LossCount) 
        : null;

    /// <summary>
    /// Average risk multiple for winning trades (positive R).
    /// Only includes HitTarget outcomes.
    /// </summary>
    public decimal? AvgWinRMultiple { get; set; }

    /// <summary>
    /// Average risk multiple for losing trades (negative R).
    /// Only includes HitStop outcomes. Returned as positive value.
    /// </summary>
    public decimal? AvgLossRMultiple { get; set; }

    /// <summary>
    /// Expectancy: (win_rate * avg_win_r) - ((1 - win_rate) * avg_loss_r).
    /// Represents expected R per trade.
    /// </summary>
    public decimal? Expectancy 
    { 
        get
        {
            if (AvgWinRMultiple == null || AvgLossRMultiple == null || WinRate == null)
                return null;

            return (WinRate.Value * AvgWinRMultiple.Value) - ((1 - WinRate.Value) * AvgLossRMultiple.Value);
        }
    }

    /// <summary>
    /// Total P&L in USD across all closed trades.
    /// </summary>
    public decimal TotalPnlUsd { get; set; }

    /// <summary>
    /// Maximum peak-to-trough drawdown in USD.
    /// </summary>
    public decimal? MaxDrawdownUsd { get; set; }

    /// <summary>
    /// Maximum drawdown as a percentage of peak equity.
    /// </summary>
    public decimal? MaxDrawdownPercent { get; set; }

    /// <summary>
    /// Total number of accepted signals (whether closed or open).
    /// </summary>
    public int TotalSignals { get; set; }

    /// <summary>
    /// Number of still-open trades (NoExit outcome).
    /// </summary>
    public int OpenCount { get; set; }

    /// <summary>
    /// Number of trades that exited but didn't hit target/stop (NoHit outcome).
    /// </summary>
    public int NoHitCount { get; set; }

    /// <summary>
    /// Percentage of trades that closed profitably.
    /// </summary>
    public decimal? ClosureRate => (WinCount + LossCount) > 0
        ? (decimal)WinCount / (WinCount + LossCount)
        : null;

    public string? Summary()
    {
        if (TotalSignals == 0)
            return "No outcomes to report";

        var winRate = WinRate.HasValue
            ? (WinRate.Value * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%"
            : "N/A";
        var pnl = TotalPnlUsd > 0
            ? $"+${TotalPnlUsd.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"${TotalPnlUsd.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";
        var exp = Expectancy?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A";

        return $"Wins: {WinCount} | Losses: {LossCount} | Open: {OpenCount} | " +
               $"WinRate: {winRate} | PnL: {pnl} | Expectancy: {exp}R";
    }
}
