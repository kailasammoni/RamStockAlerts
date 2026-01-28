using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services.Reporting;

public sealed class MarketHoursLogIngestor : BackgroundService
{
    private readonly ILogger<MarketHoursLogIngestor> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly FileTailer _journalTailer;
    private readonly FileTailer _ledgerTailer;
    private readonly RollingLogTailer _logTailer;
    private readonly MarketHoursState _state = new();
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _summaryLogInterval;
    private readonly double _heartbeatStaleThresholdSeconds;
    private readonly string _outputPath;

    private DateTimeOffset _lastSummaryDebugLogUtc = DateTimeOffset.MinValue;
    private bool _lastHeartbeatWasStale;
    private int? _lastUniverseCount;
    private int? _lastActiveSubscriptionsCount;
    private ExecutionStatus? _lastExecutionStatus;

    public MarketHoursLogIngestor(IConfiguration configuration, ILogger<MarketHoursLogIngestor> logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        _pollInterval = TimeSpan.FromSeconds(configuration.GetValue("Monitoring:PollIntervalSeconds", 15));
        _summaryLogInterval = TimeSpan.FromSeconds(configuration.GetValue("Monitoring:SummaryLogIntervalSeconds", 60));
        _heartbeatStaleThresholdSeconds = configuration.GetValue("Monitoring:HeartbeatStaleThresholdSeconds", 120.0);
        _outputPath = configuration.GetValue<string>("Monitoring:OutputPath")
                      ?? Path.Combine("logs", "market-hours-status.jsonl");

        var tradeJournalPath = configuration.GetValue<string>("Monitoring:TradeJournalPath")
                               ?? Path.Combine("logs", "trade-journal.jsonl");
        var executionLedgerPath = configuration.GetValue<string>("Monitoring:ExecutionLedgerPath")
                                  ?? Path.Combine("logs", "execution-ledger.jsonl");
        var logGlob = configuration.GetValue<string>("Monitoring:LogGlob")
                     ?? Path.Combine("logs", "ramstockalerts-*.txt");

        _journalTailer = new FileTailer(tradeJournalPath);
        _ledgerTailer = new FileTailer(executionLedgerPath);
        _logTailer = new RollingLogTailer(logGlob);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureOutputDirectory();

        _logger.LogInformation(
            "[Monitoring] Market-hours log ingestor running. Poll={PollSeconds}s Output={OutputPath}",
            _pollInterval.TotalSeconds,
            _outputPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IngestTradeJournal();
                IngestExecutionLedger();
                IngestSerilogTextLogs();
                EmitSummary();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[Monitoring] Failed to update market-hours status.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private void IngestTradeJournal()
    {
        foreach (var line in _journalTailer.ReadNewLines())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TradeJournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<TradeJournalEntry>(line, _jsonOptions);
            }
            catch
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            var timestamp = entry.DecisionTimestampUtc
                            ?? entry.MarketTimestampUtc
                            ?? entry.JournalWriteTimestampUtc;

            if (string.Equals(entry.EntryType, "Heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                _state.LastHeartbeatUtc = timestamp;
                if (entry.SystemMetrics is not null)
                {
                    _state.LastSystemMetrics = entry.SystemMetrics;
                }
            }

            if (string.Equals(entry.EntryType, "Signal", StringComparison.OrdinalIgnoreCase))
            {
                _state.SignalCount += 1;
                _state.LastSignalUtc = timestamp;
            }

            if (string.Equals(entry.EntryType, "Rejection", StringComparison.OrdinalIgnoreCase))
            {
                _state.RejectionCount += 1;
            }
        }
    }

    private void IngestExecutionLedger()
    {
        foreach (var line in _ledgerTailer.ReadNewLines())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ExecutionLedgerEvent? ledgerEvent;
            try
            {
                ledgerEvent = JsonSerializer.Deserialize<ExecutionLedgerEvent>(line, _jsonOptions);
            }
            catch
            {
                continue;
            }

            if (ledgerEvent is null || string.IsNullOrWhiteSpace(ledgerEvent.Type))
            {
                continue;
            }

            if (!string.Equals(ledgerEvent.Type, "result", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ExecutionResult? result;
            try
            {
                result = ledgerEvent.Payload.Deserialize<ExecutionResult>(_jsonOptions);
            }
            catch
            {
                continue;
            }

            if (result is null)
            {
                continue;
            }

            _state.LastExecutionStatus = result.Status;
            _state.LastExecutionTimestampUtc = result.TimestampUtc;
            _state.LastExecutionRejectionReason = result.RejectionReason;
            _state.LastExecutionBroker = result.BrokerName;
        }
    }

    private void IngestSerilogTextLogs()
    {
        foreach (var line in _logTailer.ReadNewLines())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _state.LastLogLine = line.Trim();
            _state.LastLogTimestampUtc = TryParseLogTimestamp(line);
        }
    }

    private void EmitSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = _state.LastSystemMetrics;
        var heartbeatAge = _state.LastHeartbeatUtc.HasValue
            ? (now - _state.LastHeartbeatUtc.Value).TotalSeconds
            : (double?)null;

        var summary = new MarketHoursStatus
        {
            TimestampUtc = now,
            LastHeartbeatUtc = _state.LastHeartbeatUtc,
            HeartbeatAgeSeconds = heartbeatAge,
            UniverseCount = metrics?.UniverseCount,
            ActiveSubscriptionsCount = metrics?.ActiveSubscriptionsCount,
            DepthEnabledCount = metrics?.DepthEnabledCount,
            TickByTickEnabledCount = metrics?.TickByTickEnabledCount,
            DepthSubscribeErrors = metrics?.DepthSubscribeErrors,
            DepthSubscribeLastErrorCode = metrics?.DepthSubscribeLastErrorCode,
            DepthSubscribeLastErrorMessage = metrics?.DepthSubscribeLastErrorMessage,
            IsBookValidAny = metrics?.IsBookValidAny,
            TapeRecentAny = metrics?.TapeRecentAny,
            LastDepthUpdateAgeMs = metrics?.LastDepthUpdateAgeMs,
            LastTapeUpdateAgeMs = metrics?.LastTapeUpdateAgeMs,
            SignalCount = _state.SignalCount,
            RejectionCount = _state.RejectionCount,
            LastSignalUtc = _state.LastSignalUtc,
            LastExecutionStatus = _state.LastExecutionStatus,
            LastExecutionTimestampUtc = _state.LastExecutionTimestampUtc,
            LastExecutionRejectionReason = _state.LastExecutionRejectionReason,
            LastExecutionBroker = _state.LastExecutionBroker,
            LastLogLine = _state.LastLogLine,
            LastLogTimestampUtc = _state.LastLogTimestampUtc
        };

        var payload = JsonSerializer.Serialize(summary, _jsonOptions);
        File.AppendAllText(_outputPath, payload + Environment.NewLine);

        EmitTransitionLogs(summary);

        if (_summaryLogInterval <= TimeSpan.Zero || now - _lastSummaryDebugLogUtc >= _summaryLogInterval)
        {
            _lastSummaryDebugLogUtc = now;
            _logger.LogDebug(
                "[Monitoring] HeartbeatAge={HeartbeatAge}s Universe={UniverseCount} Subs={Subscriptions} Signals={Signals} LastExec={LastExecutionStatus}",
                summary.HeartbeatAgeSeconds?.ToString("F1") ?? "n/a",
                summary.UniverseCount?.ToString() ?? "n/a",
                summary.ActiveSubscriptionsCount?.ToString() ?? "n/a",
                summary.SignalCount,
                summary.LastExecutionStatus?.ToString() ?? "n/a");
        }
    }

    private void EmitTransitionLogs(MarketHoursStatus summary)
    {
        var heartbeatAgeSeconds = summary.HeartbeatAgeSeconds;
        var heartbeatIsStale = heartbeatAgeSeconds is not null &&
                               heartbeatAgeSeconds.Value > _heartbeatStaleThresholdSeconds;

        if (heartbeatIsStale && !_lastHeartbeatWasStale && heartbeatAgeSeconds is { } heartbeatAge)
        {
            _logger.LogWarning(
                "[Monitoring] Heartbeat stale: ageSec={HeartbeatAgeSeconds:F1} thresholdSec={ThresholdSeconds:F0}",
                heartbeatAge,
                _heartbeatStaleThresholdSeconds);
        }

        if (!heartbeatIsStale && _lastHeartbeatWasStale)
        {
            _logger.LogInformation(
                "[Monitoring] Heartbeat recovered: ageSec={HeartbeatAgeSeconds:F1} thresholdSec={ThresholdSeconds:F0}",
                heartbeatAgeSeconds ?? -1,
                _heartbeatStaleThresholdSeconds);
        }

        if (_lastUniverseCount.HasValue && summary.UniverseCount.HasValue)
        {
            if (_lastUniverseCount.Value == 0 && summary.UniverseCount.Value > 0)
            {
                _logger.LogInformation("[Monitoring] Universe recovered: 0 → {UniverseCount}", summary.UniverseCount.Value);
            }

            if (_lastUniverseCount.Value > 0 && summary.UniverseCount.Value == 0)
            {
                _logger.LogWarning("[Monitoring] Universe dropped: {Prev} → 0", _lastUniverseCount.Value);
            }
        }

        if (_lastActiveSubscriptionsCount.HasValue && summary.ActiveSubscriptionsCount.HasValue)
        {
            if (_lastActiveSubscriptionsCount.Value > 0 && summary.ActiveSubscriptionsCount.Value == 0)
            {
                _logger.LogWarning("[Monitoring] Subscriptions dropped: {Prev} → 0", _lastActiveSubscriptionsCount.Value);
            }

            if (_lastActiveSubscriptionsCount.Value == 0 && summary.ActiveSubscriptionsCount.Value > 0)
            {
                _logger.LogInformation("[Monitoring] Subscriptions recovered: 0 → {Subscriptions}", summary.ActiveSubscriptionsCount.Value);
            }
        }

        if (_lastExecutionStatus.HasValue && summary.LastExecutionStatus.HasValue && _lastExecutionStatus != summary.LastExecutionStatus)
        {
            _logger.LogInformation(
                "[Monitoring] Execution status changed: {OldStatus} → {NewStatus}",
                _lastExecutionStatus.Value,
                summary.LastExecutionStatus.Value);
        }

        _lastHeartbeatWasStale = heartbeatIsStale;
        _lastUniverseCount = summary.UniverseCount;
        _lastActiveSubscriptionsCount = summary.ActiveSubscriptionsCount;
        _lastExecutionStatus = summary.LastExecutionStatus;
    }

    private void EnsureOutputDirectory()
    {
        var dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static DateTimeOffset? TryParseLogTimestamp(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 25)
        {
            return null;
        }

        var candidate = line.Length >= 29 ? line[..29] : line;
        if (DateTimeOffset.TryParse(candidate, out var timestamp))
        {
            return timestamp;
        }

        return null;
    }

    private sealed class MarketHoursState
    {
        public DateTimeOffset? LastHeartbeatUtc { get; set; }
        public TradeJournalEntry.SystemMetricsSnapshot? LastSystemMetrics { get; set; }
        public int SignalCount { get; set; }
        public int RejectionCount { get; set; }
        public DateTimeOffset? LastSignalUtc { get; set; }
        public ExecutionStatus? LastExecutionStatus { get; set; }
        public DateTimeOffset? LastExecutionTimestampUtc { get; set; }
        public string? LastExecutionRejectionReason { get; set; }
        public string? LastExecutionBroker { get; set; }
        public string? LastLogLine { get; set; }
        public DateTimeOffset? LastLogTimestampUtc { get; set; }
    }

    private sealed class MarketHoursStatus
    {
        public DateTimeOffset TimestampUtc { get; init; }
        public DateTimeOffset? LastHeartbeatUtc { get; init; }
        public double? HeartbeatAgeSeconds { get; init; }
        public int? UniverseCount { get; init; }
        public int? ActiveSubscriptionsCount { get; init; }
        public int? DepthEnabledCount { get; init; }
        public int? TickByTickEnabledCount { get; init; }
        public long? DepthSubscribeErrors { get; init; }
        public int? DepthSubscribeLastErrorCode { get; init; }
        public string? DepthSubscribeLastErrorMessage { get; init; }
        public bool? IsBookValidAny { get; init; }
        public bool? TapeRecentAny { get; init; }
        public long? LastDepthUpdateAgeMs { get; init; }
        public long? LastTapeUpdateAgeMs { get; init; }
        public int SignalCount { get; init; }
        public int RejectionCount { get; init; }
        public DateTimeOffset? LastSignalUtc { get; init; }
        public ExecutionStatus? LastExecutionStatus { get; init; }
        public DateTimeOffset? LastExecutionTimestampUtc { get; init; }
        public string? LastExecutionRejectionReason { get; init; }
        public string? LastExecutionBroker { get; init; }
        public string? LastLogLine { get; init; }
        public DateTimeOffset? LastLogTimestampUtc { get; init; }
    }

    private sealed class FileTailer
    {
        private readonly string _path;
        private long _position;

        public FileTailer(string path)
        {
            _path = path;
        }

        public IReadOnlyList<string> ReadNewLines()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return Array.Empty<string>();
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (_position > stream.Length)
            {
                _position = 0;
            }

            stream.Seek(_position, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            _position = stream.Position;
            return lines;
        }
    }

    private sealed class RollingLogTailer
    {
        private readonly string _logGlob;
        private string? _currentPath;
        private long _position;

        public RollingLogTailer(string logGlob)
        {
            _logGlob = logGlob;
        }

        public IReadOnlyList<string> ReadNewLines()
        {
            var path = ResolveLatestPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return Array.Empty<string>();
            }

            if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentPath = path;
                _position = 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (_position > stream.Length)
            {
                _position = 0;
            }

            stream.Seek(_position, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            _position = stream.Position;
            return lines;
        }

        private string? ResolveLatestPath()
        {
            var directory = Path.GetDirectoryName(_logGlob);
            var pattern = Path.GetFileName(_logGlob);
            if (string.IsNullOrWhiteSpace(directory)
                || string.IsNullOrWhiteSpace(pattern)
                || !Directory.Exists(directory))
            {
                return null;
            }

            var files = Directory.GetFiles(directory, pattern);
            if (files.Length == 0)
            {
                return null;
            }

            return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
    }
}
