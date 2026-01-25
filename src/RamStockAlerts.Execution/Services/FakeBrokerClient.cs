namespace RamStockAlerts.Execution.Services;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// Simple fake broker client for development/testing.
/// Always returns Accepted with mock order IDs.
/// For production, replace with real broker client implementation.
/// </summary>
public class FakeBrokerClient : IBrokerClient
{
    public string Name => "FakeBroker";

    /// <summary>
    /// Place a single order - always succeeds with fake order ID.
    /// </summary>
    public Task<ExecutionResult> PlaceAsync(OrderIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

        return Task.FromResult(new ExecutionResult
        {
            Status = ExecutionStatus.Accepted,
            BrokerName = Name,
            BrokerOrderIds = new() { $"FAKE-{Guid.NewGuid():N}" },
            TimestampUtc = DateTimeOffset.UtcNow,
            Debug = $"Fake order placed: {intent.Symbol} {intent.Side} {intent.Quantity ?? intent.NotionalUsd}"
        });
    }

    /// <summary>
    /// Place a bracket order - always succeeds with 3 fake order IDs.
    /// </summary>
    public Task<ExecutionResult> PlaceBracketAsync(BracketIntent intent, CancellationToken ct = default)
    {
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));

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
            TimestampUtc = DateTimeOffset.UtcNow,
            Debug = $"Fake bracket placed: {intent.Entry.Symbol} (Entry + {(intent.StopLoss != null ? "Stop" : "")} {(intent.TakeProfit != null ? "+ TP" : "")})"
        });
    }

    /// <summary>
    /// Cancel an order - always succeeds.
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
            TimestampUtc = DateTimeOffset.UtcNow,
            Debug = $"Fake cancel: {brokerOrderId}"
        });
    }
}
