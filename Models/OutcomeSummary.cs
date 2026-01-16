namespace RamStockAlerts.Models;

/// <summary>
/// Summary statistics for a single day of outcomes.
/// Accumulated into the daily rollup report.
/// </summary>
public sealed class OutcomeSummary
{
    /// <summary>
    /// Date (UTC) of the outcomes.
    /// </summary>
    public DateOnly DateUtc { get; set; }

    /// <summary>
    /// Total number of accepted signals for this day.
    /// </summary>
    public int TotalSignals { get; set; }

    /// <summary>
    /// Number of signals that hit target.
    /// </summary>
    public int HitTargetCount { get; set; }

    /// <summary>
    /// Number of signals that hit stop.
    /// </summary>
    public int HitStopCount { get; set; }

    /// <summary>
    /// Number of signals with no exit (still open or not yet labeled).
    /// </summary>
    public int NoExitCount { get; set; }

    /// <summary>
    /// Number of signals still waiting for outcome (no exit recorded).
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>
    /// Total P&amp;L in USD across all closed positions.
    /// </summary>
    public decimal TotalPnlUsd { get; set; }

    /// <summary>
    /// Sum of all risk multiples for closed trades.
    /// </summary>
    public decimal SumRiskMultiples { get; set; }

    /// <summary>
    /// Count of closed trades used in P&amp;L calculation.
    /// </summary>
    public int ClosedTradeCount { get; set; }

    /// <summary>
    /// Average risk multiple: SumRiskMultiples / ClosedTradeCount.
    /// Positive = average profit in multiples. Negative = average loss.
    /// </summary>
    public decimal? AvgRiskMultiple => ClosedTradeCount > 0 ? SumRiskMultiples / ClosedTradeCount : null;

    /// <summary>
    /// Win rate: HitTargetCount / (HitTargetCount + HitStopCount) if any closed.
    /// </summary>
    public decimal? WinRate =>
        (HitTargetCount + HitStopCount) > 0
            ? (decimal)HitTargetCount / (HitTargetCount + HitStopCount)
            : null;

    /// <summary>
    /// Expectancy: (win_rate * avg_win_size) - ((1 - win_rate) * avg_loss_size).
    /// For now, approximated as (win_rate * avg_win_r) + ((1 - win_rate) * avg_loss_r).
    /// </summary>
    public decimal? Expectancy => null; // TODO: Calculate from closed trades

    /// <summary>
    /// Timestamp when this summary was generated.
    /// </summary>
    public DateTimeOffset GeneratedUtc { get; set; }

    /// <summary>
    /// Schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;
}
