using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public sealed class ShadowTradingCoordinator
{
    private const int TapePresenceWindowMs = 3000;
    private readonly OrderFlowMetrics _metrics;
    private readonly OrderFlowSignalValidator _validator;
    private readonly ShadowTradeJournal _journal;
    private readonly ILogger<ShadowTradingCoordinator> _logger;
    private readonly bool _enabled;
    private readonly bool _recordBlueprints;
    private readonly string _tradingMode;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly ScarcityController _scarcityController;
    private readonly ConcurrentDictionary<string, long> _lastProcessedSnapshotMs = new();
    private readonly Dictionary<Guid, PendingRankEntry> _pendingRankedEntries = new();

    public ShadowTradingCoordinator(
        IConfiguration configuration,
        OrderFlowMetrics metrics,
        OrderFlowSignalValidator validator,
        ShadowTradeJournal journal,
        ScarcityController scarcityController,
        ILogger<ShadowTradingCoordinator> logger)
    {
        _metrics = metrics;
        _validator = validator;
        _journal = journal;
        _scarcityController = scarcityController;
        _logger = logger;

        var tradingMode = configuration.GetValue<string>("TradingMode") ?? string.Empty;
        _enabled = string.Equals(tradingMode, "Shadow", StringComparison.OrdinalIgnoreCase);
        _recordBlueprints = configuration.GetValue("RecordBlueprints", true);
        _tradingMode = string.IsNullOrWhiteSpace(tradingMode) ? "Shadow" : tradingMode;

        if (_enabled)
        {
            _logger.LogInformation("[Shadow] Shadow trading coordinator enabled. RecordBlueprints={Record}",
                _recordBlueprints);
        }
    }

    public void ProcessSnapshot(OrderBookState book, long nowMs)
    {
        if (!_enabled)
        {
            return;
        }

        FinalizeRankedDecisions(_scarcityController.FlushRankWindow(nowMs));

        if (!book.IsBookValid(out _, nowMs))
        {
            return;
        }

        if (!HasRecentTape(book, nowMs))
        {
            return;
        }

        var snapshot = _metrics.GetLatestSnapshot(book.Symbol);
        if (snapshot == null)
        {
            return;
        }

        if (_lastProcessedSnapshotMs.TryGetValue(book.Symbol, out var lastProcessed) &&
            snapshot.TimestampMs <= lastProcessed)
        {
            return;
        }

        _lastProcessedSnapshotMs[book.Symbol] = snapshot.TimestampMs;

        var decision = _validator.EvaluateDecision(book, nowMs);
        if (!decision.HasCandidate || decision.Snapshot == null)
        {
            return;
        }

        var depthSnapshot = BuildDepthSnapshot(book);
        var tapeStats = BuildTapeStats(book, nowMs);
        var candidateId = Guid.NewGuid();

        if (!decision.Accepted)
        {
            var rejectionReason = decision.RejectionReason;

            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats);
            rejectedEntry.Decision = "Rejected";
            rejectedEntry.Accepted = false;
            rejectedEntry.RejectionReason = rejectionReason;

            EnqueueEntry(rejectedEntry);
            _logger.LogInformation("[Shadow] Signal rejected for {Symbol} ({Direction}): {Reason}",
                snapshot.Symbol, decision.Direction, rejectionReason ?? "Unknown");
            return;
        }

        var blueprintPlan = BuildBlueprintPlan(book, decision.Direction);
        if (_recordBlueprints && !blueprintPlan.Success)
        {
            var rejectionReason = blueprintPlan.RejectionReason ?? "BlueprintUnavailable";
            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats);
            rejectedEntry.Decision = "Rejected";
            rejectedEntry.Accepted = false;
            rejectedEntry.RejectionReason = rejectionReason;

            EnqueueEntry(rejectedEntry);
            _logger.LogInformation("[Shadow] Signal rejected for {Symbol} ({Direction}): {Reason}",
                snapshot.Symbol, decision.Direction, rejectionReason);
            return;
        }

        var pendingEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats);
        _pendingRankedEntries[candidateId] = new PendingRankEntry(pendingEntry, blueprintPlan, nowMs);

        var resolved = _scarcityController.StageCandidate(candidateId, snapshot.Symbol, decision.Signal?.Confidence ?? 0m, nowMs);
        FinalizeRankedDecisions(resolved);
    }

    private static bool HasRecentTape(OrderBookState book, long nowMs)
    {
        if (book.RecentTrades.Count == 0)
        {
            return false;
        }

        var lastTrade = book.RecentTrades.LastOrDefault();
        if (lastTrade.TimestampMs == 0)
        {
            return false;
        }

        return nowMs - lastTrade.TimestampMs <= TapePresenceWindowMs;
    }

    private BlueprintPlan BuildBlueprintPlan(OrderBookState book, string? direction)
    {
        if (!_recordBlueprints)
        {
            return new BlueprintPlan(true, null, null, null, null);
        }

        if (TryBuildBlueprint(book, direction, out var entry, out var stop, out var target, out var rejectionReason))
        {
            return new BlueprintPlan(true, entry, stop, target, null);
        }

        return new BlueprintPlan(false, null, null, null, rejectionReason ?? "BlueprintUnavailable");
    }

    private ShadowTradeJournalEntry BuildJournalEntry(
        OrderBookState book,
        OrderFlowMetrics.MetricSnapshot snapshot,
        OrderFlowSignalValidator.OrderFlowSignalDecision decision,
        long nowMs,
        Guid decisionId,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats)
    {
        return new ShadowTradeJournalEntry
        {
            SchemaVersion = 1,
            DecisionId = decisionId,
            SessionId = _sessionId,
            Source = "IBKR",
            TimestampUtc = DateTime.UtcNow,
            TradingMode = _tradingMode,
            Symbol = snapshot.Symbol,
            Direction = decision.Direction ?? string.Empty,
            Score = decision.Signal?.Confidence ?? 0m,
            QueueImbalance = snapshot.QueueImbalance,
            BidDepth4Level = snapshot.BidDepth4Level,
            AskDepth4Level = snapshot.AskDepth4Level,
            BidWallAgeMs = snapshot.BidWallAgeMs,
            AskWallAgeMs = snapshot.AskWallAgeMs,
            BidAbsorptionRate = snapshot.BidAbsorptionRate,
            AskAbsorptionRate = snapshot.AskAbsorptionRate,
            SpoofScore = snapshot.SpoofScore,
            TapeAcceleration = snapshot.TapeAcceleration,
            TradesIn3Sec = snapshot.TradesIn3Sec,
            Spread = snapshot.Spread,
            MidPrice = snapshot.MidPrice,
            LastPrice = tapeStats.LastPrice,
            VwapPrice = tapeStats.VwapPrice,
            BestBidPrice = book.BestBid,
            BestBidSize = depthSnapshot.BestBidSize,
            BestAskPrice = book.BestAsk,
            BestAskSize = depthSnapshot.BestAskSize,
            TotalBidSizeTopN = depthSnapshot.TotalBidSizeTopN,
            TotalAskSizeTopN = depthSnapshot.TotalAskSizeTopN,
            BidAskRatioTopN = depthSnapshot.BidAskRatioTopN,
            TapeVelocity3Sec = tapeStats.Velocity,
            TapeVolume3Sec = tapeStats.Volume,
            LastDepthUpdateAgeMs = depthSnapshot.LastDepthUpdateAgeMs,
            LastTapeUpdateAgeMs = tapeStats.LastTapeAgeMs,
            TickerCooldownRemainingSec = _validator.GetCooldownRemainingSeconds(snapshot.Symbol, nowMs),
            AlertsLastHourCount = _validator.GetAlertCountInLastHour(nowMs),
            BidsTopN = depthSnapshot.BidsTopN,
            AsksTopN = depthSnapshot.AsksTopN
        };
    }

    private void FinalizeRankedDecisions(IEnumerable<RankedScarcityDecision> decisions)
    {
        foreach (var ranked in decisions)
        {
            if (!_pendingRankedEntries.TryGetValue(ranked.CandidateId, out var pending))
            {
                _logger.LogWarning("[Shadow] Missing pending entry for decision {DecisionId}", ranked.CandidateId);
                continue;
            }

            var entry = pending.Entry;
            if (ranked.Decision.Accepted)
            {
                entry.Accepted = true;
                entry.Decision = "Accepted";
                entry.RejectionReason = null;

                if (_recordBlueprints && pending.Blueprint.Success)
                {
                    entry.Entry = pending.Blueprint.Entry;
                    entry.Stop = pending.Blueprint.Stop;
                    entry.Target = pending.Blueprint.Target;
                }

                _validator.RecordAcceptedSignal(entry.Symbol, pending.TimestampMsUtc);
                _logger.LogInformation("[Shadow] Signal accepted for {Symbol} ({Direction}) Score={Score}",
                    entry.Symbol, entry.Direction, entry.Score);
            }
            else
            {
                entry.Accepted = false;
                entry.Decision = "ScarcityRejected";
                entry.RejectionReason = FormatRejectionReason(ranked.Decision);

                _logger.LogInformation("[Shadow] Signal rejected for {Symbol} ({Direction}): {Reason}",
                    entry.Symbol, entry.Direction, entry.RejectionReason ?? "Unknown");
            }

            EnqueueEntry(entry);
            _pendingRankedEntries.Remove(ranked.CandidateId);
        }
    }

    private void EnqueueEntry(ShadowTradeJournalEntry entry)
    {
        if (!_journal.TryEnqueue(entry))
        {
            _logger.LogWarning("[Shadow] Journal enqueue failed for {Symbol}", entry.Symbol);
        }
    }

    private static string FormatRejectionReason(ScarcityDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.ReasonDetail))
        {
            return decision.ReasonCode;
        }

        return $"{decision.ReasonCode}:{decision.ReasonDetail}";
    }

    private readonly record struct TapeStats(
        decimal Velocity,
        decimal Volume,
        decimal? LastPrice,
        decimal? VwapPrice,
        long? LastTapeAgeMs);

    private sealed record PendingRankEntry(
        ShadowTradeJournalEntry Entry,
        BlueprintPlan Blueprint,
        long TimestampMsUtc);

    private sealed record BlueprintPlan(
        bool Success,
        decimal? Entry,
        decimal? Stop,
        decimal? Target,
        string? RejectionReason);

    private static TapeStats BuildTapeStats(OrderBookState book, long nowMs)
    {
        var windowStart = nowMs - TapePresenceWindowMs;
        var trades = book.RecentTrades.Where(t => t.TimestampMs >= windowStart).ToList();

        var volume = trades.Sum(t => t.Size);
        var velocity = TapePresenceWindowMs > 0 ? trades.Count / (decimal)(TapePresenceWindowMs / 1000m) : 0m;

        decimal? lastPrice = null;
        long? lastTapeAgeMs = null;
        if (book.RecentTrades.Count > 0)
        {
            var lastTrade = book.RecentTrades.Last();
            lastPrice = (decimal)lastTrade.Price;
            lastTapeAgeMs = nowMs - lastTrade.TimestampMs;
        }

        decimal? vwap = null;
        var totalSize = trades.Sum(t => t.Size);
        if (totalSize > 0)
        {
            var weightedSum = trades.Sum(t => (decimal)t.Price * t.Size);
            vwap = weightedSum / totalSize;
        }

        return new TapeStats(velocity, volume, lastPrice, vwap, lastTapeAgeMs);
    }

    private sealed class DepthSnapshot
    {
        public decimal BestBidSize { get; init; }
        public decimal BestAskSize { get; init; }
        public decimal TotalBidSizeTopN { get; init; }
        public decimal TotalAskSizeTopN { get; init; }
        public decimal BidAskRatioTopN { get; init; }
        public List<ShadowTradeJournalEntry.DepthLevelSnapshot> BidsTopN { get; init; } = new();
        public List<ShadowTradeJournalEntry.DepthLevelSnapshot> AsksTopN { get; init; } = new();
        public long? LastDepthUpdateAgeMs { get; init; }
    }

    private static DepthSnapshot BuildDepthSnapshot(OrderBookState book, int depthLevels = 5)
    {
        var bids = book.BidLevels.Take(depthLevels).Select((x, idx) => new ShadowTradeJournalEntry.DepthLevelSnapshot
        {
            Level = idx,
            Price = x.Price,
            Size = x.Size
        }).ToList();

        var asks = book.AskLevels.Take(depthLevels).Select((x, idx) => new ShadowTradeJournalEntry.DepthLevelSnapshot
        {
            Level = idx,
            Price = x.Price,
            Size = x.Size
        }).ToList();

        var totalBid = bids.Sum(b => b.Size);
        var totalAsk = asks.Sum(a => a.Size);
        var ratio = totalAsk > 0m ? totalBid / totalAsk : 0m;

        long? depthAge = null;
        if (book.LastDepthUpdateUtcMs > 0)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            depthAge = nowMs - book.LastDepthUpdateUtcMs;
        }

        return new DepthSnapshot
        {
            BestBidSize = bids.FirstOrDefault()?.Size ?? 0m,
            BestAskSize = asks.FirstOrDefault()?.Size ?? 0m,
            TotalBidSizeTopN = totalBid,
            TotalAskSizeTopN = totalAsk,
            BidAskRatioTopN = ratio,
            BidsTopN = bids,
            AsksTopN = asks,
            LastDepthUpdateAgeMs = depthAge
        };
    }

    private static bool TryBuildBlueprint(
        OrderBookState book,
        string? direction,
        out decimal? entry,
        out decimal? stop,
        out decimal? target,
        out string? rejectionReason)
    {
        entry = null;
        stop = null;
        target = null;
        rejectionReason = null;

        var spread = book.Spread;
        if (spread <= 0m)
        {
            rejectionReason = "InvalidSpread";
            return false;
        }

        var isBuy = string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase);
        if (isBuy)
        {
            entry = book.BestAsk;
            if (entry <= 0m)
            {
                rejectionReason = "InvalidAsk";
                return false;
            }

            stop = entry - (spread * 4m);
            target = entry + (spread * 8m);
        }
        else
        {
            entry = book.BestBid;
            if (entry <= 0m)
            {
                rejectionReason = "InvalidBid";
                return false;
            }

            stop = entry + (spread * 4m);
            target = entry - (spread * 8m);
        }

        return true;
    }
}
