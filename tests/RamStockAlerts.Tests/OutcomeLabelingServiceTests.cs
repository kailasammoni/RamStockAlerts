using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Execution.Storage;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public sealed class OutcomeLabelingServiceTests
{
    [Fact]
    public async Task OutcomeLabelingService_LabelsOutcome_OnBracketFill()
    {
        var decisionId = Guid.NewGuid();
        var orderId = 555;
        var journalPath = Path.Combine(Path.GetTempPath(), $"test-journal-{Guid.NewGuid()}.jsonl");

        try
        {
            var entry = new TradeJournalEntry
            {
                SchemaVersion = 2,
                DecisionId = decisionId,
                Symbol = "AAPL",
                Direction = "Long",
                DecisionOutcome = "Accepted",
                DecisionTimestampUtc = DateTimeOffset.UtcNow,
                Blueprint = new TradeJournalEntry.BlueprintPlan
                {
                    Entry = 100m,
                    Stop = 95m,
                    Target = 110m
                }
            };

            var json = JsonSerializer.Serialize(entry);
            await File.WriteAllTextAsync(journalPath, json + Environment.NewLine);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SignalsJournal:FilePath"] = journalPath
                })
                .Build();

            var tracker = new TestOrderStateTracker();
            var ledger = new InMemoryExecutionLedger();
            var intent = new OrderIntent
            {
                IntentId = Guid.NewGuid(),
                DecisionId = decisionId,
                Symbol = "AAPL",
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 10m
            };

            ledger.RecordIntent(intent);
            ledger.RecordResult(intent.IntentId, new ExecutionResult
            {
                IntentId = intent.IntentId,
                Status = ExecutionStatus.Submitted,
                BrokerOrderIds = new List<string> { orderId.ToString() },
                TimestampUtc = DateTimeOffset.UtcNow
            });

            var labeler = new TradeOutcomeLabeler();
            var store = new InMemoryOutcomeSummaryStore();
            var snapshot = new DailyPerformanceSnapshot();
            var service = new OutcomeLabelingService(
                tracker,
                ledger,
                labeler,
                store,
                config,
                NullLogger<OutcomeLabelingService>.Instance,
                snapshot);

            await service.StartAsync(CancellationToken.None);

            tracker.FireFilled(orderId, parentOrderId: 999, fillPrice: 110m, fillTime: DateTimeOffset.UtcNow);

            var outcome = await store.WaitForOutcomeAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(outcome);
            Assert.Equal(decisionId, outcome!.DecisionId);
            Assert.Equal("HitTarget", outcome.OutcomeType);
        }
        finally
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
    }

    [Fact]
    public async Task OutcomeLabelingService_IgnoresNonBracketFill()
    {
        var tracker = new TestOrderStateTracker();
        var ledger = new InMemoryExecutionLedger();
        var labeler = new TradeOutcomeLabeler();
        var store = new InMemoryOutcomeSummaryStore();
        var config = new ConfigurationBuilder().Build();

        var service = new OutcomeLabelingService(
            tracker,
            ledger,
            labeler,
            store,
            config,
            NullLogger<OutcomeLabelingService>.Instance);

        await service.StartAsync(CancellationToken.None);

        tracker.FireFilled(1, parentOrderId: null, fillPrice: 100m, fillTime: DateTimeOffset.UtcNow);

        var outcome = await store.WaitForOutcomeAsync(TimeSpan.FromMilliseconds(250));
        Assert.Null(outcome);
    }

    private sealed class TestOrderStateTracker : IOrderStateTracker
    {
        public event Action<int, int?, decimal, DateTimeOffset>? OnOrderFilled;

        public void FireFilled(int orderId, int? parentOrderId, decimal fillPrice, DateTimeOffset fillTime)
        {
            OnOrderFilled?.Invoke(orderId, parentOrderId, fillPrice, fillTime);
        }

        public void TrackSubmittedOrder(int orderId, Guid intentId, string symbol, decimal quantity, OrderSide side) { }
        public void ProcessOrderStatus(OrderStatusUpdate update) { }
        public void ProcessFill(FillReport fill) { }
        public void ProcessCommissionReport(string execId, decimal? commission, decimal? realizedPnl) { }
        public BrokerOrderStatus GetOrderStatus(int orderId) => BrokerOrderStatus.Unknown;
        public IReadOnlyList<FillReport> GetFillsForOrder(int orderId) => Array.Empty<FillReport>();
        public IReadOnlyList<FillReport> GetFillsForIntent(Guid intentId) => Array.Empty<FillReport>();
        public decimal GetRealizedPnlToday() => 0m;
        public int GetOpenBracketCount() => 0;
    }

    private sealed class InMemoryOutcomeSummaryStore : IOutcomeSummaryStore
    {
        private readonly ConcurrentBag<TradeOutcome> _outcomes = new();
        private readonly TaskCompletionSource<TradeOutcome?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AppendOutcomeAsync(TradeOutcome outcome, CancellationToken cancellationToken = default)
        {
            _outcomes.Add(outcome);
            _tcs.TrySetResult(outcome);
            return Task.CompletedTask;
        }

        public Task AppendOutcomesAsync(List<TradeOutcome> outcomes, CancellationToken cancellationToken = default)
        {
            foreach (var outcome in outcomes)
            {
                _outcomes.Add(outcome);
                _tcs.TrySetResult(outcome);
            }
            return Task.CompletedTask;
        }

        public Task<List<TradeOutcome>> GetOutcomesByDateAsync(DateOnly dateUtc, CancellationToken cancellationToken = default)
        {
            var result = _outcomes.Where(o => DateOnly.FromDateTime(o.OutcomeLabeledUtc.UtcDateTime) == dateUtc).ToList();
            return Task.FromResult(result);
        }

        public Task<List<TradeOutcome>> GetAllOutcomesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_outcomes.ToList());
        }

        public Task<List<TradeOutcome>> GetOutcomesByDateRangeAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            var result = _outcomes
                .Where(o => DateOnly.FromDateTime(o.OutcomeLabeledUtc.UtcDateTime) >= startDate
                            && DateOnly.FromDateTime(o.OutcomeLabeledUtc.UtcDateTime) <= endDate)
                .ToList();
            return Task.FromResult(result);
        }

        public async Task<TradeOutcome?> WaitForOutcomeAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_tcs.Task, Task.Delay(timeout));
            return completed == _tcs.Task ? await _tcs.Task : null;
        }
    }
}
