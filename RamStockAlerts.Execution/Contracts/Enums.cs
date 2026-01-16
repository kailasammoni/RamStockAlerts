namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Trading mode for execution context.
/// </summary>
public enum TradingMode
{
    Shadow,
    Paper,
    Live
}

/// <summary>
/// Order side (direction).
/// </summary>
public enum OrderSide
{
    Buy,
    Sell
}

/// <summary>
/// Order type.
/// </summary>
public enum OrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

/// <summary>
/// Time in force for an order.
/// </summary>
public enum TimeInForce
{
    Day,
    Gtc
}

/// <summary>
/// Execution status of an order.
/// </summary>
public enum ExecutionStatus
{
    Accepted,
    Rejected,
    Submitted,
    Filled,
    Cancelled,
    Error
}
