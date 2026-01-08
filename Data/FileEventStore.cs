using System.Collections.Concurrent;
using System.Text.Json;

namespace RamStockAlerts.Data;

/// <summary>
/// File-based event store that persists events to disk for backtest replay.
/// Events are stored in a JSON file and loaded on startup.
/// </summary>
public class FileEventStore : IEventStore, IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<FileEventStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<StoredEvent> _events = new();

    public FileEventStore(IConfiguration configuration, ILogger<FileEventStore> logger)
    {
        _logger = logger;
        _filePath = configuration.GetValue<string>("EventStore:FilePath") ?? "events.jsonl";
        
        // Load existing events from file
        LoadEventsFromFile();
    }

    private void LoadEventsFromFile()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("Event store file does not exist, starting fresh: {FilePath}", _filePath);
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_filePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var evt = JsonSerializer.Deserialize<StoredEvent>(line);
                if (evt != null)
                {
                    _events.Add(evt);
                }
            }

            _logger.LogInformation("Loaded {Count} events from {FilePath}", _events.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load events from {FilePath}", _filePath);
        }
    }

    public async Task AppendAsync(string eventType, object payload, CancellationToken cancellationToken = default)
    {
        var serialized = JsonSerializer.Serialize(payload);
        var evt = new StoredEvent
        {
            EventType = eventType,
            Data = serialized,
            RecordedAt = DateTime.UtcNow
        };

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Add to in-memory list
            _events.Add(evt);

            // Append to file (JSONL format - one JSON object per line)
            var json = JsonSerializer.Serialize(evt);
            await File.AppendAllLinesAsync(_filePath, new[] { json }, cancellationToken);

            _logger.LogDebug("Appended event {EventType} to {FilePath}", eventType, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append event {EventType} to {FilePath}", eventType, _filePath);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<(string EventType, string Data, DateTime RecordedAt)> ReplayAsync(
        DateTime? from = null, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Return events from in-memory list (already loaded from file)
        foreach (var evt in _events)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            if (from.HasValue && evt.RecordedAt < from.Value)
                continue;

            yield return (evt.EventType, evt.Data, evt.RecordedAt);
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        _writeLock?.Dispose();
    }

    private class StoredEvent
    {
        public string EventType { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public DateTime RecordedAt { get; set; }
    }
}
