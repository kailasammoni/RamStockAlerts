using System;
using System.Text.Json;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Decisions;
using Xunit;

namespace RamStockAlerts.Tests;

public class ShadowTradeJournalEntrySerializationTests
{
    [Fact]
    public void Serialize_OmitsDecisionResult_WhenNull()
    {
        var entry = new ShadowTradeJournalEntry
        {
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            DecisionResult = null
        };

        var json = JsonSerializer.Serialize(entry);

        Assert.DoesNotContain("\"DecisionResult\"", json);
    }

    [Fact]
    public void Serialize_IncludesDecisionResult_WhenPresent()
    {
        var entry = new ShadowTradeJournalEntry
        {
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            DecisionResult = new StrategyDecisionResult
            {
                Outcome = DecisionOutcome.Accepted,
                Direction = TradeDirection.Buy,
                Score = 1m
            }
        };

        var json = JsonSerializer.Serialize(entry);

        Assert.Contains("\"DecisionResult\"", json);
    }
}
