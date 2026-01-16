namespace RamStockAlerts.Execution.Services;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// Main execution orchestrator service.
/// Validates via risk manager, places via broker, records in ledger.
/// </summary>
public class ExecutionService : IExecutionService
{
    private readonly IRiskManager _riskManager;
    private readonly IBrokerClient _brokerClient;
    private readonly IExecutionLedger _ledger;

    public ExecutionService(
        IRiskManager riskManager,
        IBrokerClient brokerClient,
        IExecutionLedger ledger)
    {
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _brokerClient = brokerClient ?? throw new ArgumentNullException(nameof(brokerClient));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <summary>
    /// Execute a single order intent.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(OrderIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        // Record the intent
        _ledger.RecordIntent(intent);

        // Validate via risk manager
        var riskDecision = _riskManager.Validate(intent);
        if (!riskDecision.Allowed)
        {
            var result = new ExecutionResult
            {
                Status = ExecutionStatus.Rejected,
                RejectionReason = riskDecision.Reason,
                BrokerName = _brokerClient.Name,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            _ledger.RecordResult(intent.IntentId, result);
            return result;
        }

        // Place via broker
        var executionResult = await _brokerClient.PlaceAsync(intent, ct);
        
        // Ensure timestamp is set
        if (executionResult.TimestampUtc == default)
        {
            executionResult.TimestampUtc = DateTimeOffset.UtcNow;
        }

        // Record the result
        _ledger.RecordResult(intent.IntentId, executionResult);

        return executionResult;
    }

    /// <summary>
    /// Execute a bracket intent.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(BracketIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        // Record the bracket intent
        _ledger.RecordBracket(intent);

        // Validate via risk manager
        var riskDecision = _riskManager.Validate(intent);
        if (!riskDecision.Allowed)
        {
            var result = new ExecutionResult
            {
                Status = ExecutionStatus.Rejected,
                RejectionReason = riskDecision.Reason,
                BrokerName = _brokerClient.Name,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            _ledger.RecordResult(intent.Entry.IntentId, result);
            return result;
        }

        // Place via broker
        var executionResult = await _brokerClient.PlaceBracketAsync(intent, ct);

        // Ensure timestamp is set
        if (executionResult.TimestampUtc == default)
        {
            executionResult.TimestampUtc = DateTimeOffset.UtcNow;
        }

        // Record the result
        _ledger.RecordResult(intent.Entry.IntentId, executionResult);

        return executionResult;
    }

    /// <summary>
    /// Cancel an order by broker order ID.
    /// </summary>
    public async Task<ExecutionResult> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            throw new ArgumentException("Broker order ID cannot be empty", nameof(brokerOrderId));

        // Cancel via broker
        var result = await _brokerClient.CancelAsync(brokerOrderId, ct);

        // Ensure timestamp is set
        if (result.TimestampUtc == default)
        {
            result.TimestampUtc = DateTimeOffset.UtcNow;
        }

        // Record the result (use empty GUID as placeholder since this is not tied to an original intent)
        _ledger.RecordResult(Guid.Empty, result);

        return result;
    }
}
