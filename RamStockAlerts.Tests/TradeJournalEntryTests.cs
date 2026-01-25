using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Tests.Helpers;
using Xunit;

namespace RamStockAlerts.Tests;

public class ShadowTradeJournalEntryTests
{
    [Fact]
    public void JournalEntry_IsFullyPopulated_WhenAccepted()
    {
        var entry = ShadowTradeJournalEntryTestHelper.BuildAcceptedEntry();

        Assert.Equal(ShadowTradeJournal.CurrentSchemaVersion, entry.SchemaVersion);
        Assert.Equal("Accepted", entry.DecisionOutcome);
        Assert.NotNull(entry.ObservedMetrics);
        Assert.NotNull(entry.DecisionInputs);
        Assert.NotNull(entry.DecisionTrace);
        Assert.NotNull(entry.DataQualityFlags);
        Assert.Equal(new[] { "ValidatorPass", "SpoofCheckPass", "ReplenishmentCheckPass", "AbsorptionCheckPass", "BlueprintPass", "ScarcityPass" }, entry.DecisionTrace);

        Assert.True(entry.ObservedMetrics.QueueImbalance.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.BidDepth4Level.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.AskDepth4Level.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.Spread.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.MidPrice.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.TapeVelocity3Sec.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.TapeVolume3Sec.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.BestBidPrice.GetValueOrDefault() > 0m);
        Assert.True(entry.ObservedMetrics.BestAskPrice.GetValueOrDefault() > 0m);

        Assert.True(entry.DecisionInputs.Score.GetValueOrDefault() > 0m);
        Assert.True(entry.DecisionInputs.RankScore.GetValueOrDefault() > 0m);
        Assert.True(entry.DecisionInputs.Spread.GetValueOrDefault() > 0m);
    }

    [Fact]
    public void JournalEntry_IsFullyPopulated_WhenRejected()
    {
        var entry = ShadowTradeJournalEntryTestHelper.BuildRejectedEntry();

        Assert.Equal("Rejected", entry.DecisionOutcome);
        Assert.Equal("TapeStale", entry.RejectionReason);
        Assert.NotNull(entry.ObservedMetrics);
        Assert.NotNull(entry.DecisionInputs);
        Assert.NotNull(entry.DecisionTrace);
        Assert.Contains("GateReject:TapeStale", entry.DecisionTrace);
        Assert.NotNull(entry.DataQualityFlags);
        Assert.Contains("TapeStale", entry.DataQualityFlags);
    }

    [Fact]
    public void JournalEntry_MissingContextReject_HasNullDecisionInputs_AndFlagsMissingContext()
    {
        var entry = ShadowTradeJournalEntryTestHelper.BuildMissingContextRejectionEntry();

        Assert.Equal("Rejected", entry.DecisionOutcome);
        Assert.Equal("MissingBookContext", entry.RejectionReason);
        
        // Missing context rejection: ObservedMetrics and DecisionInputs are null
        Assert.Null(entry.ObservedMetrics);
        Assert.Null(entry.DecisionInputs);
        
        Assert.NotNull(entry.DecisionTrace);
        Assert.Contains("GateReject:MissingBookContext", entry.DecisionTrace);
        
        Assert.NotNull(entry.DataQualityFlags);
        Assert.Contains("MissingBookContext", entry.DataQualityFlags);
    }

    [Fact]
    public async Task JournalEntry_MissingContextReject_WritesAndDeserializesSuccessfully()
    {
        var entry = ShadowTradeJournalEntryTestHelper.BuildMissingContextRejectionEntry();

        var line = await ShadowTradeJournalEntryTestHelper.WriteEntryAsync(entry);
        var parsed = JsonSerializer.Deserialize<ShadowTradeJournalEntry>(line);

        Assert.NotNull(parsed);
        Assert.Equal("Rejected", parsed!.DecisionOutcome);
        Assert.Equal("MissingBookContext", parsed.RejectionReason);
        Assert.Null(parsed.ObservedMetrics);
        Assert.Null(parsed.DecisionInputs);
        Assert.Contains("MissingBookContext", parsed.DataQualityFlags ?? new List<string>());
        
        // Verify JSON contains explicit null for DecisionInputs
        Assert.Contains("\"DecisionInputs\":null", line);
    }

    [Fact]
    public async Task JournalSchemaVersion_Bumps_WhenModelChanges()
    {
        var entry = ShadowTradeJournalEntryTestHelper.BuildAcceptedEntry();
        entry.SchemaVersion = 0;

        var line = await ShadowTradeJournalEntryTestHelper.WriteEntryAsync(entry);
        var json = JsonDocument.Parse(line);
        Assert.Equal(ShadowTradeJournal.CurrentSchemaVersion, json.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task JournalWriter_WritesSingleLine_WithRequiredFields()
    {
        var entry = ShadowTradeJournalEntryTestHelper.BuildAcceptedEntry();

        var line = await ShadowTradeJournalEntryTestHelper.WriteEntryAsync(entry);
        var parsed = JsonSerializer.Deserialize<ShadowTradeJournalEntry>(line);

        Assert.NotNull(parsed);
        Assert.Equal("Accepted", parsed!.DecisionOutcome);
        Assert.NotNull(parsed.ObservedMetrics);
        Assert.NotNull(parsed.DecisionInputs);
        Assert.NotNull(parsed.DecisionTrace);
        Assert.NotNull(parsed.DataQualityFlags);
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Symbol));
    }

}
