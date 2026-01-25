using System.Text.Json;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 1.4 validation test: Load historical trade journal and validate outcomes
/// are correctly labeled on real data.
/// </summary>
public class Phase1ValidationTests
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact(Skip = "Requires historical journal data")]
    public async Task ValidateOutcomeLabelingOnHistoricalData()
    {
        // Arrange
        var journalPath = "logs/trade-journal.jsonl";
        if (!File.Exists(journalPath))
        {
            throw new FileNotFoundException($"Historical journal not found: {journalPath}");
        }

        var acceptedEntries = new List<TradeJournalEntry>();
        var totalLines = 0;

        // Load journal and extract accepted entries
        using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                totalLines++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<TradeJournalEntry>(line, _jsonOptions);
                    if (entry?.DecisionOutcome?.Equals("Accepted", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        acceptedEntries.Add(entry);
                    }
                }
                catch
                {
                    // Skip malformed entries
                }
            }
        }

        Assert.True(totalLines > 0, "Journal file is empty");
        Assert.True(acceptedEntries.Count > 0, "No accepted entries found in journal");

        // Act - Label outcomes
        var labeler = new TradeOutcomeLabeler();
        var outcomes = await labeler.LabelOutcomesAsync(acceptedEntries);

        // Assert
        Assert.Equal(acceptedEntries.Count, outcomes.Count);

        // Verify all outcomes have required fields
        foreach (var outcome in outcomes)
        {
            Assert.NotNull(outcome.Symbol);
            Assert.NotNull(outcome.Direction);
            Assert.NotNull(outcome.OutcomeType);
            Assert.True(outcome.SchemaVersion > 0);
        }

        // Show statistics
        var hitTargets = outcomes.Count(o => o.OutcomeType == "HitTarget");
        var hitStops = outcomes.Count(o => o.OutcomeType == "HitStop");
        var noHit = outcomes.Count(o => o.OutcomeType == "NoHit");
        var noExit = outcomes.Count(o => o.OutcomeType == "NoExit");

        // At least some outcomes should have meaningful labels
        Assert.True(hitTargets + hitStops + noHit > 0, "No meaningful outcomes found");
    }

    [Fact]
    public async Task ValidateOutcomeLabelingWithSampleData()
    {
        // Arrange - Create controlled test data
        var entries = new List<TradeJournalEntry>
        {
            // Winning long trade
            new TradeJournalEntry
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "AAPL",
                Direction = "Long",
                DecisionTimestampUtc = DateTimeOffset.UtcNow.AddHours(-1),
                Blueprint = new TradeJournalEntry.BlueprintPlan
                {
                    Entry = 100m,
                    Stop = 95m,
                    Target = 110m
                }
            },
            // Losing short trade
            new TradeJournalEntry
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "TSLA",
                Direction = "Short",
                DecisionTimestampUtc = DateTimeOffset.UtcNow.AddHours(-2),
                Blueprint = new TradeJournalEntry.BlueprintPlan
                {
                    Entry = 200m,
                    Stop = 210m,
                    Target = 190m
                }
            }
        };

        var labeler = new TradeOutcomeLabeler();

        // Act
        var outcomes = await labeler.LabelOutcomesAsync(entries);

        // Assert
        Assert.Equal(2, outcomes.Count);

        // Both should have NoExit (since we didn't provide exit data)
        Assert.All(outcomes, o => Assert.Equal("NoExit", o.OutcomeType));
        Assert.All(outcomes, o => Assert.Null(o.IsWin));
        Assert.All(outcomes, o => Assert.Null(o.PnlUsd));

        // Verify first trade
        Assert.Equal("AAPL", outcomes[0].Symbol);
        Assert.Equal("Long", outcomes[0].Direction);
        Assert.Equal(100m, outcomes[0].EntryPrice);

        // Verify second trade
        Assert.Equal("TSLA", outcomes[1].Symbol);
        Assert.Equal("Short", outcomes[1].Direction);
        Assert.Equal(200m, outcomes[1].EntryPrice);
    }

    [Fact]
    public async Task ValidateOutcomesAreStoredCorrectly()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"phase1-validation-{Guid.NewGuid()}.jsonl");
        var store = new FileBasedOutcomeSummaryStore(tempFile);

        var outcomes = new List<TradeOutcome>
        {
            new TradeOutcome
            {
                DecisionId = Guid.NewGuid(),
                Symbol = "AAPL",
                Direction = "Long",
                EntryPrice = 100m,
                ExitPrice = 110m,
                OutcomeLabeledUtc = DateTimeOffset.UtcNow,
                OutcomeType = "HitTarget",
                IsWin = true
            }
        };

        try
        {
            // Act
            await store.AppendOutcomesAsync(outcomes);

            // Assert
            Assert.True(File.Exists(tempFile));

            var loaded = await store.GetAllOutcomesAsync();
            Assert.Single(loaded);
            Assert.Equal("AAPL", loaded[0].Symbol);
            Assert.Equal("HitTarget", loaded[0].OutcomeType);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}


