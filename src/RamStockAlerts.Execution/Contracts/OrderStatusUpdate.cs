namespace RamStockAlerts.Execution.Contracts;

public enum BrokerOrderStatus
{
    Unknown,
    PendingSubmit,
    PreSubmitted,
    Submitted,
    Filled,
    PartiallyFilled,
    Cancelled,
    Inactive,
    Error
}

public sealed class OrderStatusUpdate
{
    public int OrderId { get; init; }
    public BrokerOrderStatus Status { get; init; }
    public decimal FilledQuantity { get; init; }
    public decimal RemainingQuantity { get; init; }
    public decimal AvgFillPrice { get; init; }
    public decimal LastFillPrice { get; init; }
    public long PermId { get; init; }
    public int ParentId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
}

public sealed class FillReport
{
    public int OrderId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal Price { get; init; }
    public string ExecId { get; init; } = string.Empty;
    public DateTimeOffset ExecutionTimeUtc { get; init; }
    public decimal? Commission { get; init; }
    public decimal? RealizedPnl { get; init; }
}
