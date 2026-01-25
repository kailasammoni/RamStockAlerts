using System.Text.Json;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Unit tests for TradeOutcomeLabeler and OutcomeSummaryStore.
/// Tests cover: HitTarget, HitStop, NoHit, R-multiple calculation, P&L, win flag.
/// </summary>
public class OutcomeLabelingTests
{
    private readonly ITradeOutcomeLabeler _labeler = new TradeOutcomeLabeler();

    [Fact]
    public async Task LabelOutcome_LongTrade_HitTarget_IsWin()
    {
        // Arrange
        var entry = CreateJournalEntry(
            symbol: "AAPL",
            direction: "Long",
            entry: 100m,
            stop: 95m,
            target: 110m);

        var exitPrice = 115m; // Above target
        var exitTime = entry.DecisionTimestampUtc?.AddSeconds(60);

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, exitPrice, exitTime);

        // Assert
        Assert.NotNull(outcome);
        Assert.Equal("HitTarget", outcome.OutcomeType);
        Assert.True(outcome.IsWin);
        Assert.Equal(115m, outcome.ExitPrice);
        Assert.Equal(15m, outcome.PnlUsd); // 115 - 100
        Assert.Equal(3m, outcome.RiskMultiple); // (115 - 100) / (100 - 95) = 15 / 5 = 3
    }

    [Fact]
    public async Task LabelOutcome_LongTrade_HitStop_IsLoss()
    {
        // Arrange
        var entry = CreateJournalEntry(
            symbol: "AAPL",
            direction: "Long",
            entry: 100m,
            stop: 95m,
            target: 110m);

        var exitPrice = 94m; // Below stop
        var exitTime = entry.DecisionTimestampUtc?.AddSeconds(30);

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, exitPrice, exitTime);

        // Assert
        Assert.NotNull(outcome);
        Assert.Equal("HitStop", outcome.OutcomeType);
        Assert.False(outcome.IsWin);
        Assert.Equal(94m, outcome.ExitPrice);
        Assert.Equal(-6m, outcome.PnlUsd); // 94 - 100
        Assert.Equal(-1.2m, outcome.RiskMultiple); // (94 - 100) / (100 - 95) = -6 / 5 = -1.2
    }

    [Fact]
    public async Task LabelOutcome_LongTrade_NoHit_PartialProfit()
    {
        // Arrange
        var entry = CreateJournalEntry(
            symbol: "TSLA",
            direction: "Long",
            entry: 200m,
            stop: 190m,
            target: 220m);

        var exitPrice = 205m; // Between stop and target
        var exitTime = entry.DecisionTimestampUtc?.AddSeconds(120);

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, exitPrice, exitTime);

        // Assert
        Assert.NotNull(outcome);
        Assert.Equal("NoHit", outcome.OutcomeType);
        Assert.True(outcome.IsWin);
        Assert.Equal(5m, outcome.PnlUsd); // 205 - 200
        Assert.Equal(0.5m, outcome.RiskMultiple); // (205 - 200) / (200 - 190) = 5 / 10 = 0.5
    }

    [Fact]
    public async Task LabelOutcome_ShortTrade_HitTarget()
    {
        // Arrange
        var entry = CreateJournalEntry(
            symbol: "SPY",
            direction: "Short",
            entry: 450m,
            stop: 460m,
            target: 440m);

        var exitPrice = 435m; // Below target (profitable for short)
        var exitTime = entry.DecisionTimestampUtc?.AddSeconds(45);

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, exitPrice, exitTime);

        // Assert
        Assert.NotNull(outcome);
        Assert.Equal("HitTarget", outcome.OutcomeType);
        Assert.True(outcome.IsWin);
        Assert.Equal(-15m, outcome.PnlUsd); // 435 - 450 (short profit is negative)
        // For short: moveRange = 435 - 450 = -15, riskRange = 10, R = -15/10 = -1.5
        // The sign indicates profit in the short direction
        Assert.Equal(-1.5m, outcome.RiskMultiple);
    }

    [Fact]
    public async Task LabelOutcome_ShortTrade_HitStop()
    {
        // Arrange
        var entry = CreateJournalEntry(
            symbol: "QQQ",
            direction: "Short",
            entry: 350m,
            stop: 360m,
            target: 340m);

        var exitPrice = 362m; // Above stop (loss for short)
        var exitTime = entry.DecisionTimestampUtc?.AddSeconds(20);

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, exitPrice, exitTime);

        // Assert
        Assert.NotNull(outcome);
        Assert.Equal("HitStop", outcome.OutcomeType);
        Assert.False(outcome.IsWin);
        Assert.Equal(12m, outcome.PnlUsd); // 362 - 350 (loss for short)
        // For short: moveRange = 362 - 350 = 12, riskRange = 10, R = 12/10 = 1.2
        // The sign indicates loss in the short direction
        Assert.Equal(1.2m, outcome.RiskMultiple);
    }

    [Fact]
    public async Task LabelOutcome_NoExitPrice_OutcomeTypeNoExit()
    {
        // Arrange
        var entry = CreateJournalEntry(
            symbol: "MSFT",
            direction: "Long",
            entry: 300m,
            stop: 290m,
            target: 310m);

        var exitPrice = (decimal?)null; // No exit
        var exitTime = (DateTimeOffset?)null;

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, exitPrice, exitTime);

        // Assert
        Assert.NotNull(outcome);
        Assert.Equal("NoExit", outcome.OutcomeType);
        Assert.Null(outcome.IsWin);
        Assert.Null(outcome.PnlUsd);
        Assert.Null(outcome.RiskMultiple);
    }

    [Fact]
    public async Task LabelOutcome_CalculatesDuration()
    {
        // Arrange
        var entryTime = DateTimeOffset.UtcNow;
        var exitTime = entryTime.AddSeconds(300); // 5 minutes

        var entry = CreateJournalEntry(
            symbol: "NVDA",
            direction: "Long",
            entry: 500m,
            stop: 490m,
            target: 520m);
        entry.DecisionTimestampUtc = entryTime;

        // Act
        var outcome = await _labeler.LabelOutcomeAsync(entry, 505m, exitTime);

        // Assert
        Assert.Equal(300, outcome.DurationSeconds);
    }

    [Fact]
    public async Task LabelOutcomes_BatchProcessing()
    {
        // Arrange
        var entries = new List<TradeJournalEntry>
        {
            CreateJournalEntry("AAPL", "Long", 100m, 95m, 110m),
            CreateJournalEntry("TSLA", "Long", 200m, 190m, 220m),
            CreateJournalEntry("SPY", "Short", 450m, 460m, 440m)
        };

        var exitData = new Dictionary<Guid, (decimal, DateTimeOffset)>
        {
            { entries[0].DecisionId, (115m, DateTimeOffset.UtcNow.AddSeconds(60)) },
            { entries[1].DecisionId, (205m, DateTimeOffset.UtcNow.AddSeconds(120)) },
            { entries[2].DecisionId, (435m, DateTimeOffset.UtcNow.AddSeconds(45)) }
        };

        // Act
        var outcomes = await _labeler.LabelOutcomesAsync(entries, exitData);

        // Assert
        Assert.Equal(3, outcomes.Count);
        Assert.Equal("HitTarget", outcomes[0].OutcomeType);
        Assert.Equal("NoHit", outcomes[1].OutcomeType);
        Assert.Equal("HitTarget", outcomes[2].OutcomeType);
    }

    [Fact]
    public async Task LabelOutcomes_PartialExitData()
    {
        // Arrange
        var entries = new List<TradeJournalEntry>
        {
            CreateJournalEntry("AAPL", "Long", 100m, 95m, 110m),
            CreateJournalEntry("TSLA", "Long", 200m, 190m, 220m)
        };

        var exitData = new Dictionary<Guid, (decimal, DateTimeOffset)>
        {
            { entries[0].DecisionId, (115m, DateTimeOffset.UtcNow.AddSeconds(60)) }
            // entries[1] has no exit data
        };

        // Act
        var outcomes = await _labeler.LabelOutcomesAsync(entries, exitData);

        // Assert
        Assert.Equal(2, outcomes.Count);
        Assert.Equal("HitTarget", outcomes[0].OutcomeType);
        Assert.Equal("NoExit", outcomes[1].OutcomeType);
    }

    private static TradeJournalEntry CreateJournalEntry(
        string symbol,
        string direction,
        decimal entry,
        decimal stop,
        decimal target)
    {
        return new TradeJournalEntry
        {
            DecisionId = Guid.NewGuid(),
            Symbol = symbol,
            Direction = direction,
            DecisionTimestampUtc = DateTimeOffset.UtcNow,
            Blueprint = new TradeJournalEntry.BlueprintPlan
            {
                Entry = entry,
                Stop = stop,
                Target = target
            }
        };
    }
}

/// <summary>
/// Unit tests for FileBasedOutcomeSummaryStore.
/// Tests cover: append, read, date filtering, and JSONL format.
/// </summary>
public class OutcomeSummaryStoreTests : IDisposable
{
    private readonly string _tempFile;
    private readonly FileBasedOutcomeSummaryStore _store;

    public OutcomeSummaryStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"test-outcomes-{Guid.NewGuid()}.jsonl");
        _store = new FileBasedOutcomeSummaryStore(_tempFile);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public async Task AppendOutcome_WritesToFile()
    {
        // Arrange
        var outcome = CreateOutcome(symbol: "AAPL", isWin: true);

        // Act
        await _store.AppendOutcomeAsync(outcome);

        // Assert
        Assert.True(File.Exists(_tempFile));
        var lines = File.ReadAllLines(_tempFile);
        Assert.Single(lines);

        var parsed = JsonSerializer.Deserialize<TradeOutcome>(lines[0]);
        Assert.NotNull(parsed);
        Assert.Equal("AAPL", parsed.Symbol);
    }

    [Fact]
    public async Task AppendOutcomes_WritesMultipleLines()
    {
        // Arrange
        var outcomes = new List<TradeOutcome>
        {
            CreateOutcome("AAPL", true),
            CreateOutcome("TSLA", false),
            CreateOutcome("SPY", true)
        };

        // Act
        await _store.AppendOutcomesAsync(outcomes);

        // Assert
        var lines = File.ReadAllLines(_tempFile);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task GetAllOutcomes_ReadsAllLines()
    {
        // Arrange
        var outcomes = new List<TradeOutcome>
        {
            CreateOutcome("AAPL", true),
            CreateOutcome("TSLA", false)
        };
        await _store.AppendOutcomesAsync(outcomes);

        // Act
        var loaded = await _store.GetAllOutcomesAsync();

        // Assert
        Assert.Equal(2, loaded.Count);
        Assert.Equal("AAPL", loaded[0].Symbol);
        Assert.Equal("TSLA", loaded[1].Symbol);
    }

    [Fact]
    public async Task GetOutcomesByDate_FiltersCorrectly()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var yesterday = today.AddDays(-1);

        var todayOutcome = CreateOutcome("AAPL", true);
        todayOutcome.OutcomeLabeledUtc = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0, 0)), TimeSpan.Zero);

        var yesterdayOutcome = CreateOutcome("TSLA", false);
        yesterdayOutcome.OutcomeLabeledUtc = new DateTimeOffset(yesterday.ToDateTime(new TimeOnly(12, 0, 0)), TimeSpan.Zero);

        await _store.AppendOutcomesAsync(new List<TradeOutcome> { todayOutcome, yesterdayOutcome });

        // Act
        var todayOutcomes = await _store.GetOutcomesByDateAsync(today);
        var yesterdayOutcomes = await _store.GetOutcomesByDateAsync(yesterday);

        // Assert
        Assert.Single(todayOutcomes);
        Assert.Single(yesterdayOutcomes);
        Assert.Equal("AAPL", todayOutcomes[0].Symbol);
        Assert.Equal("TSLA", yesterdayOutcomes[0].Symbol);
    }

    [Fact]
    public async Task GetOutcomesByDateRange_FiltersCorrectly()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = today.AddDays(-2);
        var endDate = today;

        var outcome1 = CreateOutcome("AAPL", true);
        outcome1.OutcomeLabeledUtc = new DateTimeOffset(today.AddDays(-2).ToDateTime(new TimeOnly(12, 0, 0)), TimeSpan.Zero);

        var outcome2 = CreateOutcome("TSLA", false);
        outcome2.OutcomeLabeledUtc = new DateTimeOffset(today.AddDays(-1).ToDateTime(new TimeOnly(12, 0, 0)), TimeSpan.Zero);

        var outcome3 = CreateOutcome("SPY", true);
        outcome3.OutcomeLabeledUtc = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0, 0)), TimeSpan.Zero);

        var outOfRangeOutcome = CreateOutcome("QQQ", false);
        outOfRangeOutcome.OutcomeLabeledUtc = new DateTimeOffset(today.AddDays(-3).ToDateTime(new TimeOnly(12, 0, 0)), TimeSpan.Zero);

        await _store.AppendOutcomesAsync(new List<TradeOutcome> { outcome1, outcome2, outcome3, outOfRangeOutcome });

        // Act
        var inRange = await _store.GetOutcomesByDateRangeAsync(startDate, endDate);

        // Assert
        Assert.Equal(3, inRange.Count); // outcome1, 2, 3
        Assert.DoesNotContain(inRange, o => o.Symbol == "QQQ");
    }

    [Fact]
    public async Task GetAllOutcomes_EmptyFile_ReturnsEmpty()
    {
        // Act
        var outcomes = await _store.GetAllOutcomesAsync();

        // Assert
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task GetAllOutcomes_SkipsBlankLines()
    {
        // Arrange
        var content = new[]
        {
            JsonSerializer.Serialize(CreateOutcome("AAPL", true)),
            string.Empty,
            JsonSerializer.Serialize(CreateOutcome("TSLA", false)),
            "   ",
            JsonSerializer.Serialize(CreateOutcome("SPY", true))
        };
        File.WriteAllLines(_tempFile, content);

        // Act
        var outcomes = await _store.GetAllOutcomesAsync();

        // Assert
        Assert.Equal(3, outcomes.Count);
    }

    private static TradeOutcome CreateOutcome(string symbol, bool isWin)
    {
        return new TradeOutcome
        {
            DecisionId = Guid.NewGuid(),
            Symbol = symbol,
            Direction = "Long",
            EntryPrice = 100m,
            StopPrice = 95m,
            TargetPrice = 110m,
            ExitPrice = isWin ? 110m : 94m,
            OutcomeLabeledUtc = DateTimeOffset.UtcNow,
            OutcomeType = isWin ? "HitTarget" : "HitStop",
            PnlUsd = isWin ? 10m : -6m,
            RiskMultiple = isWin ? 2m : -1.2m,
            IsWin = isWin
        };
    }
}

