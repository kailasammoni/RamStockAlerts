namespace RamStockAlerts.Models;

/// <summary>
/// Persisted event for event sourcing and backtest replay.
/// </summary>
public class EventStoreEntry
{
    public long Id { get; set; }
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? CorrelationId { get; set; }
}
