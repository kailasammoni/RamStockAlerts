namespace RamStockAlerts.Execution.Contracts;

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

/// <summary>
/// Bracket order lifecycle state.
/// </summary>
public enum BracketState
{
    Pending,
    Open,
    ClosedWin,
    ClosedLoss,
    Cancelled,
    Error
}
