using System.Collections.Concurrent;

namespace RamStockAlerts.Data;

public class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentQueue<(string EventType, string Data, DateTime RecordedAt)> _events = new();

    public Task AppendAsync(string eventType, object payload, CancellationToken cancellationToken = default)
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(payload);
        _events.Enqueue((eventType, serialized, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string EventType, string Data, DateTime RecordedAt)> ReplayAsync(DateTime? from = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var evt in _events)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            if (from.HasValue && evt.RecordedAt < from.Value) continue;
            yield return evt;
            await Task.Yield();
        }
    }
}
