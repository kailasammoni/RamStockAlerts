namespace RamStockAlerts.Execution.Tests.Fakes;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// Fake broker client for testing. Returns Accepted by default with mock order IDs.
/// </summary>
public class FakeBrokerClient : IBrokerClient
{
    private readonly HashSet<string> _failureSymbols = new();
    private readonly List<OrderIntent> _placedOrders = new();
    private readonly List<BracketIntent> _placedBrackets = new();
    private string? _nextFailureReason;

    public string Name => "FakeBroker";

    public FakeBrokerClient()
    {
    }

    /// <summary>
    /// Force the next order to fail with a specific reason.
    /// </summary>
    public void SetNextFailure(string reason)
    {
        _nextFailureReason = reason;
    }

    /// <summary>
    /// Add a symbol that will always fail.
    /// </summary>
    public void AddFailureSymbol(string symbol)
    {
        _failureSymbols.Add(symbol);
    }

    /// <summary>
    /// Get all orders that were placed.
    /// </summary>
    public IReadOnlyList<OrderIntent> GetPlacedOrders() => _placedOrders.AsReadOnly();

    /// <summary>
    /// Get all brackets that were placed.
    /// </summary>
    public IReadOnlyList<BracketIntent> GetPlacedBrackets() => _placedBrackets.AsReadOnly();

    /// <summary>
    /// Place a single order.
    /// </summary>
    public Task<ExecutionResult> PlaceAsync(OrderIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        // Check for forced failure
        if (_nextFailureReason is not null)
        {
            var reason = _nextFailureReason;
            _nextFailureReason = null;
            return Task.FromResult(new ExecutionResult
            {
                Status = ExecutionStatus.Rejected,
                RejectionReason = reason,
                BrokerName = Name,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }

        // Check for symbol-based failure
        if (!string.IsNullOrEmpty(intent.Symbol) && _failureSymbols.Contains(intent.Symbol))
        {
            return Task.FromResult(new ExecutionResult
            {
                Status = ExecutionStatus.Error,
                RejectionReason = $"Symbol {intent.Symbol} not supported",
                BrokerName = Name,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }

        // Record the order
        _placedOrders.Add(intent);

        // Return success with fake broker order ID
        return Task.FromResult(new ExecutionResult
        {
            Status = ExecutionStatus.Accepted,
            BrokerName = Name,
            BrokerOrderIds = new() { $"FAKE-{Guid.NewGuid():N}" },
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Place a bracket order.
    /// </summary>
    public Task<ExecutionResult> PlaceBracketAsync(BracketIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        // Check for forced failure
        if (_nextFailureReason is not null)
        {
            var reason = _nextFailureReason;
            _nextFailureReason = null;
            return Task.FromResult(new ExecutionResult
            {
                Status = ExecutionStatus.Rejected,
                RejectionReason = reason,
                BrokerName = Name,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }

        // Check for symbol-based failure
        var symbol = intent.Entry.Symbol;
        if (!string.IsNullOrEmpty(symbol) && _failureSymbols.Contains(symbol))
        {
            return Task.FromResult(new ExecutionResult
            {
                Status = ExecutionStatus.Error,
                RejectionReason = $"Symbol {symbol} not supported",
                BrokerName = Name,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }

        // Record the bracket
        _placedBrackets.Add(intent);

        // Return success with 3 fake order IDs (entry, stop, profit)
        return Task.FromResult(new ExecutionResult
        {
            Status = ExecutionStatus.Accepted,
            BrokerName = Name,
            BrokerOrderIds = new()
            {
                $"FAKE-ENTRY-{Guid.NewGuid():N}",
                $"FAKE-STOP-{Guid.NewGuid():N}",
                $"FAKE-PROFIT-{Guid.NewGuid():N}"
            },
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Cancel an order.
    /// </summary>
    public Task<ExecutionResult> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            throw new ArgumentException("Broker order ID cannot be empty", nameof(brokerOrderId));

        return Task.FromResult(new ExecutionResult
        {
            Status = ExecutionStatus.Cancelled,
            BrokerName = Name,
            BrokerOrderIds = new() { brokerOrderId },
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }
}
