namespace RamStockAlerts.Models;

/// <summary>
/// Represents the labeled outcome of a trade that was accepted as a signal.
/// Used for post-trade analysis, performance metrics, and edge validation.
/// 
/// Schema Version: 1 (immutable JSONL format for audit trail).
/// </summary>
public sealed class TradeOutcome
{
    /// <summary>
    /// Unique identifier linking this outcome to the accepted TradeJournalEntry (DecisionId).
    /// </summary>
    public Guid DecisionId { get; set; }

    /// <summary>
    /// Symbol of the trade (e.g., "AAPL").
    /// </summary>
    public string? Symbol { get; set; }

    /// <summary>
    /// Direction of the original signal ("Long" or "Short").
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Entry price used in the blueprint or actual fill (if live execution).
    /// </summary>
    public decimal? EntryPrice { get; set; }

    /// <summary>
    /// Stop-loss price from the blueprint.
    /// </summary>
    public decimal? StopPrice { get; set; }

    /// <summary>
    /// Target price (first profit target) from the blueprint.
    /// </summary>
    public decimal? TargetPrice { get; set; }

    /// <summary>
    /// Exit price where the trade was closed.
    /// TODO: Sourced from IBKR execution API or manual entry when available.
    /// </summary>
    public decimal? ExitPrice { get; set; }

    /// <summary>
    /// UTC timestamp when the outcome was labeled.
    /// </summary>
    public DateTimeOffset OutcomeLabeledUtc { get; set; }

    /// <summary>
    /// Time-indexed exit: "HitTarget", "HitStop", "NoHit", "NoExit".
    /// </summary>
    public string? OutcomeType { get; set; }

    /// <summary>
    /// Duration from entry to exit in seconds.
    /// NULL if still open.
    /// </summary>
    public long? DurationSeconds { get; set; }

    /// <summary>
    /// Raw P&amp;L in dollars (exit_price - entry_price) * shares.
    /// TODO: Populated when ExitPrice is available.
    /// </summary>
    public decimal? PnlUsd { get; set; }

    /// <summary>
    /// Risk multiple: (profit) / (risk per share).
    /// Positive means profitable; negative means loss.
    /// Example: +2.0 = made 2R; -1.0 = lost 1R (hit stop)
    /// </summary>
    public decimal? RiskMultiple { get; set; }

    /// <summary>
    /// Win flag: true if exit_price > entry_price (long) or exit_price &lt; entry_price (short).
    /// </summary>
    public bool? IsWin { get; set; }

    /// <summary>
    /// Data quality flags or notes, e.g., "PartialBook", "StaleTick", "ManualEntry".
    /// </summary>
    public List<string>? QualityFlags { get; set; }

    /// <summary>
    /// Schema version for deterministic parsing.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;
}
