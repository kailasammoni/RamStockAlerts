using System.Reflection;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 2.2: Test warning rendering in daily rollup report for targets not met.
/// </summary>
public class DailyRollupMetricsWarningsTests
{
    [Fact]
    public void DailyRollup_WinRateBelowThreshold_ShowsWarning()
    {
        // Arrange: 2 wins, 3 losses = 40% win rate (below 60% target)
        var report = BuildTestReport(winCount: 2, lossCount: 3, avgWinR: 2.0m, avgLossR: 1.0m);

        // Act & Assert
        Assert.Contains("Win rate 40.0% below target", report);
        Assert.Contains("WARNINGS:", report);
    }

    [Fact]
    public void DailyRollup_WinRateAtThreshold_NoWarning()
    {
        // Arrange: 3 wins, 2 losses = 60% win rate (at target)
        var report = BuildTestReport(winCount: 3, lossCount: 2, avgWinR: 2.0m, avgLossR: 1.0m);

        // Act & Assert
        Assert.DoesNotContain("⚠ Win rate", report);
    }

    [Fact]
    public void DailyRollup_ExpectancyBelowThreshold_ShowsWarning()
    {
        // Arrange: (0.6 * 1.0) - (0.4 * 1.5) = 0.6 - 0.6 = 0R (below 0.25R target)
        var report = BuildTestReport(winCount: 3, lossCount: 2, avgWinR: 1.0m, avgLossR: 1.5m);

        // Act & Assert
        Assert.Contains("Expectancy 0.00R below target", report);
    }

    [Fact]
    public void DailyRollup_ExpectancyAtThreshold_NoWarning()
    {
        // Arrange: (0.75 * 2.0) - (0.25 * 1.0) = 1.5 - 0.25 = 1.25R (well above 0.25R target)
        var report = BuildTestReport(winCount: 3, lossCount: 1, avgWinR: 2.0m, avgLossR: 1.0m);

        // Act & Assert
        Assert.DoesNotContain("⚠ Expectancy", report);
    }

    [Fact]
    public void DailyRollup_InsufficientClosedTrades_ShowsWarning()
    {
        // Arrange: Only 2 closed trades (below 3 minimum for statistical significance)
        var report = BuildTestReport(winCount: 1, lossCount: 1, avgWinR: 2.0m, avgLossR: 1.0m, openCount: 5);

        // Act & Assert
        Assert.Contains("Only 2 closed trade(s) - insufficient sample for metrics", report);
    }

    [Fact]
    public void DailyRollup_UnfavorableRiskReward_ShowsWarning()
    {
        // Arrange: Average loss (1.5R) >= average win (1.0R) = unfavorable
        var report = BuildTestReport(winCount: 2, lossCount: 2, avgWinR: 1.0m, avgLossR: 1.5m);

        // Act & Assert
        Assert.Contains("Avg loss 1.50R >= avg win 1.00R", report);
    }

    [Fact]
    public void DailyRollup_FavorableRiskReward_NoWarning()
    {
        // Arrange: Average win (2.0R) > average loss (1.0R) = favorable
        var report = BuildTestReport(winCount: 2, lossCount: 2, avgWinR: 2.0m, avgLossR: 1.0m);

        // Act & Assert
        Assert.DoesNotContain("⚠ Avg loss", report);
        Assert.DoesNotContain("⚠ Avg win", report);
    }

    [Fact]
    public void DailyRollup_MultipleWarnings_AllDisplayed()
    {
        // Arrange: Low win rate (40%), low expectancy (-1.0R), unfavorable risk/reward
        // winCount=2, lossCount=3, avgWinR=0.5, avgLossR=2.0
        // expectancy = (0.4 * 0.5) - (0.6 * 2.0) = 0.2 - 1.2 = -1.0R
        var report = BuildTestReport(winCount: 2, lossCount: 3, avgWinR: 0.5m, avgLossR: 2.0m);

        // Act & Assert
        Assert.Contains("Win rate 40.0% below target", report);
        Assert.Contains("Expectancy -1.00R below target", report);
        Assert.Contains("Avg loss 2.00R >= avg win 0.50R", report);
        Assert.Contains("WARNINGS:", report);
    }

    [Fact]
    public void DailyRollup_NoOutcomes_NoWarnings()
    {
        // Arrange: Build report with no outcomes
        var stats = GetPrivateRollupStats();
        var report = InvokeRender(stats, "test-journal.jsonl");

        // Act & Assert
        Assert.DoesNotContain("⚠ WARNINGS:", report);
        Assert.Contains("Performance Metrics: no outcomes yet", report);
    }

    [Fact]
    public void DailyRollup_PerfectMetrics_NoWarnings()
    {
        // Arrange: 5 wins, 1 loss, 2.0R avg win, 1.0R avg loss = 83% win rate, 0.83R expectancy
        var report = BuildTestReport(winCount: 5, lossCount: 1, avgWinR: 2.0m, avgLossR: 1.0m);

        // Act & Assert
        Assert.DoesNotContain("⚠", report);
        Assert.Contains("- Win rate: 83.3%", report);
    }

    /// <summary>
    /// Helper: Build a test report with specific outcome metrics.
    /// </summary>
    private static string BuildTestReport(
        int winCount = 0, 
        int lossCount = 0, 
        decimal avgWinR = 0m, 
        decimal avgLossR = 0m,
        int openCount = 0,
        int noHitCount = 0)
    {
        var stats = GetPrivateRollupStats();

        // Record outcomes programmatically
        for (var i = 0; i < winCount; i++)
        {
            var outcome = new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "TEST",
                Direction = "Long",
                EntryPrice = 100m,
                StopPrice = 95m,
                TargetPrice = 110m,
                ExitPrice = 110m,
                OutcomeType = "HitTarget",
                RiskMultiple = avgWinR,
                PnlUsd = avgWinR * 100m,
                IsWin = true,
                DurationSeconds = 300,
                SchemaVersion = 1
            };
            InvokeRecordOutcome(stats, outcome);
        }

        for (var i = 0; i < lossCount; i++)
        {
            var outcome = new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "TEST",
                Direction = "Long",
                EntryPrice = 100m,
                StopPrice = 95m,
                TargetPrice = 110m,
                ExitPrice = 95m,
                OutcomeType = "HitStop",
                RiskMultiple = -avgLossR,
                PnlUsd = -avgLossR * 100m,
                IsWin = false,
                DurationSeconds = 60,
                SchemaVersion = 1
            };
            InvokeRecordOutcome(stats, outcome);
        }

        for (var i = 0; i < openCount; i++)
        {
            var outcome = new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "TEST",
                Direction = "Long",
                EntryPrice = 100m,
                StopPrice = 95m,
                TargetPrice = 110m,
                ExitPrice = null,
                OutcomeType = "NoExit",
                RiskMultiple = null,
                PnlUsd = null,
                IsWin = false,
                DurationSeconds = null,
                SchemaVersion = 1
            };
            InvokeRecordOutcome(stats, outcome);
        }

        for (var i = 0; i < noHitCount; i++)
        {
            var outcome = new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "TEST",
                Direction = "Long",
                EntryPrice = 100m,
                StopPrice = 95m,
                TargetPrice = 110m,
                ExitPrice = 105m,
                OutcomeType = "NoHit",
                RiskMultiple = 0.5m,
                PnlUsd = 500m,
                IsWin = false,
                DurationSeconds = 600,
                SchemaVersion = 1
            };
            InvokeRecordOutcome(stats, outcome);
        }

        return InvokeRender(stats, "test-journal.jsonl");
    }

    /// <summary>
    /// Helper: Get a private RollupStats instance via reflection.
    /// </summary>
    private static object GetPrivateRollupStats()
    {
        var reporterType = typeof(DailyRollupReporter);
        var rollupStatsType = reporterType.GetNestedType("RollupStats", BindingFlags.NonPublic);
        if (rollupStatsType == null)
            throw new InvalidOperationException("RollupStats type not found");

        var instance = Activator.CreateInstance(rollupStatsType);
        if (instance == null)
            throw new InvalidOperationException("Failed to create RollupStats instance");

        return instance;
    }

    /// <summary>
    /// Helper: Invoke RecordOutcome on RollupStats via reflection.
    /// </summary>
    private static void InvokeRecordOutcome(object stats, TradeOutcome outcome)
    {
        var method = stats.GetType().GetMethod("RecordOutcome", BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
            throw new InvalidOperationException("RecordOutcome method not found");

        method.Invoke(stats, [outcome]);
    }

    /// <summary>
    /// Helper: Invoke Render on RollupStats via reflection.
    /// </summary>
    private static string InvokeRender(object stats, string journalPath)
    {
        var method = stats.GetType().GetMethod("Render", BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
            throw new InvalidOperationException("Render method not found");

        var result = method.Invoke(stats, [journalPath]);
        return result?.ToString() ?? string.Empty;
    }
}
