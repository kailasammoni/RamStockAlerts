namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Represents a risk validation decision.
/// </summary>
public class RiskDecision
{
    /// <summary>
    /// Whether the order/bracket is allowed to proceed.
    /// </summary>
    public bool Allowed { get; set; }

    /// <summary>
    /// If not allowed, the reason for rejection.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Categorical tags for the decision (e.g., "DailyLimit", "KillSwitch", "Cooldown", "MaxLoss").
    /// Useful for metrics, routing, and audit trails.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Create an allowed decision.
    /// </summary>
    public static RiskDecision Allow(List<string>? tags = null)
    {
        return new() { Allowed = true, Tags = tags ?? new() };
    }

    /// <summary>
    /// Create a rejected decision with a reason and optional tags.
    /// </summary>
    public static RiskDecision Reject(string reason, List<string>? tags = null)
    {
        return new() { Allowed = false, Reason = reason, Tags = tags ?? new() };
    }
}
