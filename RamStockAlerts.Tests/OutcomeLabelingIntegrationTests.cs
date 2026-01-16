using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Integration tests for Phase 1 outcome labeling and DI wiring.
/// </summary>
public class OutcomeLabelingIntegrationTests
{
    [Fact]
    public async Task DailyRollupReporter_WithOutcomeLabeler_LabelsAcceptedSignals()
    {
        // Arrange
        var tempJournal = Path.Combine(Path.GetTempPath(), $"test-journal-{Guid.NewGuid()}.jsonl");
        var tempOutcomes = Path.Combine(Path.GetTempPath(), $"test-outcomes-{Guid.NewGuid()}.jsonl");

        try
        {
            // Create a test journal with an accepted entry
            var acceptedEntry = new ShadowTradeJournalEntry
            {
                SchemaVersion = 2,
                DecisionId = Guid.NewGuid(),
                Symbol = "AAPL",
                Direction = "Long",
                DecisionOutcome = "Accepted",
                DecisionTimestampUtc = DateTimeOffset.UtcNow,
                Blueprint = new ShadowTradeJournalEntry.BlueprintPlan
                {
                    Entry = 100m,
                    Stop = 95m,
                    Target = 110m
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(acceptedEntry);
            await File.WriteAllTextAsync(tempJournal, json + Environment.NewLine);

            // Create services
            var labeler = new TradeOutcomeLabeler();
            var store = new FileBasedOutcomeSummaryStore(tempOutcomes);
            var reporter = new DailyRollupReporter(labeler, store);

            // Act
            var result = await reporter.RunAsync(tempJournal, writeToFile: false);

            // Assert
            Assert.Equal(0, result);

            // Verify outcomes were stored
            var outcomes = await store.GetAllOutcomesAsync();
            Assert.Single(outcomes);
            Assert.Equal("AAPL", outcomes[0].Symbol);
            Assert.Equal("Long", outcomes[0].Direction);
            Assert.Equal("NoExit", outcomes[0].OutcomeType); // No exit provided
        }
        finally
        {
            if (File.Exists(tempJournal))
                File.Delete(tempJournal);
            if (File.Exists(tempOutcomes))
                File.Delete(tempOutcomes);
        }
    }

    [Fact]
    public async Task DailyRollupReporter_WithoutOutcomeLabeler_SkipsLabeling()
    {
        // Arrange
        var tempJournal = Path.Combine(Path.GetTempPath(), $"test-journal-{Guid.NewGuid()}.jsonl");

        try
        {
            var acceptedEntry = new ShadowTradeJournalEntry
            {
                SchemaVersion = 2,
                DecisionId = Guid.NewGuid(),
                Symbol = "AAPL",
                Direction = "Long",
                DecisionOutcome = "Accepted",
                DecisionTimestampUtc = DateTimeOffset.UtcNow,
                Blueprint = new ShadowTradeJournalEntry.BlueprintPlan
                {
                    Entry = 100m,
                    Stop = 95m,
                    Target = 110m
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(acceptedEntry);
            await File.WriteAllTextAsync(tempJournal, json + Environment.NewLine);

            // Create reporter without labeler
            var reporter = new DailyRollupReporter();

            // Act
            var result = await reporter.RunAsync(tempJournal, writeToFile: false);

            // Assert - should still succeed, just without labeling
            Assert.Equal(0, result);
        }
        finally
        {
            if (File.Exists(tempJournal))
                File.Delete(tempJournal);
        }
    }
}
