namespace RamStockAlerts.Execution.Interfaces;

using RamStockAlerts.Execution.Contracts;

/// <summary>
/// Interface for execution ledger / audit trail.
/// </summary>
public interface IExecutionLedger
{
    /// <summary>
    /// Record an order intent.
    /// </summary>
    void RecordIntent(OrderIntent intent);

    /// <summary>
    /// Record a bracket intent.
    /// </summary>
    void RecordBracket(BracketIntent intent);

    /// <summary>
    /// Record an execution result for a given intent.
    /// </summary>
    void RecordResult(Guid intentId, ExecutionResult result);

    /// <summary>
    /// Get all recorded intents.
    /// </summary>
    IReadOnlyList<OrderIntent> GetIntents();

    /// <summary>
    /// Get all recorded bracket intents.
    /// </summary>
    IReadOnlyList<BracketIntent> GetBrackets();

    /// <summary>
    /// Get all recorded results.
    /// </summary>
    IReadOnlyList<ExecutionResult> GetResults();

    /// <summary>
    /// Get count of submitted (non-rejected) intents today.
    /// </summary>
    int GetSubmittedIntentCountToday(DateTimeOffset now);

    /// <summary>
    /// Get count of submitted (non-rejected) brackets today.
    /// </summary>
    int GetSubmittedBracketCountToday(DateTimeOffset now);

    /// <summary>
    /// Get count of currently open brackets (entry filled, not yet closed).
    /// </summary>
    int GetOpenBracketCount();

    /// <summary>
    /// Update bracket state when order status changes.
    /// </summary>
    void UpdateBracketState(Guid entryIntentId, BracketState newState);
}
