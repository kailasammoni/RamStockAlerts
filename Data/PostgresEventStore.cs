using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Models;

namespace RamStockAlerts.Data;

/// <summary>
/// PostgreSQL-backed event store for persistent event sourcing and backtest replay.
/// </summary>
public class PostgresEventStore : IEventStore
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PostgresEventStore> _logger;

    public PostgresEventStore(IServiceProvider serviceProvider, ILogger<PostgresEventStore> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task AppendAsync(string eventType, object payload, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var serialized = JsonSerializer.Serialize(payload);
            var entry = new EventStoreEntry
            {
                AggregateId = Guid.NewGuid().ToString(),
                EventType = eventType,
                Payload = serialized,
                Timestamp = DateTime.UtcNow,
                CorrelationId = Activity.Current?.Id
            };

            dbContext.EventStoreEntries.Add(entry);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append event {EventType}", eventType);
            throw;
        }
    }

    public async IAsyncEnumerable<(string EventType, string Data, DateTime RecordedAt)> ReplayAsync(
        DateTime? from = null, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = dbContext.EventStoreEntries.OrderBy(e => e.Timestamp).AsQueryable();

        if (from.HasValue)
        {
            query = query.Where(e => e.Timestamp >= from.Value);
        }

        await foreach (var entry in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return (entry.EventType, entry.Payload, entry.Timestamp);
        }
    }
}
