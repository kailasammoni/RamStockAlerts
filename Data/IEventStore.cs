namespace RamStockAlerts.Data;

public interface IEventStore
{
    Task AppendAsync(string eventType, object payload, CancellationToken cancellationToken = default);
    IAsyncEnumerable<(string EventType, string Data, DateTime RecordedAt)> ReplayAsync(DateTime? from = null, CancellationToken cancellationToken = default);
}
