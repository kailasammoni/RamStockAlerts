using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 3.4: Tests for DataQualityValidator - centralized data quality flag handling.
/// </summary>
public class DataQualityValidatorTests
{
    private readonly DataQualityValidator _validator = new();

    [Fact]
    public void BuildDataQualityFlags_PartialBook_AddsPartialBookFlag()
    {
        // Arrange
        var book = CreateValidBook();
        var depthSnapshot = new DepthSnapshot(
            BidsTopN: new[] { (100m, 10m), (99m, 10m) }, // 2 levels
            AsksTopN: new[] { (101m, 10m) },              // 1 level
            ExpectedDepthLevels: 5,                       // Expected 5
            LastDepthUpdateAgeMs: 100);
        var tapeStats = CreateValidTapeStats();
        var tapeStatus = CreateReadyTapeStatus();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var flags = _validator.BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs);

        // Assert
        Assert.Contains("PartialBook", flags);
        Assert.Contains("PartialBook:bidLevels=2", flags);
        Assert.Contains("PartialBook:askLevels=1", flags);
        Assert.Contains("PartialBook:expected=5", flags);
    }

    [Fact]
    public void BuildDataQualityFlags_StaleDepth_AddsStaleDepthFlag()
    {
        // Arrange
        var book = CreateValidBook();
        var depthSnapshot = new DepthSnapshot(
            BidsTopN: CreateFullDepth(5),
            AsksTopN: CreateFullDepth(5),
            ExpectedDepthLevels: 5,
            LastDepthUpdateAgeMs: 3000); // 3 seconds > 2 second threshold
        var tapeStats = CreateValidTapeStats();
        var tapeStatus = CreateReadyTapeStatus();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var flags = _validator.BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs);

        // Assert
        Assert.Contains("StaleDepth", flags);
        Assert.Contains("StaleDepth:ageMs=3000", flags);
    }

    [Fact]
    public void BuildDataQualityFlags_StaleTape_AddsStaleTapeFlags()
    {
        // Arrange
        var book = CreateValidBook();
        var depthSnapshot = CreateValidDepthSnapshot();
        var tapeStats = CreateValidTapeStats();
        var tapeStatus = new TapeStatusData(TapeStatusKind.Stale, AgeMs: 5000);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var flags = _validator.BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs);

        // Assert
        Assert.Contains("TapeStale", flags);
        Assert.Contains("StaleTick", flags); // Alias
        Assert.Contains("TapeStale:ageMs=5000", flags);
    }

    [Fact]
    public void BuildDataQualityFlags_TapeNotWarmedUp_AddsWarmupFlags()
    {
        // Arrange
        var book = CreateValidBook();
        var depthSnapshot = CreateValidDepthSnapshot();
        var tapeStats = CreateValidTapeStats();
        var tapeStatus = new TapeStatusData(
            TapeStatusKind.NotWarmedUp,
            AgeMs: 1000,
            TradesInWarmupWindow: 2,
            WarmupMinTrades: 5,
            WarmupWindowMs: 10000);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var flags = _validator.BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs);

        // Assert
        Assert.Contains("TapeNotWarmedUp", flags);
        Assert.Contains("TapeNotWarmedUp:tradesInWindow=2", flags);
        Assert.Contains("TapeNotWarmedUp:warmupMinTrades=5", flags);
        Assert.Contains("TapeNotWarmedUp:warmupWindowMs=10000", flags);
        Assert.Contains("TapeLastAgeMs=1000", flags);
    }

    [Fact]
    public void BuildDataQualityFlags_ValidData_ReturnsEmptyFlags()
    {
        // Arrange
        var book = CreateValidBook();
        var depthSnapshot = CreateValidDepthSnapshot();
        var tapeStats = CreateValidTapeStats();
        var tapeStatus = CreateReadyTapeStatus();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var flags = _validator.BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs);

        // Assert
        // Should have no critical flags (only informational if any)
        Assert.DoesNotContain("PartialBook", flags);
        Assert.DoesNotContain("StaleDepth", flags);
        Assert.DoesNotContain("TapeStale", flags);
    }

    [Fact]
    public void InterpretFlag_PartialBook_ReturnsCriticalSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("PartialBook");

        // Assert
        Assert.Equal(DataQualitySeverity.Critical, interpretation.Severity);
        Assert.Contains("incomplete", interpretation.Description.ToLower());
        Assert.Contains("retry", interpretation.RecommendedAction?.ToLower() ?? "");
    }

    [Fact]
    public void InterpretFlag_StaleTick_ReturnsCriticalSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("StaleTick");

        // Assert
        Assert.Equal(DataQualitySeverity.Critical, interpretation.Severity);
        Assert.Contains("stale", interpretation.Description.ToLower());
    }

    [Fact]
    public void InterpretFlag_TapeStale_ReturnsCriticalSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("TapeStale");

        // Assert
        Assert.Equal(DataQualitySeverity.Critical, interpretation.Severity);
        Assert.Contains("stale", interpretation.Description.ToLower());
    }

    [Fact]
    public void InterpretFlag_StaleDepth_ReturnsWarningSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("StaleDepth");

        // Assert
        Assert.Equal(DataQualitySeverity.Warning, interpretation.Severity);
        Assert.Contains("stale", interpretation.Description.ToLower());
    }

    [Fact]
    public void InterpretFlag_TapeNotWarmedUp_ReturnsWarningSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("TapeNotWarmedUp");

        // Assert
        Assert.Equal(DataQualitySeverity.Warning, interpretation.Severity);
        Assert.Contains("warmup", interpretation.Description.ToLower());
        Assert.Contains("watchlist", interpretation.RecommendedAction?.ToLower() ?? "");
    }

    [Fact]
    public void InterpretFlag_BookInvalid_ReturnsCriticalSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("BookInvalid:MissingBid");

        // Assert
        Assert.Equal(DataQualitySeverity.Critical, interpretation.Severity);
        Assert.Contains("invalid", interpretation.Description.ToLower());
    }

    [Fact]
    public void InterpretFlag_TapeMissingSubscription_ReturnsCriticalSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("TapeMissingSubscription");

        // Assert
        Assert.Equal(DataQualitySeverity.Critical, interpretation.Severity);
        Assert.Contains("missing", interpretation.Description.ToLower());
    }

    [Fact]
    public void InterpretFlag_InformationalFlag_ReturnsInfoSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("TapeLastAgeMs=500");

        // Assert
        Assert.Equal(DataQualitySeverity.Info, interpretation.Severity);
        Assert.Null(interpretation.RecommendedAction);
    }

    [Fact]
    public void InterpretFlag_UnknownFlag_ReturnsInfoSeverity()
    {
        // Act
        var interpretation = _validator.InterpretFlag("UnknownFlag");

        // Assert
        Assert.Equal(DataQualitySeverity.Info, interpretation.Severity);
    }

    [Fact]
    public void InterpretFlag_NullOrEmpty_ReturnsInfoSeverity()
    {
        // Act
        var interpretation1 = _validator.InterpretFlag(null!);
        var interpretation2 = _validator.InterpretFlag("");
        var interpretation3 = _validator.InterpretFlag("   ");

        // Assert
        Assert.Equal(DataQualitySeverity.Info, interpretation1.Severity);
        Assert.Equal(DataQualitySeverity.Info, interpretation2.Severity);
        Assert.Equal(DataQualitySeverity.Info, interpretation3.Severity);
    }

    [Fact]
    public void HasCriticalIssues_WithCriticalFlag_ReturnsTrue()
    {
        // Arrange
        var flags = new[] { "PartialBook", "TapeLastAgeMs=100" };

        // Act
        var hasCritical = _validator.HasCriticalIssues(flags);

        // Assert
        Assert.True(hasCritical);
    }

    [Fact]
    public void HasCriticalIssues_WithOnlyWarnings_ReturnsFalse()
    {
        // Arrange
        var flags = new[] { "TapeNotWarmedUp", "StaleDepth" };

        // Act
        var hasCritical = _validator.HasCriticalIssues(flags);

        // Assert
        Assert.False(hasCritical);
    }

    [Fact]
    public void HasCriticalIssues_WithNoFlags_ReturnsFalse()
    {
        // Arrange
        var flags = Array.Empty<string>();

        // Act
        var hasCritical = _validator.HasCriticalIssues(flags);

        // Assert
        Assert.False(hasCritical);
    }

    [Fact]
    public void HasCriticalIssues_WithNullFlags_ReturnsFalse()
    {
        // Act
        var hasCritical = _validator.HasCriticalIssues(null!);

        // Assert
        Assert.False(hasCritical);
    }

    [Fact]
    public void HasCriticalIssues_MultipleCriticalFlags_ReturnsTrue()
    {
        // Arrange
        var flags = new[] { "PartialBook", "StaleTick", "BookInvalid:MissingBid" };

        // Act
        var hasCritical = _validator.HasCriticalIssues(flags);

        // Assert
        Assert.True(hasCritical);
    }

    [Fact]
    public void BuildDataQualityFlags_CombinedIssues_AddsAllRelevantFlags()
    {
        // Arrange
        var book = CreateValidBook();
        var depthSnapshot = new DepthSnapshot(
            BidsTopN: new[] { (100m, 10m) }, // 1 level (partial)
            AsksTopN: new[] { (101m, 10m) }, // 1 level (partial)
            ExpectedDepthLevels: 5,
            LastDepthUpdateAgeMs: 3000);     // Stale
        var tapeStats = CreateValidTapeStats();
        var tapeStatus = new TapeStatusData(TapeStatusKind.Stale, AgeMs: 5000);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var flags = _validator.BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs);

        // Assert
        Assert.Contains("PartialBook", flags);
        Assert.Contains("StaleDepth", flags);
        Assert.Contains("TapeStale", flags);
        Assert.Contains("StaleTick", flags);
    }

    // Helper Methods

    private OrderBookState CreateValidBook()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = new OrderBookState("AAPL");
        book.ApplyDepthUpdate(new DepthUpdate(
            "AAPL",
            DepthSide.Bid,
            DepthOperation.Insert,
            100.0m,
            100m,
            0,
            nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(
            "AAPL",
            DepthSide.Ask,
            DepthOperation.Insert,
            100.10m,
            100m,
            0,
            nowMs));
        return book;
    }

    private DepthSnapshot CreateValidDepthSnapshot()
    {
        return new DepthSnapshot(
            BidsTopN: CreateFullDepth(5),
            AsksTopN: CreateFullDepth(5),
            ExpectedDepthLevels: 5,
            LastDepthUpdateAgeMs: 100);
    }

    private IReadOnlyList<(decimal Price, decimal Size)> CreateFullDepth(int levels)
    {
        var depth = new List<(decimal, decimal)>();
        for (int i = 0; i < levels; i++)
        {
            depth.Add((100m - i, 100m));
        }
        return depth;
    }

    private TapeStats CreateValidTapeStats()
    {
        return new TapeStats(
            VelocityTps: 1.5m,
            TradesInWindow: 10,
            LastTradeAgeMs: 100);
    }

    private TapeStatusData CreateReadyTapeStatus()
    {
        return new TapeStatusData(TapeStatusKind.Ready, AgeMs: 100);
    }
}
