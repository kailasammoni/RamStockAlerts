namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Represents an intent to place an order.
/// </summary>
public class OrderIntent
{
    /// <summary>
    /// Unique identifier for this intent.
    /// </summary>
    public Guid IntentId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Optional decision ID that led to this intent (e.g., from a signal/decision engine).
    /// </summary>
    public Guid? DecisionId { get; set; }

    /// <summary>
    /// Trading mode (Shadow, Paper, Live).
    /// </summary>
    public TradingMode Mode { get; set; }

    /// <summary>
    /// Symbol to trade (e.g., "AAPL").
    /// </summary>
    public string? Symbol { get; set; }

    /// <summary>
    /// Order side (Buy or Sell).
    /// </summary>
    public OrderSide Side { get; set; }

    /// <summary>
    /// Order type (Market, Limit, Stop, StopLimit).
    /// </summary>
    public OrderType Type { get; set; }

    /// <summary>
    /// Quantity in shares (nullable to allow notional-based sizing later).
    /// </summary>
    public decimal? Quantity { get; set; }

    /// <summary>
    /// Notional USD amount (nullable to allow share-based sizing).
    /// </summary>
    public decimal? NotionalUsd { get; set; }

    /// <summary>
    /// Limit price (for Limit and StopLimit orders).
    /// </summary>
    public decimal? LimitPrice { get; set; }

    /// <summary>
    /// Stop price (for Stop and StopLimit orders).
    /// </summary>
    public decimal? StopPrice { get; set; }

    /// <summary>
    /// Time in force (Day or GTC).
    /// </summary>
    public TimeInForce Tif { get; set; } = TimeInForce.Day;

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional metadata tags for tracking/routing.
    /// </summary>
    public Dictionary<string, string>? Tags { get; set; }
}
