using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Models;
using RamStockAlerts.Services.Signals;

namespace RamStockAlerts.Tests.Helpers;

internal static class TradeJournalEntryTestHelper
{
    public static TradeJournalEntry BuildAcceptedEntry()
    {
        return new TradeJournalEntry
        {
            SchemaVersion = TradeJournal.CurrentSchemaVersion,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Source = "IBKR",
            EntryType = "Signal",
            MarketTimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            DecisionTimestampUtc = DateTimeOffset.UtcNow,
            TradingMode = "Signals",
            Symbol = "TEST",
            Direction = "BUY",
            DecisionOutcome = "Accepted",
            DecisionTrace = new List<string>
            {
                "ValidatorPass",
                "SpoofCheckPass",
                "ReplenishmentCheckPass",
                "AbsorptionCheckPass",
                "BlueprintPass",
                "ScarcityPass"
            },
            DataQualityFlags = new List<string>(),
            ObservedMetrics = new TradeJournalEntry.ObservedMetricsSnapshot
            {
                QueueImbalance = 3.1m,
                BidDepth4Level = 450m,
                AskDepth4Level = 120m,
                BidWallAgeMs = 1200,
                AskWallAgeMs = 900,
                BidAbsorptionRate = 12m,
                AskAbsorptionRate = 8m,
                SpoofScore = 0.2m,
                TapeAcceleration = 2.4m,
                TradesIn3Sec = 5,
                Spread = 0.02m,
                MidPrice = 100.11m,
                LastPrice = 100.12m,
                VwapPrice = 100.10m,
                BestBidPrice = 100.10m,
                BestBidSize = 500m,
                BestAskPrice = 100.12m,
                BestAskSize = 420m,
                TotalBidSizeTopN = 1500m,
                TotalAskSizeTopN = 800m,
                BidAskRatioTopN = 1.8m,
                TapeVelocity3Sec = 3m,
                TapeVolume3Sec = 120m,
                LastDepthUpdateAgeMs = 50,
                LastTapeUpdateAgeMs = 40,
                CumulativeVwap = 100.08m,
                PriceVsVwap = 0.04m,
                VwapReclaimDetected = true,
                DepthDelta = new TradeJournalEntry.DepthDeltaMetrics
                {
                    BidCancelToAddRatio1s = 0.8m,
                    AskCancelToAddRatio1s = 0.7m,
                    BidCancelToAddRatio3s = 1.1m,
                    AskCancelToAddRatio3s = 0.9m,
                    BidCancelCount1s = 3,
                    BidAddCount1s = 4,
                    AskCancelCount1s = 2,
                    AskAddCount1s = 3,
                    BidTotalCanceledSize1s = 20m,
                    AskTotalCanceledSize1s = 18m,
                    BidTotalAddedSize1s = 25m,
                    AskTotalAddedSize1s = 22m
                },
                BidsTopN = new List<TradeJournalEntry.DepthLevelSnapshot>
                {
                    new() { Level = 0, Price = 100.10m, Size = 500m }
                },
                AsksTopN = new List<TradeJournalEntry.DepthLevelSnapshot>
                {
                    new() { Level = 0, Price = 100.12m, Size = 420m }
                }
            },
            DecisionInputs = new TradeJournalEntry.DecisionInputsSnapshot
            {
                Score = 82m,
                VwapBonus = 0.5m,
                RankScore = 82.5m,
                TickerCooldownRemainingSec = 2.5,
                AlertsLastHourCount = 1,
                QueueImbalance = 3.1m,
                BidWallAgeMs = 1200,
                AskWallAgeMs = 900,
                BidAbsorptionRate = 12m,
                AskAbsorptionRate = 8m,
                SpoofScore = 0.2m,
                TapeAcceleration = 2.4m,
                TradesIn3Sec = 5,
                TapeVolume3Sec = 120m,
                Spread = 0.02m,
                BestBidPrice = 100.10m,
                BestAskPrice = 100.12m,
                VwapReclaimDetected = true,
                DepthDelta = new TradeJournalEntry.DepthDeltaMetrics
                {
                    BidCancelToAddRatio1s = 0.8m,
                    AskCancelToAddRatio1s = 0.7m,
                    BidCancelToAddRatio3s = 1.1m,
                    AskCancelToAddRatio3s = 0.9m,
                    BidCancelCount1s = 3,
                    BidAddCount1s = 4,
                    AskCancelCount1s = 2,
                    AskAddCount1s = 3,
                    BidTotalCanceledSize1s = 20m,
                    AskTotalCanceledSize1s = 18m,
                    BidTotalAddedSize1s = 25m,
                    AskTotalAddedSize1s = 22m
                }
            }
        };
    }

    public static TradeJournalEntry BuildRejectedEntry()
    {
        var entry = BuildAcceptedEntry();
        entry.DecisionOutcome = "Rejected";
        entry.RejectionReason = "TapeStale";
        entry.DecisionTrace = new List<string> { "GateReject:TapeStale" };
        entry.DataQualityFlags = new List<string> { "TapeStale" };
        return entry;
    }

    public static TradeJournalEntry BuildMissingContextRejectionEntry()
    {
        return new TradeJournalEntry
        {
            SchemaVersion = TradeJournal.CurrentSchemaVersion,
            DecisionId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Source = "IBKR",
            EntryType = "Rejection",
            MarketTimestampUtc = DateTimeOffset.UtcNow,
            DecisionTimestampUtc = DateTimeOffset.UtcNow,
            TradingMode = "Signals",
            Symbol = "TEST",
            DecisionOutcome = "Rejected",
            RejectionReason = "MissingBookContext",
            DecisionTrace = new List<string> { "GateReject:MissingBookContext" },
            DataQualityFlags = new List<string> { "MissingBookContext" },
            ObservedMetrics = null,
            DecisionInputs = null
        };
    }

    public static async Task<string> WriteEntryAsync(TradeJournalEntry entry)
    {
        var journalPath = Path.Combine(Path.GetTempPath(), "ramstockalerts-tests", $"{Guid.NewGuid()}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("SignalsJournal:FilePath", journalPath)
            })
            .Build();

        var journal = new TradeJournal(config, NullLogger<TradeJournal>.Instance);
        await journal.StartAsync(CancellationToken.None);

        try
        {
            if (!journal.TryEnqueue(entry))
            {
                throw new InvalidOperationException("Failed to enqueue journal entry.");
            }

            return await ReadSingleLineAsync(journalPath, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await journal.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> ReadSingleLineAsync(string path, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line;
                }
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("Journal did not write a line within the timeout.");
    }
}


