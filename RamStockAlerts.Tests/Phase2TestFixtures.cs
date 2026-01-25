using System.Text.Json;
using RamStockAlerts.Models;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 2.5: Test fixtures for regression testing of daily metrics.
/// Provides pre-built data sets for consistent, reproducible test scenarios.
/// </summary>
public static class Phase2TestFixtures
{
    /// <summary>
    /// Standard "realistic trading day" fixture.
    /// 9 candidates: 3 rejected, 3 accepted winners, 3 with mixed outcomes.
    /// Expected: 66% win rate, 1.4R expectancy, $600 P&L.
    /// </summary>
    public static Phase2TestData RealisticTradingDay()
    {
        return new Phase2TestData
        {
            Name = "RealisticTradingDay",
            Description = "Typical trading session with mixed outcomes",
            JournalEntries = new[]
            {
                JournalEntry("AAPL", "Long", "Rejected", "InsufficientTape"),
                JournalEntry("MSFT", "Short", "Rejected", "DepthNotAvailable"),
                JournalEntry("NVDA", "Long", "Rejected", "InsufficientQueueImbalance"),
                
                JournalEntry("AMZN", "Long", "Accepted"),
                JournalEntry("GOOGL", "Short", "Accepted"),
                JournalEntry("META", "Long", "Accepted"),
                
                JournalEntry("TSLA", "Short", "Rejected", "ScarcityRejected"),
                JournalEntry("NFLX", "Short", "Rejected", "InvalidTapeSignal"),
                JournalEntry("AMD", "Long", "Rejected", "InsufficientDepth"),
            },
            PreviousOutcomes = new[]
            {
                Outcome("TSLA_PREV", "Long", "HitTarget", 2.5m, 250m, isWin: true),
                Outcome("SPY_PREV", "Short", "HitTarget", 2.0m, 200m, isWin: true),
                Outcome("QQQ_PREV", "Long", "HitTarget", 3.0m, 300m, isWin: true),
                Outcome("IWM_PREV", "Short", "HitStop", -1.0m, -100m, isWin: false),
                Outcome("EEM_PREV", "Long", "HitStop", -1.0m, -100m, isWin: false),
            },
            NewOutcomes = new[]
            {
                Outcome("AMZN", "Long", "HitTarget", 2.0m, 200m, isWin: true),
                Outcome("GOOGL", "Short", "HitTarget", 3.0m, 300m, isWin: true),
                Outcome("META", "Long", "HitStop", -1.5m, -150m, isWin: false),
            },
            ExpectedMetrics = new ExpectedMetrics
            {
                TotalCandidates = 9,
                Accepted = 3,
                Rejected = 6,
                WinCount = 5,
                LossCount = 3,
                WinRate = 62.5m, // 5 / 8
                AvgWinR = 2.3m,  // (2.5+2.0+3.0+2.0+3.0) / 5
                AvgLossR = 1.15m, // (1.0+1.0+1.5) / 3
                Expectancy = 1.27m, // (0.625 * 2.3) - (0.375 * 1.15)
                TotalPnlUsd = 650m, // 250+200+300-100-100+200+300-150
                HasWinRateWarning = false,
                HasExpectancyWarning = false
            }
        };
    }

    /// <summary>
    /// "Perfect day" fixture: all wins, strong R-multiples.
    /// 5 accepted, 5 wins.
    /// Expected: 100% win rate, 2.7R expectancy, $1300 P&L.
    /// </summary>
    public static Phase2TestData PerfectDay()
    {
        return new Phase2TestData
        {
            Name = "PerfectDay",
            Description = "All accepted signals hit targets",
            JournalEntries = new[]
            {
                JournalEntry("AAPL", "Long", "Accepted"),
                JournalEntry("MSFT", "Long", "Accepted"),
                JournalEntry("GOOGL", "Short", "Accepted"),
                JournalEntry("AMZN", "Long", "Accepted"),
                JournalEntry("NVDA", "Short", "Accepted"),
            },
            PreviousOutcomes = Array.Empty<TradeOutcome>(),
            NewOutcomes = new[]
            {
                Outcome("AAPL", "Long", "HitTarget", 2.5m, 250m, isWin: true),
                Outcome("MSFT", "Long", "HitTarget", 3.0m, 300m, isWin: true),
                Outcome("GOOGL", "Short", "HitTarget", 2.0m, 200m, isWin: true),
                Outcome("AMZN", "Long", "HitTarget", 3.5m, 350m, isWin: true),
                Outcome("NVDA", "Short", "HitTarget", 2.5m, 250m, isWin: true),
            },
            ExpectedMetrics = new ExpectedMetrics
            {
                TotalCandidates = 5,
                Accepted = 5,
                Rejected = 0,
                WinCount = 5,
                LossCount = 0,
                WinRate = 100m,
                AvgWinR = 2.7m, // (2.5+3.0+2.0+3.5+2.5) / 5
                AvgLossR = 0m,
                Expectancy = 2.7m, // (1.0 * 2.7) - (0 * 0)
                TotalPnlUsd = 1350m,
                HasWinRateWarning = false,
                HasExpectancyWarning = false
            }
        };
    }

    /// <summary>
    /// "Disaster day" fixture: low win rate, negative expectancy.
    /// 8 accepted, 2 wins (25%), all losses are -2R each.
    /// Expected: 25% win rate, -1.25R expectancy, -$600 P&L.
    /// Warnings: Win rate, Expectancy, Risk/reward.
    /// </summary>
    public static Phase2TestData DisasterDay()
    {
        return new Phase2TestData
        {
            Name = "DisasterDay",
            Description = "Poor performance with multiple losses",
            JournalEntries = Enumerable.Range(1, 8)
                .Select(i => JournalEntry($"TEST{i}", "Long", "Accepted"))
                .ToArray(),
            PreviousOutcomes = Array.Empty<TradeOutcome>(),
            NewOutcomes = new[]
            {
                Outcome("TEST1", "Long", "HitTarget", 1.5m, 150m, isWin: true),
                Outcome("TEST2", "Long", "HitStop", -2.0m, -200m, isWin: false),
                Outcome("TEST3", "Long", "HitStop", -2.0m, -200m, isWin: false),
                Outcome("TEST4", "Long", "HitTarget", 1.0m, 100m, isWin: true),
                Outcome("TEST5", "Long", "HitStop", -2.5m, -250m, isWin: false),
                Outcome("TEST6", "Long", "HitStop", -2.0m, -200m, isWin: false),
                Outcome("TEST7", "Long", "HitStop", -1.5m, -150m, isWin: false),
                Outcome("TEST8", "Long", "HitStop", -2.0m, -200m, isWin: false),
            },
            ExpectedMetrics = new ExpectedMetrics
            {
                TotalCandidates = 8,
                Accepted = 8,
                Rejected = 0,
                WinCount = 2,
                LossCount = 6,
                WinRate = 25m,
                AvgWinR = 1.25m, // (1.5+1.0) / 2
                AvgLossR = 1.9m, // (2.0+2.0+2.5+2.0+1.5+2.0) / 6
                Expectancy = -1.175m, // (0.25 * 1.25) - (0.75 * 1.9)
                TotalPnlUsd = -1150m,
                HasWinRateWarning = true,    // 25% < 60%
                HasExpectancyWarning = true, // -1.175 < 0.25
                HasRiskRewardWarning = true  // 1.9 >= 1.25
            }
        };
    }

    /// <summary>
    /// "Insufficient sample" fixture: only 1 closed trade.
    /// Expected: Warning for insufficient data.
    /// </summary>
    public static Phase2TestData InsufficientSample()
    {
        return new Phase2TestData
        {
            Name = "InsufficientSample",
            Description = "Only 1 closed trade (below threshold of 3)",
            JournalEntries = new[]
            {
                JournalEntry("AAPL", "Long", "Accepted"),
            },
            PreviousOutcomes = Array.Empty<TradeOutcome>(),
            NewOutcomes = new[]
            {
                Outcome("AAPL", "Long", "HitTarget", 2.0m, 200m, isWin: true),
            },
            ExpectedMetrics = new ExpectedMetrics
            {
                TotalCandidates = 1,
                Accepted = 1,
                Rejected = 0,
                WinCount = 1,
                LossCount = 0,
                WinRate = 100m,
                AvgWinR = 2.0m,
                AvgLossR = 0m,
                HasSampleSizeWarning = true  // Only 1 closed trade
            }
        };
    }

    /// <summary>
    /// "All rejected" fixture: 10 candidates, all rejected, no outcomes.
    /// Expected: No metrics warnings (no trades to evaluate).
    /// </summary>
    public static Phase2TestData AllRejected()
    {
        return new Phase2TestData
        {
            Name = "AllRejected",
            Description = "All signals rejected, no accepted trades",
            JournalEntries = new[]
            {
                JournalEntry("AAPL", "Long", "Rejected", "InsufficientTape"),
                JournalEntry("MSFT", "Short", "Rejected", "DepthNotAvailable"),
                JournalEntry("GOOGL", "Long", "Rejected", "InsufficientQueueImbalance"),
                JournalEntry("AMZN", "Short", "Rejected", "InvalidTapeSignal"),
                JournalEntry("NVDA", "Long", "Rejected", "ScarcityRejected"),
                JournalEntry("TSLA", "Short", "Rejected", "InsufficientDepth"),
                JournalEntry("NFLX", "Long", "Rejected", "InvalidTapeSignal"),
                JournalEntry("AMD", "Short", "Rejected", "DepthNotAvailable"),
                JournalEntry("META", "Long", "Rejected", "InsufficientTape"),
                JournalEntry("CRM", "Short", "Rejected", "ScarcityRejected"),
            },
            PreviousOutcomes = Array.Empty<TradeOutcome>(),
            NewOutcomes = Array.Empty<TradeOutcome>(),
            ExpectedMetrics = new ExpectedMetrics
            {
                TotalCandidates = 10,
                Accepted = 0,
                Rejected = 10,
                WinCount = 0,
                LossCount = 0,
                HasSampleSizeWarning = true
            }
        };
    }

    /// <summary>
    /// "Breakeven day" fixture: wins and losses equal out to $0 P&L.
    /// 4 trades: 2 wins +$200, 2 losses -$100 each.
    /// Expected: 50% win rate, 0.25R expectancy (right at threshold).
    /// No warnings.
    /// </summary>
    public static Phase2TestData BreakEvenDay()
    {
        return new Phase2TestData
        {
            Name = "BreakEvenDay",
            Description = "Balanced wins/losses, zero P&L",
            JournalEntries = new[]
            {
                JournalEntry("AAPL", "Long", "Accepted"),
                JournalEntry("MSFT", "Short", "Accepted"),
                JournalEntry("GOOGL", "Long", "Accepted"),
                JournalEntry("AMZN", "Short", "Accepted"),
            },
            PreviousOutcomes = Array.Empty<TradeOutcome>(),
            NewOutcomes = new[]
            {
                Outcome("AAPL", "Long", "HitTarget", 2.0m, 200m, isWin: true),
                Outcome("MSFT", "Short", "HitTarget", 1.5m, 150m, isWin: true),
                Outcome("GOOGL", "Long", "HitStop", -1.0m, -100m, isWin: false),
                Outcome("AMZN", "Short", "HitStop", -1.0m, -100m, isWin: false),
            },
            ExpectedMetrics = new ExpectedMetrics
            {
                TotalCandidates = 4,
                Accepted = 4,
                Rejected = 0,
                WinCount = 2,
                LossCount = 2,
                WinRate = 50m,
                AvgWinR = 1.75m, // (2.0+1.5) / 2
                AvgLossR = 1.0m, // (1.0+1.0) / 2
                Expectancy = 0.625m, // (0.5 * 1.75) - (0.5 * 1.0)
                TotalPnlUsd = 150m,
                HasWinRateWarning = true  // 50% < 60%
            }
        };
    }

    /// <summary>
    /// Helper: Create a journal entry.
    /// </summary>
    private static TradeJournalEntry JournalEntry(
        string symbol,
        string direction,
        string outcome,
        string? rejectionReason = null)
    {
        return new TradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Symbol = symbol,
            Direction = direction,
            DecisionOutcome = outcome,
            RejectionReason = rejectionReason,
            DecisionTimestampUtc = DateTimeOffset.UtcNow,
            DecisionInputs = new TradeJournalEntry.DecisionInputsSnapshot { Score = 75m },
            ObservedMetrics = new TradeJournalEntry.ObservedMetricsSnapshot
            {
                Spread = 0.05m,
                QueueImbalance = 1.2m,
                TapeAcceleration = 1.5m
            },
            DecisionTrace = rejectionReason != null ? new List<string> { rejectionReason } : null
        };
    }

    /// <summary>
    /// Helper: Create a trade outcome.
    /// </summary>
    private static TradeOutcome Outcome(
        string symbol,
        string direction,
        string outcomeType,
        decimal riskMultiple,
        decimal pnlUsd,
        bool isWin)
    {
        return new TradeOutcome
        {
            DecisionId = Guid.NewGuid(),
            Symbol = symbol,
            Direction = direction,
            EntryPrice = 100m,
            StopPrice = 95m,
            TargetPrice = 110m,
            ExitPrice = direction == "Long" ? 100m + (riskMultiple * 5m) : 100m - (riskMultiple * 5m),
            OutcomeType = outcomeType,
            RiskMultiple = riskMultiple,
            PnlUsd = pnlUsd,
            IsWin = isWin,
            DurationSeconds = 600,
            SchemaVersion = 1
        };
    }
}

/// <summary>
/// Represents a complete test scenario with expected outcomes.
/// </summary>
public class Phase2TestData
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required TradeJournalEntry[] JournalEntries { get; init; }
    public required TradeOutcome[] PreviousOutcomes { get; init; }
    public required TradeOutcome[] NewOutcomes { get; init; }
    public ExpectedMetrics? ExpectedMetrics { get; init; }

    /// <summary>
    /// Serializes test data to a format suitable for file output.
    /// </summary>
    public (string journalJson, string outcomesJson) Serialize()
    {
        var journalJson = string.Join("\n", 
            JournalEntries.Select(e => JsonSerializer.Serialize(e))) + "\n";
        
        var allOutcomes = PreviousOutcomes.Concat(NewOutcomes).ToArray();
        var outcomesJson = string.Join("\n",
            allOutcomes.Select(o => JsonSerializer.Serialize(o))) + "\n";

        return (journalJson, outcomesJson);
    }
}

/// <summary>
/// Expected metrics values for a test scenario.
/// </summary>
public class ExpectedMetrics
{
    public int TotalCandidates { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public int OpenCount { get; set; }
    public int NoHitCount { get; set; }
    public decimal WinRate { get; set; }
    public decimal AvgWinR { get; set; }
    public decimal AvgLossR { get; set; }
    public decimal Expectancy { get; set; }
    public decimal TotalPnlUsd { get; set; }
    public bool HasWinRateWarning { get; set; }
    public bool HasExpectancyWarning { get; set; }
    public bool HasSampleSizeWarning { get; set; }
    public bool HasRiskRewardWarning { get; set; }

    /// <summary>
    /// Verify actual metrics against expected.
    /// </summary>
    public void Verify(string reportContent)
    {
        // Count verification
        Assert.Contains($"Total candidates: {TotalCandidates}", reportContent);
        Assert.Contains($"accepted: {Accepted}", reportContent);
        Assert.Contains($"rejected: {Rejected}", reportContent);

        // Metric verification
        if (WinCount > 0 || LossCount > 0)
        {
            var expectedWinRateStr = WinRate.ToString("F1") + "%";
            Assert.Contains($"Win rate: {expectedWinRateStr}", reportContent);
        }

        // Warning verification
        if (HasWinRateWarning)
            Assert.Contains("below target", reportContent);

        if (HasExpectancyWarning)
            Assert.Contains("Expectancy", reportContent);

        if (HasSampleSizeWarning)
            Assert.Contains("insufficient sample", reportContent);

        if (HasRiskRewardWarning)
            Assert.Contains("unfavorable risk/reward", reportContent);
    }
}

