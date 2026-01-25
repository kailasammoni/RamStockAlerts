using System.Text.Json;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Tests for GateTrace schema serialization, ensuring deterministic output and proper schema versioning.
/// </summary>
public class GateTraceSerializationTests
{
    [Fact]
    public void GateTrace_SerializesToJson_WithAllFields()
    {
        // Arrange
        var gateTrace = new TradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = 1768480310000,
            
            // Tape context
            LastTradeMs = 1768480305000,
            TradesInWarmupWindow = 12,
            WarmedUp = true,
            StaleAgeMs = 5000,
            
            // Depth context
            LastDepthMs = 1768480308000,
            DepthAgeMs = 2000,
            DepthRowsKnown = 5,
            DepthSupported = true,
            
            // Config snapshot
            WarmupMinTrades = 5,
            WarmupWindowMs = 10000,
            StaleWindowMs = 5000,
            DepthStaleWindowMs = 2000
        };

        // Act
        var json = JsonSerializer.Serialize(gateTrace, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<TradeJournalEntry.GateTraceSnapshot>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.SchemaVersion);
        Assert.Equal(1768480310000, deserialized.NowMs);
        
        // Tape assertions
        Assert.Equal(1768480305000, deserialized.LastTradeMs);
        Assert.Equal(12, deserialized.TradesInWarmupWindow);
        Assert.True(deserialized.WarmedUp);
        Assert.Equal(5000, deserialized.StaleAgeMs);
        
        // Depth assertions
        Assert.Equal(1768480308000, deserialized.LastDepthMs);
        Assert.Equal(2000, deserialized.DepthAgeMs);
        Assert.Equal(5, deserialized.DepthRowsKnown);
        Assert.True(deserialized.DepthSupported);
        
        // Config assertions
        Assert.Equal(5, deserialized.WarmupMinTrades);
        Assert.Equal(10000, deserialized.WarmupWindowMs);
        Assert.Equal(5000, deserialized.StaleWindowMs);
        Assert.Equal(2000, deserialized.DepthStaleWindowMs);
    }

    [Fact]
    public void GateTrace_WithNullOptionalFields_SerializesProperly()
    {
        // Arrange: Simulates scenario where tape has no trades and depth not initialized
        var gateTrace = new TradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = 1768480310000,
            
            // Tape context - no trades
            LastTradeMs = null,
            TradesInWarmupWindow = 0,
            WarmedUp = false,
            StaleAgeMs = null,
            
            // Depth context - not initialized
            LastDepthMs = null,
            DepthAgeMs = null,
            DepthRowsKnown = null,
            DepthSupported = false,
            
            // Config snapshot (always present)
            WarmupMinTrades = 5,
            WarmupWindowMs = 10000,
            StaleWindowMs = 5000,
            DepthStaleWindowMs = 2000
        };

        // Act
        var json = JsonSerializer.Serialize(gateTrace, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<TradeJournalEntry.GateTraceSnapshot>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Null(deserialized.LastTradeMs);
        Assert.Equal(0, deserialized.TradesInWarmupWindow);
        Assert.False(deserialized.WarmedUp);
        Assert.Null(deserialized.StaleAgeMs);
        Assert.Null(deserialized.LastDepthMs);
        Assert.Null(deserialized.DepthAgeMs);
        Assert.Null(deserialized.DepthRowsKnown);
        Assert.False(deserialized.DepthSupported);
    }

    [Fact]
    public void GateTrace_InJournalEntry_SerializesComplete()
    {
        // Arrange
        var entry = new TradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Source = "IBKR",
            EntryType = "Rejection",
            Symbol = "AAPL",
            RejectionReason = "NotReady_TapeNotWarmedUp",
            GateTrace = new TradeJournalEntry.GateTraceSnapshot
            {
                SchemaVersion = 1,
                NowMs = 1768480310000,
                LastTradeMs = 1768480308000,
                TradesInWarmupWindow = 3,
                WarmedUp = false,
                StaleAgeMs = 2000,
                LastDepthMs = 1768480309000,
                DepthAgeMs = 1000,
                DepthRowsKnown = 5,
                DepthSupported = true,
                WarmupMinTrades = 5,
                WarmupWindowMs = 10000,
                StaleWindowMs = 5000,
                DepthStaleWindowMs = 2000
            }
        };

        // Act
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<TradeJournalEntry>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.GateTrace);
        Assert.Equal(1, deserialized.GateTrace.SchemaVersion);
        Assert.Equal(1768480310000, deserialized.GateTrace.NowMs);
        Assert.Equal(3, deserialized.GateTrace.TradesInWarmupWindow);
        Assert.False(deserialized.GateTrace.WarmedUp);
    }

    [Fact]
    public void GateTrace_SchemaVersioning_IsEnforced()
    {
        // Arrange
        var gateTrace = new TradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = 1768480310000,
            TradesInWarmupWindow = 0,
            WarmedUp = false,
            DepthSupported = false,
            WarmupMinTrades = 5,
            WarmupWindowMs = 10000,
            StaleWindowMs = 5000,
            DepthStaleWindowMs = 2000
        };

        // Act
        var json = JsonSerializer.Serialize(gateTrace);

        // Assert
        Assert.Contains("\"SchemaVersion\":1", json);
    }

    [Fact]
    public void GateTrace_DeterministicSerialization_NoImplicitDefaults()
    {
        // Arrange: Two identical instances
        var gateTrace1 = new TradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = 1768480310000,
            LastTradeMs = 1768480305000,
            TradesInWarmupWindow = 12,
            WarmedUp = true,
            StaleAgeMs = 5000,
            LastDepthMs = 1768480308000,
            DepthAgeMs = 2000,
            DepthRowsKnown = 5,
            DepthSupported = true,
            WarmupMinTrades = 5,
            WarmupWindowMs = 10000,
            StaleWindowMs = 5000,
            DepthStaleWindowMs = 2000
        };

        var gateTrace2 = new TradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = 1768480310000,
            LastTradeMs = 1768480305000,
            TradesInWarmupWindow = 12,
            WarmedUp = true,
            StaleAgeMs = 5000,
            LastDepthMs = 1768480308000,
            DepthAgeMs = 2000,
            DepthRowsKnown = 5,
            DepthSupported = true,
            WarmupMinTrades = 5,
            WarmupWindowMs = 10000,
            StaleWindowMs = 5000,
            DepthStaleWindowMs = 2000
        };

        // Act
        var json1 = JsonSerializer.Serialize(gateTrace1);
        var json2 = JsonSerializer.Serialize(gateTrace2);

        // Assert: Deterministic output
        Assert.Equal(json1, json2);
    }

    [Fact]
    public void GateTrace_JsonStructure_MatchesExpectedSchema()
    {
        // Arrange
        var gateTrace = new TradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = 1768480310000,
            LastTradeMs = 1768480305000,
            TradesInWarmupWindow = 12,
            WarmedUp = true,
            StaleAgeMs = 5000,
            LastDepthMs = 1768480308000,
            DepthAgeMs = 2000,
            DepthRowsKnown = 5,
            DepthSupported = true,
            WarmupMinTrades = 5,
            WarmupWindowMs = 10000,
            StaleWindowMs = 5000,
            DepthStaleWindowMs = 2000
        };

        // Act
        var json = JsonSerializer.Serialize(gateTrace, new JsonSerializerOptions { WriteIndented = false });

        // Assert: Verify all expected fields are present in JSON
        Assert.Contains("\"SchemaVersion\":", json);
        Assert.Contains("\"NowMs\":", json);
        Assert.Contains("\"LastTradeMs\":", json);
        Assert.Contains("\"TradesInWarmupWindow\":", json);
        Assert.Contains("\"WarmedUp\":", json);
        Assert.Contains("\"StaleAgeMs\":", json);
        Assert.Contains("\"LastDepthMs\":", json);
        Assert.Contains("\"DepthAgeMs\":", json);
        Assert.Contains("\"DepthRowsKnown\":", json);
        Assert.Contains("\"DepthSupported\":", json);
        Assert.Contains("\"WarmupMinTrades\":", json);
        Assert.Contains("\"WarmupWindowMs\":", json);
        Assert.Contains("\"StaleWindowMs\":", json);
        Assert.Contains("\"DepthStaleWindowMs\":", json);
    }

    [Fact]
    public void GateTrace_WhenDisabled_NotEmitted()
    {
        // Arrange: Entry without GateTrace (feature disabled)
        var entry = new TradeJournalEntry
        {
            SchemaVersion = 2,
            Symbol = "AAPL",
            RejectionReason = "NotReady_NoDepth",
            GateTrace = null
        };

        // Act
        var json = JsonSerializer.Serialize(entry);

        // Assert: GateTrace field should be null/absent
        var deserialized = JsonSerializer.Deserialize<TradeJournalEntry>(json);
        Assert.Null(deserialized?.GateTrace);
    }
}

