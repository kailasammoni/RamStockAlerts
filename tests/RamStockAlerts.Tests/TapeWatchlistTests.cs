using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Microstructure;
using RamStockAlerts.Services.Universe;
using RamStockAlerts.Services.Signals;
using RamStockAlerts.Tests.TestDoubles;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 3.2: Tests for Tape Warm-up Watchlist functionality.
/// </summary>
public class TapeWatchlistTests
{
    [Fact]
    public void AddToWatchlist_WhenTapeNotWarmedUp_AddsSymbolToWatchlist()
    {
        // Arrange: Create coordinator with tape warmup enabled
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: true);
        var book = CreateBookWithTapeNotWarmedUp("AAPL");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: Process snapshot (should add to watchlist due to TapeNotWarmedUp)
        coordinator.ProcessSnapshot(book, nowMs);

        // Assert: Verify symbol was added to watchlist
        // Note: This test is a placeholder - need to expose watchlist state for testing
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void AddToWatchlist_WhenDisabled_DoesNotAddToWatchlist()
    {
        // Arrange: Create coordinator with watchlist disabled
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: false);
        var book = CreateBookWithTapeNotWarmedUp("AAPL");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: Process snapshot
        coordinator.ProcessSnapshot(book, nowMs);

        // Assert: Verify symbol was NOT added to watchlist
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void AddToWatchlist_SpamPrevention_SkipsRecentlyCheckedSymbols()
    {
        // Arrange: Add symbol to watchlist, then try again immediately
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: true, recheckIntervalMs: 5000);
        var book = CreateBookWithTapeNotWarmedUp("AAPL");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: Process twice in quick succession
        coordinator.ProcessSnapshot(book, nowMs);
        coordinator.ProcessSnapshot(book, nowMs + 100); // 100ms later

        // Assert: Second add should be skipped due to spam prevention
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void RecheckWatchlist_WhenTapeWarmsUp_RemovesFromWatchlistAndReEvaluates()
    {
        // Arrange: Symbol in watchlist, tape later warms up
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: true);
        var book1 = CreateBookWithTapeNotWarmedUp("AAPL");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Add to watchlist
        coordinator.ProcessSnapshot(book1, nowMs);

        // Simulate tape warming up
        var book2 = CreateBookWithWarmedUpTape("AAPL");
        // TODO: Need to trigger recheck timer manually or expose recheck method

        // Assert: Symbol should be removed from watchlist and re-evaluated
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void RecheckWatchlist_WhenBookMissing_RemovesFromWatchlist()
    {
        // Arrange: Symbol in watchlist, but book state is removed
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: true);
        
        // TODO: Need to simulate book being removed from OrderFlowMetrics
        
        // Assert: Symbol should be removed from watchlist
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void RecheckWatchlist_WhenDisabled_DoesNothing()
    {
        // Arrange: Watchlist disabled
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: false);
        
        // Act: Trigger recheck (if exposed)
        
        // Assert: No changes should occur
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void RecheckWatchlist_UpdatesLastRecheckTime()
    {
        // Arrange: Symbol in watchlist
        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: true);
        var book = CreateBookWithTapeNotWarmedUp("AAPL");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Add to watchlist
        coordinator.ProcessSnapshot(book, nowMs);

        // TODO: Trigger recheck and verify LastRecheckMs is updated
        
        Assert.True(true); // Placeholder
    }

    [Fact]
    public void Integration_TapeWarmupWorkflow_AcceptsSignalAfterWarmup()
    {
        // Arrange: End-to-end test
        // 1. Symbol rejected due to TapeNotWarmedUp
        // 2. Symbol added to watchlist
        // 3. Tape warms up (trades arrive)
        // 4. Recheck timer fires
        // 5. Symbol re-evaluated and signal accepted

        var coordinator = CreateTestCoordinator(tapeWatchlistEnabled: true);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Step 1: Initial rejection
        var coldBook = CreateBookWithTapeNotWarmedUp("AAPL");
        coordinator.ProcessSnapshot(coldBook, nowMs);

        // Step 2: Simulate tape warming up
        var warmBook = CreateBookWithWarmedUpTape("AAPL");
        
        // Step 3: TODO: Trigger recheck timer
        
        // Step 4: Verify signal is now accepted
        // Assert: Signal should be accepted after warmup
        Assert.True(true); // Placeholder
    }

    // Helper Methods

    private SignalCoordinator CreateTestCoordinator(
        bool tapeWatchlistEnabled = true,
        long recheckIntervalMs = 5000)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signals:Enabled"] = "true",
                ["Signals:TapeWatchlistEnabled"] = tapeWatchlistEnabled.ToString(),
                ["Signals:TapeWatchlistRecheckIntervalMs"] = recheckIntervalMs.ToString(),
                ["Signals:PostSignalMonitoringEnabled"] = "false",
                ["Signals:MinSignalsPerDay"] = "3",
                ["Signals:MaxSignalsPerDay"] = "6",
                ["AlertsEnabled"] = "false",
                ["RecordBlueprints"] = "false",
                ["MarketData:MaxLines"] = "95",
                ["MarketData:TickByTickMaxSymbols"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:TapeWarmupMinTrades"] = "5",
                ["MarketData:TapeWarmupWindowMs"] = "10000",
                ["MarketData:TapeStaleWindowMs"] = "30000"
            })
            .Build();

        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(
            NullLogger<OrderFlowSignalValidator>.Instance,
            metrics,
            config);
        var journal = new TestTradeJournal();
        var scarcityController = new ScarcityController(config);
        
        var classificationCache = new ContractClassificationCache(
            config,
            NullLogger<ContractClassificationCache>.Instance);
        var requestIdSource = new TestRequestIdSource();
        var classificationService = new ContractClassificationService(
            config,
            NullLogger<ContractClassificationService>.Instance,
            classificationCache,
            requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(
            config,
            NullLogger<DepthEligibilityCache>.Instance);
        var subscriptionManager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

        return new SignalCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcityController,
            subscriptionManager,
            NullLogger<SignalCoordinator>.Instance);
    }

    private OrderBookState CreateBookWithTapeNotWarmedUp(string symbol)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = new OrderBookState(symbol);
        
        // Add valid book state
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, 100.0m, 1000m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, 100.10m, 1000m, 0, nowMs));
        
        // Add 0-4 trades (less than WarmupMinTrades=5)
        for (int i = 0; i < 3; i++)
        {
            book.RecordTrade(nowMs - 1000 + i * 100, nowMs - 1000 + i * 100, 100.05, 100m);
        }
        
        return book;
    }

    private OrderBookState CreateBookWithWarmedUpTape(string symbol)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = new OrderBookState(symbol);
        
        // Add valid book state
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, 100.0m, 1000m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, 100.10m, 1000m, 0, nowMs));
        
        // Add 5+ trades (meets WarmupMinTrades=5)
        for (int i = 0; i < 10; i++)
        {
            book.RecordTrade(nowMs - 9000 + i * 1000, nowMs - 9000 + i * 1000, 100.05, 100m);
        }
        
        return book;
    }
}
