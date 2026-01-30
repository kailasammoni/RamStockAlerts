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
    private readonly ConcurrentDictionary<Guid, BracketState> _bracketStates = new();
    private readonly ConcurrentDictionary<int, Guid> _orderDecisionIds = new();
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
            TryRecordDecisionMapping(result);

            if (IsBracketEntryIntent(result.IntentId))
            {
                if (IsSubmittedStatus(result.Status))
                {
                    _bracketStates.TryAdd(result.IntentId, BracketState.Pending);
                }
                else if (result.Status == ExecutionStatus.Rejected || result.Status == ExecutionStatus.Error)
                {
                    _bracketStates.TryAdd(result.IntentId, BracketState.Error);
                }
            }
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

    public int GetSubmittedIntentCountToday(DateTimeOffset now)
    {
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var todayEnd = todayStart.AddDays(1);

        lock (_lock)
        {
            return _results
                .Where(r => IsSubmittedStatus(r.Status)
                            && r.TimestampUtc >= todayStart
                            && r.TimestampUtc < todayEnd)
                .Select(r => r.IntentId)
                .ToHashSet()
                .Count;
        }
    }

    public int GetSubmittedBracketCountToday(DateTimeOffset now)
    {
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var todayEnd = todayStart.AddDays(1);

        lock (_lock)
        {
            var submitted = _results
                .Where(r => IsSubmittedStatus(r.Status)
                            && r.TimestampUtc >= todayStart
                            && r.TimestampUtc < todayEnd)
                .Select(r => r.IntentId)
                .ToHashSet();

            return _brackets.Count(b => submitted.Contains(b.Entry.IntentId)
                                        && b.Entry.CreatedUtc >= todayStart
                                        && b.Entry.CreatedUtc < todayEnd);
        }
    }

    public int GetOpenBracketCount()
    {
        lock (_lock)
        {
            return _bracketStates.Values.Count(state => state == BracketState.Open);
        }
    }

    public Guid? GetDecisionIdByOrderId(int orderId)
    {
        return _orderDecisionIds.TryGetValue(orderId, out var decisionId) ? decisionId : (Guid?)null;
    }

    public void UpdateBracketState(Guid entryIntentId, BracketState newState)
    {
        _bracketStates[entryIntentId] = newState;
    }

    private void TryRecordDecisionMapping(ExecutionResult result)
    {
        if (result.BrokerOrderIds is null || result.BrokerOrderIds.Count == 0)
        {
            return;
        }

        var decisionId = ResolveDecisionId(result.IntentId);
        if (!decisionId.HasValue)
        {
            return;
        }

        foreach (var brokerOrderId in result.BrokerOrderIds)
        {
            if (!int.TryParse(brokerOrderId, out var orderId))
            {
                continue;
            }

            _orderDecisionIds.TryAdd(orderId, decisionId.Value);
        }
    }

    private Guid? ResolveDecisionId(Guid intentId)
    {
        foreach (var intent in _intents)
        {
            if (intent.IntentId == intentId)
            {
                return intent.DecisionId;
            }
        }

        foreach (var bracket in _brackets)
        {
            var decisionId = FindDecisionIdFromBracket(bracket, intentId);
            if (decisionId.HasValue)
            {
                return decisionId;
            }
        }

        return null;
    }

    private static Guid? FindDecisionIdFromBracket(BracketIntent bracket, Guid intentId)
    {
        if (bracket.Entry.IntentId == intentId)
        {
            return bracket.Entry.DecisionId;
        }

        if (bracket.StopLoss?.IntentId == intentId)
        {
            return bracket.StopLoss.DecisionId;
        }

        if (bracket.TakeProfit?.IntentId == intentId)
        {
            return bracket.TakeProfit.DecisionId;
        }

        return null;
    }

    private bool IsBracketEntryIntent(Guid intentId)
    {
        return _brackets.Any(b => b.Entry.IntentId == intentId);
    }

    private static bool IsSubmittedStatus(ExecutionStatus status)
    {
        return status == ExecutionStatus.Submitted || status == ExecutionStatus.Accepted;
    }
}
