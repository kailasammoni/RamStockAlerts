using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Tests for Phase 2: Daily metrics and reporting with outcome tracking.
/// </summary>
public class DailyRollupOutcomeTrackingTests
{
    [Fact]
    public void RollupStats_RecordsOutcomeMetrics_WinningTrades()
    {
        // Arrange - Use reflection to access the private RollupStats class
        var reporterType = typeof(DailyRollupReporter);
        var assembly = reporterType.Assembly;
        var rollupStatsType = assembly.GetType("RamStockAlerts.Services.DailyRollupReporter+RollupStats");

        Assert.NotNull(rollupStatsType);

        var stats = Activator.CreateInstance(rollupStatsType);
        Assert.NotNull(stats);

        var recordOutcomeMethod = rollupStatsType!.GetMethod("RecordOutcome");
        Assert.NotNull(recordOutcomeMethod);

        var getMetricsMethod = rollupStatsType.GetMethod("GetPerformanceMetrics");
        Assert.NotNull(getMetricsMethod);

        // Create a winning outcome
        var winOutcome = new TradeOutcome
        {
            Symbol = "AAPL",
            Direction = "Long",
            EntryPrice = 100m,
            ExitPrice = 110m,
            OutcomeType = "HitTarget",
            IsWin = true,
            RiskMultiple = 2m,
            PnlUsd = 10m
        };

        // Act
        recordOutcomeMethod!.Invoke(stats, new object[] { winOutcome });

        var metrics = getMetricsMethod!.Invoke(stats, null) as PerformanceMetrics;

        // Assert
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics!.WinCount);
        Assert.Equal(0, metrics.LossCount);
        Assert.Equal(1, metrics.TotalSignals);
        Assert.Equal(10m, metrics.TotalPnlUsd);
        Assert.Equal(2m, metrics.AvgWinRMultiple);
        Assert.Equal(1m, metrics.WinRate);
    }

    [Fact]
    public void RollupStats_RecordsOutcomeMetrics_MixedOutcomes()
    {
        // Arrange
        var reporterType = typeof(DailyRollupReporter);
        var assembly = reporterType.Assembly;
        var rollupStatsType = assembly.GetType("RamStockAlerts.Services.DailyRollupReporter+RollupStats");

        Assert.NotNull(rollupStatsType);

        var stats = Activator.CreateInstance(rollupStatsType);
        Assert.NotNull(stats);

        var recordOutcomeMethod = rollupStatsType!.GetMethod("RecordOutcome");
        var getMetricsMethod = rollupStatsType.GetMethod("GetPerformanceMetrics");

        var winOutcome = new TradeOutcome
        {
            Symbol = "AAPL",
            Direction = "Long",
            OutcomeType = "HitTarget",
            RiskMultiple = 2m,
            PnlUsd = 20m
        };

        var lossOutcome = new TradeOutcome
        {
            Symbol = "TSLA",
            Direction = "Short",
            OutcomeType = "HitStop",
            RiskMultiple = -1m,
            PnlUsd = -10m
        };

        var openOutcome = new TradeOutcome
        {
            Symbol = "SPY",
            Direction = "Long",
            OutcomeType = "NoExit"
        };

        // Act
        recordOutcomeMethod!.Invoke(stats, new object[] { winOutcome });
        recordOutcomeMethod.Invoke(stats, new object[] { lossOutcome });
        recordOutcomeMethod.Invoke(stats, new object[] { openOutcome });

        var metrics = getMetricsMethod!.Invoke(stats, null) as PerformanceMetrics;

        // Assert
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics!.WinCount);
        Assert.Equal(1, metrics.LossCount);
        Assert.Equal(1, metrics.OpenCount);
        Assert.Equal(3, metrics.TotalSignals);
        Assert.Equal(10m, metrics.TotalPnlUsd); // 20 - 10
        Assert.Equal(0.5m, metrics.WinRate); // 1 win / 2 closed
        Assert.Equal(2m, metrics.AvgWinRMultiple);
        Assert.Equal(1m, metrics.AvgLossRMultiple); // abs(-1) = 1
    }

    [Fact]
    public void PerformanceMetrics_CalculatesExpectancy()
    {
        // Arrange
        var metrics = new PerformanceMetrics
        {
            WinCount = 6,
            LossCount = 4,
            AvgWinRMultiple = 2m,
            AvgLossRMultiple = 1.5m
        };

        // Act
        var expectancy = metrics.Expectancy;

        // Assert: (0.6 * 2) - (0.4 * 1.5) = 1.2 - 0.6 = 0.6R
        Assert.NotNull(expectancy);
        Assert.Equal(0.6m, expectancy.Value);
    }

    [Fact]
    public void PerformanceMetrics_GeneratesSummary()
    {
        // Arrange
        var metrics = new PerformanceMetrics
        {
            WinCount = 5,
            LossCount = 3,
            OpenCount = 2,
            TotalSignals = 10,
            TotalPnlUsd = 150m,
            AvgWinRMultiple = 1.5m,
            AvgLossRMultiple = 1m
        };

        // Act
        var summary = metrics.Summary();

        // Assert
        Assert.NotNull(summary);
        Assert.Contains("Wins: 5", summary);
        Assert.Contains("Losses: 3", summary);
        Assert.Contains("Open: 2", summary);
        Assert.Contains("62.5%", summary); // 5/(5+3)
        Assert.Contains("+$150.00", summary);
        // Expectancy = (0.625 * 1.5) - (0.375 * 1) = 0.9375 - 0.375 = 0.5625
        Assert.Contains("0.56R", summary);
    }

    [Fact]
    public void PerformanceMetrics_HandlesZeroOutcomes()
    {
        // Arrange
        var metrics = new PerformanceMetrics();

        // Act
        var summary = metrics.Summary();

        // Assert
        Assert.Equal("No outcomes to report", summary);
    }

    [Fact]
    public void PerformanceMetrics_HandlesOpenOnly()
    {
        // Arrange
        var metrics = new PerformanceMetrics
        {
            OpenCount = 3,
            TotalSignals = 3
        };

        // Act
        var summary = metrics.Summary();

        // Assert
        Assert.NotNull(summary);
        Assert.Contains("Open: 3", summary);
    }
}
