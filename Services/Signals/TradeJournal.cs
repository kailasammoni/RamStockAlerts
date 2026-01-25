using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services.Signals;

// NOTE: Offline replay mode intentionally omitted.
// Schema validated via live signaling only.

public sealed class TradeJournal : BackgroundService, ITradeJournal
{
    internal const int CurrentSchemaVersion = 2;
    private readonly ILogger<TradeJournal> _logger;
    private readonly Channel<TradeJournalEntry> _channel;
    private readonly string _filePath;
    private readonly Guid _sessionId = Guid.NewGuid();
    private StreamWriter? _writer;
    private DateTimeOffset _lastWriteFailureLog = DateTimeOffset.MinValue;

    public TradeJournal(IConfiguration configuration, ILogger<TradeJournal> logger)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<TradeJournalEntry>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _filePath = configuration.GetValue<string>("SignalsJournal:FilePath")
                    ?? Path.Combine("logs", "trade-journal.jsonl");
    }

    public Guid SessionId => _sessionId;

    public bool TryEnqueue(TradeJournalEntry entry)
    {
        if (entry.SessionId == Guid.Empty)
        {
            entry.SessionId = _sessionId;
        }
        return _channel.Writer.TryWrite(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
        _writer = new StreamWriter(
            new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        _logger.LogInformation("[Journal] Trade journal active: {Path}", _filePath);

        await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                entry.SchemaVersion = entry.SchemaVersion == 0 ? CurrentSchemaVersion : entry.SchemaVersion;
                entry.JournalWriteTimestampUtc = DateTimeOffset.UtcNow;
                EnsureMonotonicTimestamps(entry);
                var line = JsonSerializer.Serialize(entry);
                await _writer.WriteLineAsync(line);
                if (entry.EntryType == "UniverseUpdate")
                {
                    _logger.LogInformation(
                        "JournalWrite: type={Type} schema={Schema} candidates={CandidatesCount} active={ActiveCount} exclusions={ExclusionsCount}",
                        entry.EntryType,
                        entry.SchemaVersion,
                        entry.UniverseUpdate?.Counts?.CandidatesCount ?? 0,
                        entry.UniverseUpdate?.Counts?.ActiveCount ?? 0,
                        entry.UniverseUpdate?.Exclusions?.Count ?? 0);
                }
                else
                {
                    _logger.LogInformation(
                        "JournalWrite: type={Type} outcome={Outcome} schema={Schema} ticker={Ticker} score={Score} reason={Reason}",
                        entry.EntryType ?? "Unknown",
                        entry.DecisionOutcome ?? "Unknown",
                        entry.SchemaVersion,
                        entry.Symbol ?? string.Empty,
                        entry.DecisionInputs?.Score,
                        entry.RejectionReason ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastWriteFailureLog >= TimeSpan.FromMinutes(1))
                {
                    _lastWriteFailureLog = now;
                    _logger.LogError(ex, "[Journal] Write failed: {Message}", ex.Message);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);

        if (_writer != null)
        {
            await _writer.FlushAsync();
            _writer.Dispose();
        }
    }

    private static void EnsureMonotonicTimestamps(TradeJournalEntry entry)
    {
        if (entry.MarketTimestampUtc.HasValue && entry.DecisionTimestampUtc.HasValue &&
            entry.DecisionTimestampUtc < entry.MarketTimestampUtc)
        {
            entry.DecisionTimestampUtc = entry.MarketTimestampUtc;
        }

        var floor = entry.DecisionTimestampUtc ?? entry.MarketTimestampUtc;
        if (floor.HasValue && entry.JournalWriteTimestampUtc.HasValue &&
            entry.JournalWriteTimestampUtc < floor)
        {
            entry.JournalWriteTimestampUtc = floor;
        }
    }
}
