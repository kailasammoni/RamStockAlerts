namespace RamStockAlerts.Execution.Interfaces;

using RamStockAlerts.Execution.Contracts;

/// <summary>
/// Interface for broker order placement and management.
/// </summary>
public interface IBrokerClient
{
    /// <summary>
    /// Name of the broker.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Place a single order.
    /// </summary>
    Task<ExecutionResult> PlaceAsync(OrderIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Place a bracket order (entry + stop-loss + take-profit as a group).
    /// </summary>
    Task<ExecutionResult> PlaceBracketAsync(BracketIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Cancel an order by broker order ID.
    /// </summary>
    Task<ExecutionResult> CancelAsync(string brokerOrderId, CancellationToken ct = default);
}
