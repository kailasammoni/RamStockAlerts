using System.Text.Json;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 2.5: Manual verification of complete Phase 2 flow with realistic sample data.
/// Demonstrates the full integration: journal → outcomes → metrics → warnings → report.
/// </summary>
public class Phase2ManualVerificationTests : IDisposable
{
    private readonly string _tempDir;

    public Phase2ManualVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"phase2-verify-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Phase2_ManualVerification_RealisticTradingDay()
    {
        // Arrange: Simulate a realistic trading day with multiple trades
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");
        var reportPath = Path.Combine(_tempDir, "rollup-report.txt");

        // Create sample journal entries from a realistic trading session
        var entries = new[]
        {
            // Morning: 3 rejected signals
            CreateEntry("AAPL", "Long", "Rejected", "InsufficientTape"),
            CreateEntry("MSFT", "Short", "Rejected", "DepthNotAvailable"),
            CreateEntry("NVDA", "Long", "Rejected", "InsufficientQueueImbalance"),
            
            // Mid-morning: 2 accepted signals (winners)
            CreateEntry("AMZN", "Long", "Accepted"),
            CreateEntry("GOOGL", "Short", "Accepted"),
            
            // Mid-day: 1 rejected (scarcity)
            CreateEntry("TSLA", "Long", "Rejected", "ScarcityRejected"),
            
            // Afternoon: 1 accepted (looser), 2 rejected
            CreateEntry("META", "Long", "Accepted"),
            CreateEntry("NFLX", "Short", "Rejected", "InvalidTapeSignal"),
            CreateEntry("AMD", "Long", "Rejected", "InsufficientDepth"),
        };

        var journalContent = string.Join("\n", entries.Select(e => JsonSerializer.Serialize(e)));
        await File.WriteAllTextAsync(journalPath, journalContent + "\n");

        // Create outcomes for previous day's trades (3 wins, 2 losses)
        var previousOutcomes = new[]
        {
            new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "TSLA_PREV",
                Direction = "Long",
                EntryPrice = 250m,
                StopPrice = 245m,
                TargetPrice = 265m,
                ExitPrice = 265m,
                OutcomeType = "HitTarget",
                RiskMultiple = 3.0m,
                PnlUsd = 450m,
                IsWin = true,
                DurationSeconds = 600,
                SchemaVersion = 1
            },
            new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "SPY_PREV",
                Direction = "Short",
                EntryPrice = 450m,
                StopPrice = 455m,
                TargetPrice = 440m,
                ExitPrice = 440m,
                OutcomeType = "HitTarget",
                RiskMultiple = 2.0m,
                PnlUsd = 200m,
                IsWin = true,
                DurationSeconds = 1200,
                SchemaVersion = 1
            },
            new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "QQQ_PREV",
                Direction = "Long",
                EntryPrice = 380m,
                StopPrice = 375m,
                TargetPrice = 395m,
                ExitPrice = 395m,
                OutcomeType = "HitTarget",
                RiskMultiple = 3.0m,
                PnlUsd = 300m,
                IsWin = true,
                DurationSeconds = 1800,
                SchemaVersion = 1
            },
            new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "IWM_PREV",
                Direction = "Short",
                EntryPrice = 200m,
                StopPrice = 205m,
                TargetPrice = 190m,
                ExitPrice = 205m,
                OutcomeType = "HitStop",
                RiskMultiple = -1.0m,
                PnlUsd = -100m,
                IsWin = false,
                DurationSeconds = 900,
                SchemaVersion = 1
            },
            new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "EEM_PREV",
                Direction = "Long",
                EntryPrice = 42m,
                StopPrice = 40m,
                TargetPrice = 50m,
                ExitPrice = 40m,
                OutcomeType = "HitStop",
                RiskMultiple = -1.0m,
                PnlUsd = -100m,
                IsWin = false,
                DurationSeconds = 600,
                SchemaVersion = 1
            }
        };

        var outcomesContent = string.Join("\n", previousOutcomes.Select(o => JsonSerializer.Serialize(o)));
        await File.WriteAllTextAsync(outcomesPath, outcomesContent + "\n");

        // Act: Run full rollup with rotation and outcome loading
        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: true, outputPath: reportPath);

        // Assert
        Assert.Equal(0, result);
        Assert.True(File.Exists(reportPath), "Report file should be created");

        var report = await File.ReadAllTextAsync(reportPath);

        // Verify report contains expected sections
        Assert.Contains("Trade Daily Rollup", report);
        Assert.Contains("Total candidates:", report);  // Count format flexible
        Assert.Contains("Performance Metrics", report);

        // Verify metrics are present
        Assert.Contains("Win rate:", report);
        Assert.Contains("Total P&L:", report);

        // Print report for manual inspection
        System.Console.WriteLine("\n=== VERIFICATION REPORT ===");
        System.Console.WriteLine(report);
        System.Console.WriteLine("=== END REPORT ===\n");
    }

    [Fact]
    public async Task Phase2_ManualVerification_PerfectWinRate()
    {
        // Arrange: Simulate perfect trading day (all winners)
        var journalPath = Path.Combine(_tempDir, "perfect-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "perfect-outcomes.jsonl");
        var reportPath = Path.Combine(_tempDir, "perfect-report.txt");

        // Create 5 accepted entries
        var entries = new[]
        {
            CreateEntry("AAPL", "Long", "Accepted"),
            CreateEntry("MSFT", "Long", "Accepted"),
            CreateEntry("GOOGL", "Short", "Accepted"),
            CreateEntry("AMZN", "Long", "Accepted"),
            CreateEntry("NVDA", "Short", "Accepted"),
        };

        var journalContent = string.Join("\n", entries.Select(e => JsonSerializer.Serialize(e)));
        await File.WriteAllTextAsync(journalPath, journalContent + "\n");

        // Create outcomes: all targets hit with strong R-multiples
        var outcomes = new[]
        {
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "AAPL", Direction = "Long", OutcomeType = "HitTarget", RiskMultiple = 2.5m, PnlUsd = 250m, IsWin = true, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "MSFT", Direction = "Long", OutcomeType = "HitTarget", RiskMultiple = 3.0m, PnlUsd = 300m, IsWin = true, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "GOOGL", Direction = "Short", OutcomeType = "HitTarget", RiskMultiple = 2.0m, PnlUsd = 200m, IsWin = true, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "AMZN", Direction = "Long", OutcomeType = "HitTarget", RiskMultiple = 3.5m, PnlUsd = 350m, IsWin = true, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "NVDA", Direction = "Short", OutcomeType = "HitTarget", RiskMultiple = 2.5m, PnlUsd = 250m, IsWin = true, SchemaVersion = 1 },
        };

        var outcomesContent = string.Join("\n", outcomes.Select(o => JsonSerializer.Serialize(o)));
        await File.WriteAllTextAsync(outcomesPath, outcomesContent + "\n");

        // Act
        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: true, outputPath: reportPath);

        // Assert
        Assert.Equal(0, result);

        var report = await File.ReadAllTextAsync(reportPath);
        
        // Verify perfect metrics
        Assert.Contains("Win rate: 100.0%", report);
        Assert.DoesNotContain("WARNINGS", report); // No warnings should appear

        System.Console.WriteLine("\n=== PERFECT DAY REPORT ===");
        System.Console.WriteLine(report);
        System.Console.WriteLine("=== END REPORT ===\n");
    }

    [Fact]
    public async Task Phase2_ManualVerification_PoorPerformance()
    {
        // Arrange: Simulate poor trading day (low win rate, negative expectancy)
        var journalPath = Path.Combine(_tempDir, "poor-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "poor-outcomes.jsonl");
        var reportPath = Path.Combine(_tempDir, "poor-report.txt");

        // Create entries: 8 accepted, mostly losses
        var entries = Enumerable.Range(1, 8)
            .Select(i => CreateEntry($"TEST{i}", "Long", "Accepted"))
            .ToArray();

        var journalContent = string.Join("\n", entries.Select(e => JsonSerializer.Serialize(e)));
        await File.WriteAllTextAsync(journalPath, journalContent + "\n");

        // Create outcomes: 2 wins, 6 losses (25% win rate)
        var outcomes = new[]
        {
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST1", Direction = "Long", OutcomeType = "HitTarget", RiskMultiple = 1.5m, PnlUsd = 150m, IsWin = true, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST2", Direction = "Long", OutcomeType = "HitStop", RiskMultiple = -2.0m, PnlUsd = -200m, IsWin = false, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST3", Direction = "Long", OutcomeType = "HitStop", RiskMultiple = -2.0m, PnlUsd = -200m, IsWin = false, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST4", Direction = "Long", OutcomeType = "HitTarget", RiskMultiple = 1.0m, PnlUsd = 100m, IsWin = true, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST5", Direction = "Long", OutcomeType = "HitStop", RiskMultiple = -2.5m, PnlUsd = -250m, IsWin = false, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST6", Direction = "Long", OutcomeType = "HitStop", RiskMultiple = -2.0m, PnlUsd = -200m, IsWin = false, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST7", Direction = "Long", OutcomeType = "HitStop", RiskMultiple = -1.5m, PnlUsd = -150m, IsWin = false, SchemaVersion = 1 },
            new TradeOutcome { DecisionId = Guid.NewGuid(), Symbol = "TEST8", Direction = "Long", OutcomeType = "HitStop", RiskMultiple = -2.0m, PnlUsd = -200m, IsWin = false, SchemaVersion = 1 },
        };

        var outcomesContent = string.Join("\n", outcomes.Select(o => JsonSerializer.Serialize(o)));
        await File.WriteAllTextAsync(outcomesPath, outcomesContent + "\n");

        // Act
        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: true, outputPath: reportPath);

        // Assert
        Assert.Equal(0, result);

        var report = await File.ReadAllTextAsync(reportPath);
        
        // Verify poor performance is flagged
        Assert.Contains("Win rate: 25.0%", report);
        Assert.Contains("below target", report);    // Warning about win rate
        Assert.Contains("unfavorable risk/reward", report);  // Risk/reward warning

        System.Console.WriteLine("\n=== POOR PERFORMANCE REPORT ===");
        System.Console.WriteLine(report);
        System.Console.WriteLine("=== END REPORT ===\n");
    }

    /// <summary>
    /// Helper: Create a journal entry for testing.
    /// </summary>
    private static TradeJournalEntry CreateEntry(
        string symbol = "TEST",
        string direction = "Long",
        string decisionOutcome = "Accepted",
        string? rejectionReason = null)
    {
        return new TradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Symbol = symbol,
            Direction = direction,
            DecisionOutcome = decisionOutcome,
            RejectionReason = rejectionReason,
            DecisionTimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-System.Random.Shared.Next(3600)),
            DecisionInputs = new TradeJournalEntry.DecisionInputsSnapshot 
            { 
                Score = (decimal)(50 + System.Random.Shared.Next(40))
            },
            ObservedMetrics = new TradeJournalEntry.ObservedMetricsSnapshot 
            { 
                Spread = (decimal)(0.02 + System.Random.Shared.NextDouble() * 0.08),
                QueueImbalance = (decimal)(0.8 + System.Random.Shared.NextDouble() * 0.8),
                TapeAcceleration = (decimal)(0.5 + System.Random.Shared.NextDouble() * 2.0)
            },
            DecisionTrace = rejectionReason != null ? new List<string> { rejectionReason } : null
        };
    }
}



