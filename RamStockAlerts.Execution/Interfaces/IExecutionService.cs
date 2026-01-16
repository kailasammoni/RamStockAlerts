namespace RamStockAlerts.Execution.Interfaces;

using RamStockAlerts.Execution.Contracts;

/// <summary>
/// Interface for the main execution orchestrator service.
/// </summary>
public interface IExecutionService
{
    /// <summary>
    /// Execute a single order intent.
    /// </summary>
    Task<ExecutionResult> ExecuteAsync(OrderIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Execute a bracket intent.
    /// </summary>
    Task<ExecutionResult> ExecuteAsync(BracketIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Cancel an order by broker order ID.
    /// </summary>
    Task<ExecutionResult> CancelAsync(string brokerOrderId, CancellationToken ct = default);
}
