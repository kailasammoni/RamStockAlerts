namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Represents the result of an execution attempt.
/// </summary>
public class ExecutionResult
{
    /// <summary>
    /// Status of the execution.
    /// </summary>
    public ExecutionStatus Status { get; set; }

    /// <summary>
    /// If rejected or error, the reason.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// Name of the broker that handled (or rejected) the order.
    /// </summary>
    public string? BrokerName { get; set; }

    /// <summary>
    /// Broker-assigned order IDs. May be empty if rejected.
    /// </summary>
    public List<string> BrokerOrderIds { get; set; } = new();

    /// <summary>
    /// UTC timestamp of when execution was completed/recorded.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional debug information (freeform JSON/text for diagnostics).
    /// </summary>
    public string? Debug { get; set; }
}
