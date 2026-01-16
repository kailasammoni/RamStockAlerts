using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Unit tests for ActiveUniverse gating in ShadowTradingCoordinator.
/// Verifies that symbols not in ActiveUniverse are skipped early and do not produce gate rejections.
/// </summary>
public class ShadowTradingCoordinatorActiveUniverseTests
{
    [Fact]
    public void ProcessSnapshot_WithInactiveSymbol_SkipsEvaluation()
    {
        // Arrange
        var (coordinator, subscriptionManager, journal) = CreateCoordinator();
        
        // Set ActiveUniverse to only contain AAPL
        subscriptionManager.SetActiveUniverse(new[] { "AAPL" }, "TestSetup");
        
        // Create book for MSFT (not in ActiveUniverse)
        var book = CreateValidBook("MSFT", nowMs: 1000000000L);
        
        // Act
        coordinator.ProcessSnapshot(book, 1000000000L);
        
        // Assert
        // No journal entries should be created for inactive symbols
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public void ProcessSnapshot_WithActiveSymbol_ProcessesNormally()
    {
        // Arrange
        var (coordinator, subscriptionManager, journal) = CreateCoordinator();
        
        // Set ActiveUniverse to contain AAPL
        subscriptionManager.SetActiveUniverse(new[] { "AAPL" }, "TestSetup");
        
        // Set subscriptions for AAPL so it passes depth/tape checks
        // Note: In a real scenario, subscriptions would be set via ApplyUniverseAsync
        // For testing, we just need to verify the ActiveUniverse gate works
        
        // Create book for AAPL (in ActiveUniverse)
        var book = CreateValidBook("AAPL", nowMs: 1000000000L);
        
        // Act
        // Note: This will still fail other gates (no subscriptions set up),
        // but the point is it gets past the ActiveUniverse gate
        coordinator.ProcessSnapshot(book, 1000000000L);
        
        // Assert
        // We expect a gate rejection entry because AAPL is Active but missing subscriptions
        // This proves the ActiveUniverse gate allowed processing to continue
        Assert.NotEmpty(journal.Entries);
    }

    [Fact]
    public void ProcessSnapshot_MultipleInactiveSymbols_NoneProcessed()
    {
        // Arrange
        var (coordinator, subscriptionManager, journal) = CreateCoordinator();
        
        // Set ActiveUniverse to empty
        subscriptionManager.SetActiveUniverse(Array.Empty<string>(), "TestSetup");
        
        // Act - process multiple inactive symbols
        coordinator.ProcessSnapshot(CreateValidBook("AAPL", 1000000000L), 1000000000L);
        coordinator.ProcessSnapshot(CreateValidBook("MSFT", 1000000000L), 1000000000L);
        coordinator.ProcessSnapshot(CreateValidBook("GOOGL", 1000000000L), 1000000000L);
        
        // Assert
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public void ProcessSnapshot_InactiveSymbol_NoGateRejectionEntry()
    {
        // Arrange
        var (coordinator, subscriptionManager, journal) = CreateCoordinator();
        
        // Set ActiveUniverse to not include MSFT
        subscriptionManager.SetActiveUniverse(new[] { "AAPL" }, "TestSetup");
        
        // Create book for MSFT with invalid state (would normally trigger gate rejection)
        var book = new OrderBookState("MSFT");
        // Don't set any depth levels - book is invalid
        
        // Act
        coordinator.ProcessSnapshot(book, 1000000000L);
        
        // Assert
        // No gate rejection should be logged for inactive symbol
        Assert.Empty(journal.Entries);
        
        // Verify no entries with "NotReady_" reasons
        foreach (var entry in journal.Entries)
        {
            Assert.False(entry.RejectionReason?.StartsWith("NotReady_") ?? false,
                "No NotReady_ rejections should be created for inactive symbols");
        }
    }

    [Fact]
    public void ProcessSnapshot_CaseInsensitive_ActiveUniverseCheck()
    {
        // Arrange
        var (coordinator, subscriptionManager, journal) = CreateCoordinator();
        
        // Set ActiveUniverse with uppercase
        subscriptionManager.SetActiveUniverse(new[] { "AAPL" }, "TestSetup");
        
        // Create book with lowercase symbol
        var book = CreateValidBook("aapl", nowMs: 1000000000L);
        
        // Act
        coordinator.ProcessSnapshot(book, 1000000000L);
        
        // Assert
        // Should be processed because ActiveUniverse check is case-insensitive
        Assert.NotEmpty(journal.Entries);
    }

    private static (ShadowTradingCoordinator coordinator, MarketDataSubscriptionManager subscriptionManager, FakeJournal journal) 
        CreateCoordinator()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TradingMode"] = "Shadow",
                ["RecordBlueprints"] = "false",
                ["MarketData:MaxLines"] = "95",
                ["MarketData:TickByTickMaxSymbols"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:TapeWarmupMinTrades"] = "1",
                ["MarketData:TapeWarmupWindowMs"] = "15000",
                ["MarketData:TapeStaleWindowMs"] = "30000"
            })
            .Build();

        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(
            NullLogger<OrderFlowSignalValidator>.Instance,
            metrics);
        
        var journal = new FakeJournal();
        var scarcityController = new ScarcityController(config);
        
        var classificationCache = new ContractClassificationCache(
            config,
            NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(
            config,
            NullLogger<ContractClassificationService>.Instance,
            classificationCache);
        var eligibilityCache = new DepthEligibilityCache(
            config,
            NullLogger<DepthEligibilityCache>.Instance);
        var subscriptionManager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

        var coordinator = new ShadowTradingCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcityController,
            subscriptionManager,
            NullLogger<ShadowTradingCoordinator>.Instance);

        return (coordinator, subscriptionManager, journal);
    }

    private static OrderBookState CreateValidBook(string symbol, long nowMs)
    {
        var book = new OrderBookState(symbol);
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, 100.00m, 200m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, 100.05m, 200m, 0, nowMs));
        book.RecordTrade(nowMs - 1000, 100.02, 10m); // Trade 1 second ago
        return book;
    }

    /// <summary>
    /// Fake journal implementation for testing that tracks entries in memory
    /// </summary>
    private class FakeJournal : IShadowTradeJournal
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public List<ShadowTradeJournalEntry> Entries { get; } = new();

        public bool TryEnqueue(ShadowTradeJournalEntry entry)
        {
            Entries.Add(entry);
            return true;
        }
    }
}
