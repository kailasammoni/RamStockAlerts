using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

// NOTE: Offline ReplayShadow mode intentionally omitted.
// Schema validated via live shadow trading only.

public sealed class ShadowTradeJournal : BackgroundService
{
    private readonly ILogger<ShadowTradeJournal> _logger;
    private readonly Channel<ShadowTradeJournalEntry> _channel;
    private readonly string _filePath;
    private readonly bool _enabled;
    private readonly Guid _sessionId = Guid.NewGuid();
    private StreamWriter? _writer;
    private DateTimeOffset _lastWriteFailureLog = DateTimeOffset.MinValue;

    public ShadowTradeJournal(IConfiguration configuration, ILogger<ShadowTradeJournal> logger)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<ShadowTradeJournalEntry>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _filePath = configuration.GetValue<string>("ShadowTradeJournal:FilePath")
                    ?? Path.Combine("logs", "shadow-trade-journal.jsonl");

        var tradingMode = configuration.GetValue<string>("TradingMode") ?? string.Empty;
        _enabled = string.Equals(tradingMode, "Shadow", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryEnqueue(ShadowTradeJournalEntry entry)
    {
        if (!_enabled)
        {
            return false;
        }

        if (entry.SessionId == Guid.Empty)
        {
            entry.SessionId = _sessionId;
        }
        return _channel.Writer.TryWrite(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("[Shadow] ShadowTradeJournal disabled (TradingMode != Shadow).");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
        _writer = new StreamWriter(
            new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        _logger.LogInformation("[Shadow] ShadowTradeJournal active: {Path}", _filePath);

        await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                entry.SchemaVersion = entry.SchemaVersion == 0 ? 1 : entry.SchemaVersion;
                var line = JsonSerializer.Serialize(entry);
                await _writer.WriteLineAsync(line);
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastWriteFailureLog >= TimeSpan.FromMinutes(1))
                {
                    _lastWriteFailureLog = now;
                    _logger.LogError(ex, "[ShadowJournal] Write failed: {Message}", ex.Message);
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
}
