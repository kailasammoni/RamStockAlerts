namespace RamStockAlerts.Execution.Storage;

using System.Text.Json;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// File-backed JSONL execution ledger (append-only) with an in-memory index.
/// Each line is a small envelope describing a ledger event.
/// </summary>
public sealed class JsonlExecutionLedger : IExecutionLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _filePath;
    private readonly InMemoryExecutionLedger _inner = new();
    private readonly object _ioLock = new();

    public JsonlExecutionLedger(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required", nameof(filePath));

        _filePath = filePath;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        LoadExisting();
    }

    public void RecordIntent(OrderIntent intent)
    {
        _inner.RecordIntent(intent);
        AppendEvent("intent", DateTimeOffset.UtcNow, intent);
    }

    public void RecordBracket(BracketIntent intent)
    {
        _inner.RecordBracket(intent);
        AppendEvent("bracket", DateTimeOffset.UtcNow, intent);
    }

    public void RecordResult(Guid intentId, ExecutionResult result)
    {
        _inner.RecordResult(intentId, result);
        AppendEvent("result", DateTimeOffset.UtcNow, result);
    }

    public IReadOnlyList<OrderIntent> GetIntents() => _inner.GetIntents();
    public IReadOnlyList<BracketIntent> GetBrackets() => _inner.GetBrackets();
    public IReadOnlyList<ExecutionResult> GetResults() => _inner.GetResults();
    public int GetSubmittedIntentCountToday(DateTimeOffset now) => _inner.GetSubmittedIntentCountToday(now);
    public int GetSubmittedBracketCountToday(DateTimeOffset now) => _inner.GetSubmittedBracketCountToday(now);
    public int GetOpenBracketCount() => _inner.GetOpenBracketCount();
    public void UpdateBracketState(Guid entryIntentId, BracketState newState) => _inner.UpdateBracketState(entryIntentId, newState);

    private void LoadExisting()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        foreach (var line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ExecutionLedgerEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ExecutionLedgerEvent>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (evt is null || string.IsNullOrWhiteSpace(evt.Type))
            {
                continue;
            }

            try
            {
                switch (evt.Type)
                {
                    case "intent":
                    {
                        var intent = evt.Payload.Deserialize<OrderIntent>(JsonOptions);
                        if (intent is not null)
                        {
                            _inner.RecordIntent(intent);
                        }
                        break;
                    }
                    case "bracket":
                    {
                        var bracket = evt.Payload.Deserialize<BracketIntent>(JsonOptions);
                        if (bracket is not null)
                        {
                            _inner.RecordBracket(bracket);
                        }
                        break;
                    }
                    case "result":
                    {
                        var result = evt.Payload.Deserialize<ExecutionResult>(JsonOptions);
                        if (result is not null)
                        {
                            _inner.RecordResult(result.IntentId, result);
                        }
                        break;
                    }
                }
            }
            catch
            {
                // Best-effort load; ignore malformed lines.
            }
        }
    }

    private void AppendEvent(string type, DateTimeOffset timestampUtc, object payload)
    {
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var evt = new ExecutionLedgerEvent(type, timestampUtc, payloadElement);
        var json = JsonSerializer.Serialize(evt, JsonOptions);

        lock (_ioLock)
        {
            File.AppendAllText(_filePath, json + Environment.NewLine);
        }
    }

}
