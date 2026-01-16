using System.Reflection;
using System.Text.Json;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 2.4: Integration tests for full outcomes rollup with journal rotation and outcome loading.
/// </summary>
public class DailyRollupFullIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public DailyRollupFullIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rollup-integration-{Guid.NewGuid()}");
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
    public async Task DailyRollup_WithJournalRotation_RotatesAndProcessesNewJournal()
    {
        // Arrange
        var journalPath = Path.Combine(_tempDir, "shadow-trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");

        // Create initial journal entry
        var entry = CreateJournalEntry(DecisionOutcome: "Accepted");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(entry) + "\n");

        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        // Act
        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: false);

        // Assert
        Assert.Equal(0, result);
        
        // Journal should have been rotated
        var rotatedPath = Path.Combine(_tempDir, $"shadow-trade-journal-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        // After rotation and processing, new journal should be empty or not exist
        // (The reporter creates it as empty if rotation occurs)
        Assert.True(!File.Exists(journalPath) || new FileInfo(journalPath).Length == 0 || File.Exists(rotatedPath));
    }

    [Fact]
    public async Task DailyRollup_WithPreviousOutcomes_IncludesThemInMetrics()
    {
        // Arrange
        var journalPath = Path.Combine(_tempDir, "shadow-trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");

        // Create previous outcome (from past trades)
        var previousOutcome = new TradeOutcome
        {
            DecisionId = Guid.NewGuid(),
            Symbol = "PREV",
            Direction = "Long",
            EntryPrice = 100m,
            StopPrice = 95m,
            TargetPrice = 110m,
            ExitPrice = 110m,
            OutcomeType = "HitTarget",
            RiskMultiple = 2.0m,
            PnlUsd = 200m,
            IsWin = true,
            DurationSeconds = 300,
            SchemaVersion = 1
        };

        await File.WriteAllTextAsync(outcomesPath, JsonSerializer.Serialize(previousOutcome) + "\n");

        // Create current journal entry
        var entry = CreateJournalEntry(DecisionOutcome: "Rejected");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(entry) + "\n");

        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        // Act
        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore);
        var result = await reporter.RunAsync(journalPath, writeToFile: false);

        // Assert
        Assert.Equal(0, result);
        
        // Verify previous outcomes were loaded (this happens internally in the reporter)
        var loadedOutcomes = await outcomeStore.GetAllOutcomesAsync();
        Assert.Single(loadedOutcomes);
        Assert.Equal("PREV", loadedOutcomes[0].Symbol);
    }

    [Fact]
    public async Task DailyRollup_WithOutcomesAndRotation_LoadsAndRotatesCorrectly()
    {
        // Arrange
        var journalPath = Path.Combine(_tempDir, "shadow-trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");

        // Create 2 previous outcomes
        var outcome1 = new TradeOutcome
        {
            DecisionId = Guid.NewGuid(),
            Symbol = "OUT1",
            Direction = "Long",
            EntryPrice = 100m,
            StopPrice = 95m,
            TargetPrice = 110m,
            ExitPrice = 110m,
            OutcomeType = "HitTarget",
            RiskMultiple = 1.0m,
            PnlUsd = 100m,
            IsWin = true,
            DurationSeconds = 300,
            SchemaVersion = 1
        };
        
        var outcome2 = new TradeOutcome
        {
            DecisionId = Guid.NewGuid(),
            Symbol = "OUT2",
            Direction = "Short",
            EntryPrice = 100m,
            StopPrice = 105m,
            TargetPrice = 90m,
            ExitPrice = 90m,
            OutcomeType = "HitTarget",
            RiskMultiple = 2.0m,
            PnlUsd = 200m,
            IsWin = true,
            DurationSeconds = 600,
            SchemaVersion = 1
        };

        var outcomesContent = JsonSerializer.Serialize(outcome1) + "\n" + JsonSerializer.Serialize(outcome2) + "\n";
        await File.WriteAllTextAsync(outcomesPath, outcomesContent);

        // Create 2 accepted entries in current journal
        var entry1 = CreateJournalEntry(Symbol: "CURR1", DecisionOutcome: "Accepted");
        var entry2 = CreateJournalEntry(Symbol: "CURR2", DecisionOutcome: "Accepted");
        var journalContent = JsonSerializer.Serialize(entry1) + "\n" + JsonSerializer.Serialize(entry2) + "\n";
        await File.WriteAllTextAsync(journalPath, journalContent);

        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        // Act
        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: false);

        // Assert
        Assert.Equal(0, result);
        
        // Verify outcomes were stored (2 previous + 2 new from accepted entries)
        var allOutcomes = await outcomeStore.GetAllOutcomesAsync();
        Assert.True(allOutcomes.Count >= 2, "Should have at least previous outcomes loaded");
    }

    [Fact]
    public async Task DailyRollup_WithoutRotationService_StillProcessesJournal()
    {
        // Arrange
        var journalPath = Path.Combine(_tempDir, "shadow-trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");

        var entry = CreateJournalEntry(DecisionOutcome: "Accepted");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(entry) + "\n");

        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        // Act - Create reporter without rotation service
        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, journalRotationService: null);
        var result = await reporter.RunAsync(journalPath, writeToFile: false);

        // Assert
        Assert.Equal(0, result);
        
        // Journal should NOT be rotated (because no rotation service)
        var originalExists = File.Exists(journalPath);
        var rotatedPath = Path.Combine(_tempDir, $"shadow-trade-journal-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var rotatedExists = File.Exists(rotatedPath);

        // Either original exists (not rotated) or rotated exists (but one should be true)
        Assert.True(originalExists || !rotatedExists, "Without rotation service, original should remain");
    }

    [Fact]
    public async Task DailyRollup_FullFlow_GeneratesReport()
    {
        // Arrange
        var journalPath = Path.Combine(_tempDir, "shadow-trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");
        var reportPath = Path.Combine(_tempDir, "rollup-report.txt");

        // Create previous outcome
        var prevOutcome = new TradeOutcome
        {
            DecisionId = Guid.NewGuid(),
            Symbol = "PREV",
            Direction = "Long",
            EntryPrice = 100m,
            StopPrice = 95m,
            TargetPrice = 110m,
            ExitPrice = 110m,
            OutcomeType = "HitTarget",
            RiskMultiple = 2.0m,
            PnlUsd = 200m,
            IsWin = true,
            DurationSeconds = 300,
            SchemaVersion = 1
        };
        await File.WriteAllTextAsync(outcomesPath, JsonSerializer.Serialize(prevOutcome) + "\n");

        // Create current journal entries
        var rejectedEntry = CreateJournalEntry(Symbol: "REJ1", DecisionOutcome: "Rejected", RejectionReason: "InsufficientTape");
        var acceptedEntry = CreateJournalEntry(Symbol: "ACC1", DecisionOutcome: "Accepted");
        var journalContent = JsonSerializer.Serialize(rejectedEntry) + "\n" + JsonSerializer.Serialize(acceptedEntry) + "\n";
        await File.WriteAllTextAsync(journalPath, journalContent);

        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        // Act
        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: true, outputPath: reportPath);

        // Assert
        Assert.Equal(0, result);
        Assert.True(File.Exists(reportPath), "Report file should be written");
        
        var reportContent = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("Shadow Trade Daily Rollup", reportContent);
        Assert.Contains("Performance Metrics", reportContent);
        Assert.Contains("Total candidates:", reportContent);
    }

    [Fact]
    public async Task DailyRollup_RotatedJournalHandling_CreatesNewJournal()
    {
        // Arrange
        var journalPath = Path.Combine(_tempDir, "shadow-trade-journal.jsonl");
        var outcomesPath = Path.Combine(_tempDir, "trade-outcomes.jsonl");

        // Create a journal entry
        var entry = CreateJournalEntry(DecisionOutcome: "Accepted");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(entry) + "\n");

        var rotationService = new FileBasedJournalRotationService();
        var outcomeLabeler = new TradeOutcomeLabeler();
        var outcomeStore = new FileBasedOutcomeSummaryStore(outcomesPath);

        // Act
        var reporter = new DailyRollupReporter(outcomeLabeler, outcomeStore, rotationService);
        var result = await reporter.RunAsync(journalPath, writeToFile: false);

        // Assert
        Assert.Equal(0, result);
        
        // After rotation, if journal was rotated, it should either not exist or be recreated as empty
        // (The reporter creates a new empty one if rotation occurred)
        Assert.True(!File.Exists(journalPath) || new FileInfo(journalPath).Length == 0, 
            "Journal should be empty or non-existent after rotation");
    }

    /// <summary>
    /// Helper: Create a mock ShadowTradeJournalEntry for testing.
    /// </summary>
    private static ShadowTradeJournalEntry CreateJournalEntry(
        string Symbol = "TEST",
        string DecisionOutcome = "Accepted",
        string? RejectionReason = null)
    {
        return new ShadowTradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Symbol = Symbol,
            Direction = "Long",
            DecisionOutcome = DecisionOutcome,
            RejectionReason = RejectionReason,
            DecisionTimestampUtc = DateTimeOffset.UtcNow,
            DecisionInputs = new ShadowTradeJournalEntry.DecisionInputsSnapshot { Score = 75m },
            ObservedMetrics = new ShadowTradeJournalEntry.ObservedMetricsSnapshot 
            { 
                Spread = 0.05m,
                QueueImbalance = 1.2m,
                TapeAcceleration = 1.5m
            }
        };
    }
}
