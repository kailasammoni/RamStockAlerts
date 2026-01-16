namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Represents a bracket order intent with entry, take-profit, and stop-loss.
/// </summary>
public class BracketIntent
{
    /// <summary>
    /// Entry order (the primary order).
    /// </summary>
    public required OrderIntent Entry { get; set; }

    /// <summary>
    /// Optional take-profit order (OrderType.Limit recommended).
    /// </summary>
    public OrderIntent? TakeProfit { get; set; }

    /// <summary>
    /// Optional stop-loss order (OrderType.Stop or StopLimit recommended).
    /// </summary>
    public OrderIntent? StopLoss { get; set; }

    /// <summary>
    /// Optional OCO (one-cancels-other) group ID for broker grouping.
    /// </summary>
    public string? OcoGroupId { get; set; }
}
