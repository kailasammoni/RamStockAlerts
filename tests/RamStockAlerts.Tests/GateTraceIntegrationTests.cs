using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Signals;
using RamStockAlerts.Tests.Helpers;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Integration tests for GateTrace emission in rejection scenarios.
/// </summary>
public class GateTraceIntegrationTests
{
    [Fact]
    public async Task GateTrace_EmittedOnTapeNotWarmedUp_WithCorrectValues()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SignalsJournal:EmitGateTrace"] = "true",
                ["MarketData:TapeStaleWindowMs"] = "5000",
                ["MarketData:TapeWarmupMinTrades"] = "5",
                ["MarketData:TapeWarmupWindowMs"] = "10000",
                ["MarketData:MaxDepthSymbols"] = "1"
            })
            .Build();

        var journal = new InMemoryJournal();
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics);
        var scarcityController = SignalCoordinatorTestHelper.CreateScarcityController();
        
        // Create subscription manager with AAPL subscribed (tape + depth + tick-by-tick)
        var subscriptionManager = await SignalCoordinatorTestHelper.CreateSubscriptionManagerAsync(
            config,
            "AAPL",
            enableTickByTick: true);

        var coordinator = new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcityController,
            subscriptionManager,
            NullLogger<SignalCoordinator>.Instance);

        // Create book with insufficient warmup trades (3 out of 5 required)
        var book = new OrderBookState("AAPL") { MaxDepthRows = 10 };
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Add depth (valid)
        book.ApplyDepthUpdate(new DepthUpdate("AAPL", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, nowMs - 1000));
        book.ApplyDepthUpdate(new DepthUpdate("AAPL", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, nowMs - 1000));
        
        // Add only 3 trades (warmup requires 5)
        book.RecordTrade(nowMs - 5000, nowMs - 5000, 100.05, 10m);
        book.RecordTrade(nowMs - 3000, nowMs - 3000, 100.06, 15m);
        book.RecordTrade(nowMs - 1000, nowMs - 1000, 100.07, 20m);

        metrics.UpdateMetrics(book, nowMs);

        // Act
        coordinator.ProcessSnapshot(book, nowMs);
        await Task.Delay(100); // Allow async processing

        // Assert
        var entries = journal.GetEntries();
        Assert.Single(entries);
        
        var entry = entries[0];
        Assert.Equal("Rejection", entry.EntryType);
        Assert.Equal("NotReady_TapeNotWarmedUp", entry.RejectionReason);
        Assert.NotNull(entry.GateTrace);
        
        var trace = entry.GateTrace;
        Assert.Equal(1, trace.SchemaVersion);
        Assert.InRange(trace.NowMs, nowMs - 100, nowMs + 100); // Allow small timestamp tolerance
        Assert.Equal(3, trace.TradesInWarmupWindow);
        Assert.False(trace.WarmedUp);
        Assert.Equal(5, trace.WarmupMinTrades);
        Assert.Equal(10000, trace.WarmupWindowMs);
        Assert.NotNull(trace.LastTradeMs);
        Assert.True(trace.DepthSupported);
    }

    [Fact]
    public async Task GateTrace_NotEmittedWhenDisabled()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SignalsJournal:EmitGateTrace"] = "false", // Disabled
                ["MarketData:TapeStaleWindowMs"] = "5000",
                ["MarketData:TapeWarmupMinTrades"] = "5",
                ["MarketData:TapeWarmupWindowMs"] = "10000"
            })
            .Build();

        var journal = new InMemoryJournal();
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics);
        var scarcityController = SignalCoordinatorTestHelper.CreateScarcityController();
        var subscriptionManager = SignalCoordinatorTestHelper.CreateSubscriptionManager();

        var coordinator = new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcityController,
            subscriptionManager,
            NullLogger<SignalCoordinator>.Instance);

        var book = new OrderBookState("AAPL") { MaxDepthRows = 10 };
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        book.ApplyDepthUpdate(new DepthUpdate("AAPL", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, nowMs - 1000));
        book.ApplyDepthUpdate(new DepthUpdate("AAPL", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, nowMs - 1000));
        book.RecordTrade(nowMs - 1000, nowMs - 1000, 100.05, 10m);

        metrics.UpdateMetrics(book, nowMs);

        // Act
        coordinator.ProcessSnapshot(book, nowMs);
        await Task.Delay(100);

        // Assert
        var entries = journal.GetEntries();
        if (entries.Count > 0)
        {
            var entry = entries[0];
            Assert.Null(entry.GateTrace); // Should be null when disabled
        }
    }

    [Fact]
    public async Task GateTrace_CapturesDepthContext_WhenDepthNotSupported()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SignalsJournal:EmitGateTrace"] = "true",
                ["MarketData:TapeStaleWindowMs"] = "5000",
                ["MarketData:TapeWarmupMinTrades"] = "5",
                ["MarketData:TapeWarmupWindowMs"] = "10000"
            })
            .Build();

        var journal = new InMemoryJournal();
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics);
        var scarcityController = SignalCoordinatorTestHelper.CreateScarcityController();
        
        // Create subscription manager with depth disabled
        var subscriptionManager = SignalCoordinatorTestHelper.CreateSubscriptionManager(depthEnabled: false);

        var coordinator = new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcityController,
            subscriptionManager,
            NullLogger<SignalCoordinator>.Instance);

        var book = new OrderBookState("AAPL") { MaxDepthRows = 10 };
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Tape is healthy
        for (int i = 0; i < 10; i++)
        {
            var ts = nowMs - (10 - i) * 100;
            book.RecordTrade(ts, ts, 100.0 + i * 0.01, 10m);
        }

        metrics.UpdateMetrics(book, nowMs);

        // Act
        coordinator.ProcessSnapshot(book, nowMs);
        await Task.Delay(100);

        // Assert
        var entries = journal.GetEntries();
        if (entries.Count > 0)
        {
            var entry = entries[0];
            Assert.NotNull(entry.GateTrace);
            Assert.False(entry.GateTrace.DepthSupported);
            Assert.Null(entry.GateTrace.LastDepthMs);
            Assert.Null(entry.GateTrace.DepthAgeMs);
        }
    }

    private class InMemoryJournal : ITradeJournal
    {
        private readonly List<TradeJournalEntry> _entries = new();

        public Guid SessionId { get; } = Guid.NewGuid();

        public bool TryEnqueue(TradeJournalEntry entry)
        {
            _entries.Add(entry);
            return true;
        }

        public List<TradeJournalEntry> GetEntries() => _entries;
    }
}


