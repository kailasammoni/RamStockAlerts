namespace RamStockAlerts.Execution.Storage;

using System.Collections.Concurrent;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// In-memory, thread-safe execution ledger for audit trail and diagnostics.
/// </summary>
public class InMemoryExecutionLedger : IExecutionLedger
{
    private readonly ConcurrentBag<OrderIntent> _intents = new();
    private readonly ConcurrentBag<BracketIntent> _brackets = new();
    private readonly ConcurrentBag<ExecutionResult> _results = new();
    private readonly object _lock = new();

    /// <summary>
    /// Record an order intent.
    /// </summary>
    public void RecordIntent(OrderIntent intent)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        lock (_lock)
        {
            _intents.Add(intent);
        }
    }

    /// <summary>
    /// Record a bracket intent.
    /// </summary>
    public void RecordBracket(BracketIntent intent)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        lock (_lock)
        {
            _brackets.Add(intent);
        }
    }

    /// <summary>
    /// Record an execution result.
    /// </summary>
    public void RecordResult(Guid intentId, ExecutionResult result)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));

        lock (_lock)
        {
            if (result.IntentId == Guid.Empty)
            {
                result.IntentId = intentId;
            }
            _results.Add(result);
        }
    }

    /// <summary>
    /// Get all recorded intents (read-only).
    /// </summary>
    public IReadOnlyList<OrderIntent> GetIntents()
    {
        lock (_lock)
        {
            return _intents.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Get all recorded bracket intents (read-only).
    /// </summary>
    public IReadOnlyList<BracketIntent> GetBrackets()
    {
        lock (_lock)
        {
            return _brackets.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Get all recorded results (read-only).
    /// </summary>
    public IReadOnlyList<ExecutionResult> GetResults()
    {
        lock (_lock)
        {
            return _results.ToList().AsReadOnly();
        }
    }
}
