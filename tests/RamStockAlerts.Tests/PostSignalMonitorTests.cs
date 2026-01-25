using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Models;
using RamStockAlerts.Services.Signals;
using RamStockAlerts.Tests.Helpers;
using RamStockAlerts.Tests.TestDoubles;
using Xunit;

namespace RamStockAlerts.Tests;

public class PostSignalMonitorTests
{
    [Fact]
    public async Task MonitorPostSignalQuality_SpreadBlowout_RequestsCancel()
    {
        var config = SignalCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["Signals:PostSignalMonitoringEnabled"] = "true",
            ["Signals:SpreadBlowoutThreshold"] = "0.5",
            ["Signals:TapeSlowdownThreshold"] = "0.5",
            ["Signals:PostSignalConsecutiveThreshold"] = "3",
            ["Signals:PostSignalMaxAgeMs"] = "300000"
        });

        var (manager, metrics) = await SignalCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(
            config,
            "AAPL",
            enableTickByTick: true);

        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
        var journal = new TestTradeJournal();
        var scarcity = new ScarcityController(config);
        var executionService = new FakeExecutionService();
        var coordinator = new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcity,
            manager,
            NullLogger<SignalCoordinator>.Instance,
            executionService);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        coordinator.TrackAcceptedSignal("AAPL", "BUY", Guid.NewGuid(), 0.02m, 10, nowMs, new List<string> { "123" });

        for (int i = 0; i < 3; i++)
        {
            var book = CreateBook("AAPL", spread: 0.04m, askTrades3s: 10);
            var currentMs = nowMs + (i * 1000);
            metrics.UpdateMetrics(book, currentMs);
            coordinator.ProcessSnapshot(book, currentMs);
        }

        var cancelled = SpinWait.SpinUntil(() => executionService.CancelCalled, 2000);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task MonitorPostSignalQuality_TapeSlowdown_RequestsCancel()
    {
        var config = SignalCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["Signals:PostSignalMonitoringEnabled"] = "true",
            ["Signals:SpreadBlowoutThreshold"] = "0.5",
            ["Signals:TapeSlowdownThreshold"] = "0.5",
            ["Signals:PostSignalConsecutiveThreshold"] = "3",
            ["Signals:PostSignalMaxAgeMs"] = "300000"
        });

        var (manager, metrics) = await SignalCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(
            config,
            "MSFT",
            enableTickByTick: true);

        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
        var journal = new TestTradeJournal();
        var scarcity = new ScarcityController(config);
        var executionService = new FakeExecutionService();
        var coordinator = new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcity,
            manager,
            NullLogger<SignalCoordinator>.Instance,
            executionService);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        coordinator.TrackAcceptedSignal("MSFT", "BUY", Guid.NewGuid(), 0.01m, 20, nowMs, new List<string> { "456" });

        for (int i = 0; i < 3; i++)
        {
            var book = CreateBook("MSFT", spread: 0.01m, askTrades3s: 5);
            var currentMs = nowMs + (i * 1000);
            metrics.UpdateMetrics(book, currentMs);
            coordinator.ProcessSnapshot(book, currentMs);
        }

        var cancelled = SpinWait.SpinUntil(() => executionService.CancelCalled, 2000);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task MonitorPostSignalQuality_ConditionsImprove_ResetsCounter()
    {
        var config = SignalCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["Signals:PostSignalMonitoringEnabled"] = "true",
            ["Signals:SpreadBlowoutThreshold"] = "0.5",
            ["Signals:TapeSlowdownThreshold"] = "0.5",
            ["Signals:PostSignalConsecutiveThreshold"] = "3",
            ["Signals:PostSignalMaxAgeMs"] = "300000"
        });

        var (manager, metrics) = await SignalCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(
            config,
            "GOOGL",
            enableTickByTick: true);

        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
        var journal = new TestTradeJournal();
        var scarcity = new ScarcityController(config);
        var executionService = new FakeExecutionService();
        var coordinator = new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcity,
            manager,
            NullLogger<SignalCoordinator>.Instance,
            executionService);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        coordinator.TrackAcceptedSignal("GOOGL", "SELL", Guid.NewGuid(), 0.02m, 15, nowMs, new List<string> { "789" });

        var book = CreateBook("GOOGL", spread: 0.04m, bidTrades3s: 15);
        var currentMs = nowMs + 1000;
        metrics.UpdateMetrics(book, currentMs);
        coordinator.ProcessSnapshot(book, currentMs);

        book = CreateBook("GOOGL", spread: 0.04m, bidTrades3s: 15);
        currentMs = nowMs + 2000;
        metrics.UpdateMetrics(book, currentMs);
        coordinator.ProcessSnapshot(book, currentMs);

        book = CreateBook("GOOGL", spread: 0.02m, bidTrades3s: 15);
        currentMs = nowMs + 3000;
        metrics.UpdateMetrics(book, currentMs);
        coordinator.ProcessSnapshot(book, currentMs);

        book = CreateBook("GOOGL", spread: 0.04m, bidTrades3s: 15);
        currentMs = nowMs + 4000;
        metrics.UpdateMetrics(book, currentMs);
        coordinator.ProcessSnapshot(book, currentMs);

        book = CreateBook("GOOGL", spread: 0.04m, bidTrades3s: 15);
        currentMs = nowMs + 5000;
        metrics.UpdateMetrics(book, currentMs);
        coordinator.ProcessSnapshot(book, currentMs);

        Assert.False(executionService.CancelCalled);
    }

    private static OrderBookState CreateBook(string symbol, decimal spread, int askTrades3s = 0, int bidTrades3s = 0)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = new OrderBookState(symbol);
        var bid = 100.00m;
        var ask = bid + spread;
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, bid, 200m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, ask, 200m, 0, nowMs));

        for (int i = 0; i < askTrades3s; i++)
        {
            book.RecordTrade(nowMs - 1000, nowMs - 1000, (double)(ask + 0.01m), 10m);
        }

        for (int i = 0; i < bidTrades3s; i++)
        {
            book.RecordTrade(nowMs - 1000, nowMs - 1000, (double)(bid - 0.01m), 10m);
        }

        return book;
    }

    private sealed class FakeExecutionService : IExecutionService
    {
        public bool CancelCalled { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(OrderIntent intent, CancellationToken ct = default)
        {
            return Task.FromResult(new ExecutionResult { Status = ExecutionStatus.Submitted });
        }

        public Task<ExecutionResult> ExecuteAsync(BracketIntent intent, CancellationToken ct = default)
        {
            return Task.FromResult(new ExecutionResult { Status = ExecutionStatus.Submitted });
        }

        public Task<ExecutionResult> CancelAsync(string brokerOrderId, CancellationToken ct = default)
        {
            CancelCalled = true;
            return Task.FromResult(new ExecutionResult { Status = ExecutionStatus.Cancelled });
        }
    }
}
