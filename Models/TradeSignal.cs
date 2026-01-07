namespace RamStockAlerts.Models;

/// <summary>
/// Represents a trade signal with entry, stop, target, and position sizing.
/// </summary>
public class TradeSignal
{
    public int Id { get; set; }
    public required string Ticker { get; set; }
    public decimal Entry { get; set; }
    public decimal Stop { get; set; }
    public decimal Target { get; set; }
    public decimal Score { get; set; }
    public DateTime Timestamp { get; set; }

    // Position sizing
    public int? PositionSize { get; set; }

    // Execution/outcome tracking fields for performance analytics
    public decimal? ExecutionPrice { get; set; }
    public DateTime? ExecutionTime { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal? PnL { get; set; }
    public SignalStatus Status { get; set; } = SignalStatus.Pending;
    public string? RejectionReason { get; set; }

    /// <summary>
    /// Alpaca order ID if order was placed.
    /// </summary>
    public string? OrderId { get; set; }

    /// <summary>
    /// Order placement timestamp.
    /// </summary>
    public DateTime? OrderPlacedAt { get; set; }

    /// <summary>
    /// Whether auto-trading was attempted for this signal.
    /// </summary>
    public bool AutoTradingAttempted { get; set; }

    /// <summary>
    /// Reason if auto-trading was skipped.
    /// </summary>
    public string? AutoTradingSkipReason { get; set; }
}

public enum SignalStatus
{
    Pending = 0,
    Filled = 1,
    Cancelled = 2,
    Rejected = 3
}
