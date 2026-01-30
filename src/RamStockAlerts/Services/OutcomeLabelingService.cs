using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public sealed class OutcomeLabelingService : IHostedService
{
    private readonly IOrderStateTracker _orderStateTracker;
    private readonly IExecutionLedger _executionLedger;
    private readonly ITradeOutcomeLabeler _labeler;
    private readonly IOutcomeSummaryStore _outcomeStore;
    private readonly IPerformanceSnapshot? _snapshot;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutcomeLabelingService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<int, byte> _processedOrderIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _processedDecisionIds = new();
    private CancellationTokenSource? _cts;

    public OutcomeLabelingService(
        IOrderStateTracker orderStateTracker,
        IExecutionLedger executionLedger,
        ITradeOutcomeLabeler labeler,
        IOutcomeSummaryStore outcomeStore,
        IConfiguration configuration,
        ILogger<OutcomeLabelingService> logger,
        IPerformanceSnapshot? snapshot = null)
    {
        _orderStateTracker = orderStateTracker ?? throw new ArgumentNullException(nameof(orderStateTracker));
        _executionLedger = executionLedger ?? throw new ArgumentNullException(nameof(executionLedger));
        _labeler = labeler ?? throw new ArgumentNullException(nameof(labeler));
        _outcomeStore = outcomeStore ?? throw new ArgumentNullException(nameof(outcomeStore));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshot = snapshot;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _orderStateTracker.OnOrderFilled += HandleOrderFilled;
        _logger.LogInformation("[Outcome] OutcomeLabelingService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _orderStateTracker.OnOrderFilled -= HandleOrderFilled;
        _cts?.Cancel();
        _logger.LogInformation("[Outcome] OutcomeLabelingService stopped");
        return Task.CompletedTask;
    }

    private void HandleOrderFilled(int orderId, int? parentOrderId, decimal fillPrice, DateTimeOffset fillTime)
    {
        if (parentOrderId is null)
        {
            return;
        }

        if (!_processedOrderIds.TryAdd(orderId, 0))
        {
            return;
        }

        _ = Task.Run(() => ProcessFillAsync(orderId, fillPrice, fillTime, _cts?.Token ?? CancellationToken.None));
    }

    private async Task ProcessFillAsync(int orderId, decimal fillPrice, DateTimeOffset fillTime, CancellationToken cancellationToken)
    {
        try
        {
            var decisionId = _executionLedger.GetDecisionIdByOrderId(orderId);
            if (!decisionId.HasValue)
            {
                _logger.LogWarning("[Outcome] No DecisionId mapping for orderId={OrderId}", orderId);
                _processedOrderIds.TryRemove(orderId, out _);
                return;
            }

            if (!_processedDecisionIds.TryAdd(decisionId.Value, 0))
            {
                _logger.LogDebug("[Outcome] DecisionId {DecisionId} already labeled; skipping", decisionId);
                return;
            }

            var entry = await LoadJournalEntryAsync(decisionId.Value, cancellationToken);
            if (entry is null)
            {
                _logger.LogWarning("[Outcome] Journal entry not found for DecisionId={DecisionId}", decisionId);
                _processedOrderIds.TryRemove(orderId, out _);
                _processedDecisionIds.TryRemove(decisionId.Value, out _);
                return;
            }

            var outcome = await _labeler.LabelOutcomeAsync(entry, fillPrice, fillTime, cancellationToken);
            await _outcomeStore.AppendOutcomeAsync(outcome, cancellationToken);
            _snapshot?.RecordOutcome(outcome);

            _logger.LogInformation(
                "[OUTCOME] {Symbol} {DecisionId} -> {OutcomeType} R={RiskMultiple}",
                outcome.Symbol,
                outcome.DecisionId,
                outcome.OutcomeType,
                outcome.RiskMultiple);
        }
        catch (OperationCanceledException)
        {
            // swallow
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Outcome] Failed to label outcome for orderId={OrderId}", orderId);
        }
    }

    private async Task<TradeJournalEntry?> LoadJournalEntryAsync(Guid decisionId, CancellationToken cancellationToken)
    {
        var journalPath = ResolveJournalPath(_configuration);
        if (!File.Exists(journalPath))
        {
            _logger.LogWarning("[Outcome] Trade journal not found at {Path}", journalPath);
            return null;
        }

        await using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            if (entry?.DecisionId == decisionId)
            {
                return entry;
            }
        }

        return null;
    }

    private static string ResolveJournalPath(IConfiguration configuration)
    {
        var path = configuration.GetValue<string>("SignalsJournal:FilePath");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        path = configuration.GetValue<string>("TradeJournal:FilePath");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        path = configuration.GetValue<string>("ShadowTradeJournal:FilePath");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.Combine("logs", "trade-journal.jsonl");
    }
}
