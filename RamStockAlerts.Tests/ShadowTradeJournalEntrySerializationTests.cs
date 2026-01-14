using System;
using System.Text.Json;
using RamStockAlerts.Models;
using Xunit;

namespace RamStockAlerts.Tests;

public class ShadowTradeJournalEntrySerializationTests
{
    [Fact]
    public void JournalSerialization_IsStable_AndOneLineJson()
    {
        var entry = new ShadowTradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Source = "IBKR",
            EntryType = "Signal",
            MarketTimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            DecisionTimestampUtc = DateTimeOffset.UtcNow,
            TradingMode = "Shadow",
            Symbol = "TEST",
            Direction = "BUY",
            DecisionOutcome = "Accepted",
            ObservedMetrics = new ShadowTradeJournalEntry.ObservedMetricsSnapshot
            {
                QueueImbalance = 2.9m,
                Spread = 0.02m,
                BestBidPrice = 100.10m,
                BestAskPrice = 100.12m
            },
            DecisionInputs = null,
            DecisionTrace = new List<string> { "ValidatorPass", "ScarcityPass" },
            DataQualityFlags = new List<string>()
        };

        var json = JsonSerializer.Serialize(entry);

        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("\r", json);
        Assert.Contains("\"DecisionInputs\":null", json);
        Assert.NotNull(JsonSerializer.Deserialize<ShadowTradeJournalEntry>(json));
    }
}
