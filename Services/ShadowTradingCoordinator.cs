using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Decisions;
using RamStockAlerts.Models.Microstructure;

namespace RamStockAlerts.Services;

public sealed class ShadowTradingCoordinator
{
    private const int TapePresenceWindowMs = 3000;
    private readonly OrderFlowMetrics _metrics;
    private readonly OrderFlowSignalValidator _validator;
    private readonly IShadowTradeJournal _journal;
    private readonly MarketDataSubscriptionManager _subscriptionManager;
    private readonly ILogger<ShadowTradingCoordinator> _logger;
    private readonly bool _enabled;
    private readonly bool _recordBlueprints;
    private readonly string _tradingMode;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly ScarcityController _scarcityController;
    private readonly RejectionLogger _rejectionLogger;
    private readonly GatingRejectionThrottle _gatingRejectionThrottle;
    private readonly ConcurrentDictionary<string, long> _lastProcessedSnapshotMs = new();
    private readonly Dictionary<Guid, PendingRankEntry> _pendingRankedEntries = new();
    private readonly ShadowTradingHelpers.TapeGateConfig _tapeGateConfig;
    private readonly bool _emitGateTrace;

    public ShadowTradingCoordinator(
        IConfiguration configuration,
        OrderFlowMetrics metrics,
        OrderFlowSignalValidator validator,
        IShadowTradeJournal journal,
        ScarcityController scarcityController,
        MarketDataSubscriptionManager subscriptionManager,
        ILogger<ShadowTradingCoordinator> logger)
    {
        _metrics = metrics;
        _validator = validator;
        _journal = journal;
        _subscriptionManager = subscriptionManager;
        _scarcityController = scarcityController;
        _logger = logger;

        var tradingMode = configuration.GetValue<string>("TradingMode") ?? string.Empty;
        _enabled = string.Equals(tradingMode, "Shadow", StringComparison.OrdinalIgnoreCase);
        _recordBlueprints = configuration.GetValue("RecordBlueprints", true);
        _tradingMode = string.IsNullOrWhiteSpace(tradingMode) ? "Shadow" : tradingMode;
        _tapeGateConfig = ShadowTradingHelpers.ReadTapeGateConfig(configuration);
        var gatingRejectDedupeSeconds = Math.Max(0, configuration.GetValue("ShadowTrading:GatingRejectDedupeSeconds", 4));
        _gatingRejectionThrottle = new GatingRejectionThrottle(TimeSpan.FromSeconds(gatingRejectDedupeSeconds));
        var gateRejectMinIntervalMs = configuration.GetValue("MarketData:GateRejectLogMinIntervalMs", 2000);
        _rejectionLogger = new RejectionLogger(TimeSpan.FromMilliseconds(Math.Max(0, gateRejectMinIntervalMs)));
        _emitGateTrace = configuration.GetValue("ShadowTradeJournal:EmitGateTrace", true);

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
            var tapeStatus = ShadowTradingHelpers.GetTapeStatus(
                book,
                nowMs,
                _subscriptionManager.IsTapeEnabled(book.Symbol),
                _tapeGateConfig);
            var decisionResult = BuildNotReadyDecisionResult(book, nowMs, HardRejectReason.NotReadyBookInvalid, tapeStatus);
            TryLogGatingRejection(book.Symbol, "NotReady_BookInvalid", decisionResult, tapeStatus);
            return;
        }

        var depthEnabled = _subscriptionManager.IsDepthEnabled(book.Symbol);
        var tapeEnabled = _subscriptionManager.IsTapeEnabled(book.Symbol);
        if (!depthEnabled || !tapeEnabled)
        {
            var tapeStatus = ShadowTradingHelpers.GetTapeStatus(book, nowMs, tapeEnabled, _tapeGateConfig);
            var reason = tapeEnabled ? "NotReady_NoDepth" : "NotReady_TapeMissingSubscription";
            var hardReason = tapeEnabled ? HardRejectReason.NotReadyNoDepth : HardRejectReason.NotReadyTapeMissingSubscription;
            var decisionResult = BuildNotReadyDecisionResult(book, nowMs, hardReason, tapeStatus);
            TryLogGatingRejection(book.Symbol, reason, decisionResult, tapeStatus);
            return;
        }

        var tapeStatusReadyCheck = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, _tapeGateConfig);
        var tapeRejectionReason = GetTapeRejectionReason(tapeStatusReadyCheck);
        if (tapeRejectionReason != null)
        {
            // Log detailed staleness info when tape gate blocks
            if (tapeStatusReadyCheck.Kind == ShadowTradingHelpers.TapeStatusKind.Stale && book.RecentTrades.Count > 0)
            {
                var lastTrade = book.RecentTrades.LastOrDefault();
                _logger.LogWarning(
                    "[ShadowTrading GATE] Tape staleness blocking {Symbol}: nowMs={NowMs}, lastTapeMs={LastTapeMs}, ageMs={AgeMs}, staleWindowMs={StaleWindowMs}, timeSource=UnixEpoch",
                    book.Symbol, nowMs, lastTrade.TimestampMs, tapeStatusReadyCheck.AgeMs, _tapeGateConfig.StaleWindowMs);
            }
            
            var decisionResult = BuildNotReadyDecisionResult(
                book,
                nowMs,
                MapReason(tapeRejectionReason),
                tapeStatusReadyCheck);
            TryLogGatingRejection(book.Symbol, tapeRejectionReason, decisionResult, tapeStatusReadyCheck);
            return;
        }

        var snapshot = _metrics.GetLatestSnapshot(book.Symbol);
        if (snapshot == null)
        {
            var decisionResult = BuildNotReadyDecisionResult(book, nowMs, HardRejectReason.NotReadyNoDepth, tapeStatusReadyCheck);
            TryLogGatingRejection(book.Symbol, "NotReady_NoDepth", decisionResult, tapeStatusReadyCheck);
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
        var tapeStats = BuildTapeStats(book, nowMs, decision.Direction);
        var depthDeltaSnapshot = book.DepthDeltaTracker.GetSnapshot(nowMs);
        var candidateId = Guid.NewGuid();

        if (!decision.Accepted)
        {
            var rejectionReason = decision.RejectionReason;
            var decisionResult = BuildDecisionResult(
                snapshot,
                depthSnapshot,
                tapeStats,
                depthDeltaSnapshot,
                decision,
                DecisionOutcome.Rejected,
                BuildHardRejectReasons(rejectionReason),
                nowMs,
                book.BestBid,
                book.BestAsk,
                book,
                tapeStatusReadyCheck);

            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
            rejectedEntry.DecisionOutcome = "Rejected";
            rejectedEntry.RejectionReason = rejectionReason;
            rejectedEntry.DecisionResult = decisionResult;
            rejectedEntry.DecisionTrace = BuildDecisionTraceForRejection("ValidatorReject", rejectionReason);

            EnqueueEntry(rejectedEntry);
            _logger.LogInformation("[Shadow] Signal rejected for {Symbol} ({Direction}): {Reason}",
                snapshot.Symbol, decision.Direction, rejectionReason ?? "Unknown");
            return;
        }

        if (ShouldRejectForSpoof(decision.Direction, depthDeltaSnapshot, snapshot, tapeStats.Volume))
        {
            var decisionResult = BuildDecisionResult(
                snapshot,
                depthSnapshot,
                tapeStats,
                depthDeltaSnapshot,
                decision,
                DecisionOutcome.Rejected,
                new[] { HardRejectReason.SpoofSuspected },
                nowMs,
                book.BestBid,
                book.BestAsk,
                book,
                tapeStatusReadyCheck);
            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
            rejectedEntry.DecisionOutcome = "Rejected";
            rejectedEntry.RejectionReason = "SpoofSuspected";
            rejectedEntry.DecisionResult = decisionResult;
            rejectedEntry.DecisionTrace = BuildDecisionTraceForRejection("SpoofCheckFail", "SpoofSuspected");

            EnqueueEntry(rejectedEntry);
            _logger.LogInformation("[Shadow] Signal rejected for {Symbol} ({Direction}): {Reason}",
                snapshot.Symbol, decision.Direction, "SpoofSuspected");
            return;
        }

        if (ShouldRejectForReplenishment(decision.Direction, depthDeltaSnapshot, snapshot, tapeStats))
        {
            var decisionResult = BuildDecisionResult(
                snapshot,
                depthSnapshot,
                tapeStats,
                depthDeltaSnapshot,
                decision,
                DecisionOutcome.Rejected,
                new[] { HardRejectReason.ReplenishmentSuspected },
                nowMs,
                book.BestBid,
                book.BestAsk,
                book,
                tapeStatusReadyCheck);
            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
            rejectedEntry.DecisionOutcome = "Rejected";
            rejectedEntry.RejectionReason = "ReplenishmentSuspected";
            rejectedEntry.DecisionResult = decisionResult;
            rejectedEntry.DecisionTrace = BuildDecisionTraceForRejection("ReplenishmentCheckFail", "ReplenishmentSuspected");

            EnqueueEntry(rejectedEntry);
            return;
        }

        if (ShouldRejectForReplenishment(decision.Direction, depthDeltaSnapshot, snapshot, tapeStats))
        {
            var decisionResult = BuildDecisionResult(
                snapshot,
                depthSnapshot,
                tapeStats,
                depthDeltaSnapshot,
                decision,
                DecisionOutcome.Rejected,
                new[] { HardRejectReason.ReplenishmentSuspected },
                nowMs,
                book.BestBid,
                book.BestAsk,
                book,
                tapeStatusReadyCheck);
            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
            rejectedEntry.DecisionOutcome = "Rejected";
            rejectedEntry.RejectionReason = "ReplenishmentSuspected";
            rejectedEntry.DecisionResult = decisionResult;
            rejectedEntry.DecisionTrace = BuildDecisionTraceForRejection("ReplenishmentCheckFail", "ReplenishmentSuspected");

            EnqueueEntry(rejectedEntry);
            return;
        }

        if (ShouldRejectForAbsorption(decision.Direction, snapshot, tapeStats))
        {
            var decisionResult = BuildDecisionResult(
                snapshot,
                depthSnapshot,
                tapeStats,
                depthDeltaSnapshot,
                decision,
                DecisionOutcome.Rejected,
                new[] { HardRejectReason.AbsorptionInsufficient },
                nowMs,
                book.BestBid,
                book.BestAsk,
                book,
                tapeStatusReadyCheck);
            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
            rejectedEntry.DecisionOutcome = "Rejected";
            rejectedEntry.RejectionReason = "AbsorptionInsufficient";
            rejectedEntry.DecisionResult = decisionResult;
            rejectedEntry.DecisionTrace = BuildDecisionTraceForRejection("AbsorptionCheckFail", "AbsorptionInsufficient");

            EnqueueEntry(rejectedEntry);
            return;
        }

        var blueprintPlan = BuildBlueprintPlan(book, decision.Direction);
        if (_recordBlueprints && !blueprintPlan.Success)
        {
            var rejectionReason = blueprintPlan.RejectionReason ?? "BlueprintUnavailable";
            var decisionResult = BuildDecisionResult(
                snapshot,
                depthSnapshot,
                tapeStats,
                depthDeltaSnapshot,
                decision,
                DecisionOutcome.Rejected,
                BuildHardRejectReasons("BlueprintUnavailable"),
                nowMs,
                book.BestBid,
                book.BestAsk,
                book,
                tapeStatusReadyCheck);
            var rejectedEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
            rejectedEntry.DecisionOutcome = "Rejected";
            rejectedEntry.RejectionReason = rejectionReason;
            rejectedEntry.DecisionResult = decisionResult;
            rejectedEntry.DecisionTrace = BuildDecisionTraceForRejection("BlueprintFail", rejectionReason);

            EnqueueEntry(rejectedEntry);
            _logger.LogInformation("[Shadow] Signal rejected for {Symbol} ({Direction}): {Reason}",
                snapshot.Symbol, decision.Direction, rejectionReason);
            return;
        }

        var vwapBonus = tapeStats.VwapReclaimDetected ? 0.5m : 0m;

        var acceptedDecisionResult = BuildDecisionResult(
            snapshot,
            depthSnapshot,
            tapeStats,
            depthDeltaSnapshot,
            decision,
            DecisionOutcome.Accepted,
            Array.Empty<HardRejectReason>(),
            nowMs,
            book.BestBid,
            book.BestAsk,
            book,
            tapeStatusReadyCheck);

        var pendingEntry = BuildJournalEntry(book, snapshot, decision, nowMs, candidateId, depthSnapshot, tapeStats, tapeStatusReadyCheck);
        pendingEntry.DecisionResult = acceptedDecisionResult;
        pendingEntry.DecisionOutcome = "Pending";
        pendingEntry.DecisionTrace = BuildDecisionTraceForPending();
        _pendingRankedEntries[candidateId] = new PendingRankEntry(pendingEntry, blueprintPlan, nowMs, vwapBonus);

        var baseScore = decision.Signal?.Confidence ?? 0m;
        var rankScore = baseScore + vwapBonus;
        var resolved = _scarcityController.StageCandidate(candidateId, snapshot.Symbol, rankScore, nowMs);
        FinalizeRankedDecisions(resolved);
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
        TapeStats tapeStats,
        ShadowTradingHelpers.TapeStatus tapeStatus)
    {
        var marketTs = snapshot.TimestampMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(snapshot.TimestampMs)
            : (DateTimeOffset?)null;
        var decisionTs = nowMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(nowMs) : (DateTimeOffset?)null;
        var depthDeltaSnapshot = book.DepthDeltaTracker.GetSnapshot(nowMs);

        return new ShadowTradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = decisionId,
            SessionId = _sessionId,
            Source = "IBKR",
            EntryType = "Signal",
            MarketTimestampUtc = marketTs,
            DecisionTimestampUtc = decisionTs,
            TradingMode = _tradingMode,
            Symbol = snapshot.Symbol,
            Direction = decision.Direction,
            ObservedMetrics = BuildObservedMetrics(snapshot, depthSnapshot, tapeStats, depthDeltaSnapshot, book),
            DecisionInputs = BuildDecisionInputs(snapshot, depthSnapshot, tapeStats, depthDeltaSnapshot, decision, nowMs, book),
            DecisionTrace = new List<string>(),
            DataQualityFlags = BuildDataQualityFlags(book, depthSnapshot, tapeStats, tapeStatus, nowMs)
        };
    }

    private StrategyDecisionResult BuildDecisionResult(
        OrderFlowMetrics.MetricSnapshot snapshot,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats,
        DepthDeltaSnapshot depthDeltaSnapshot,
        OrderFlowSignalValidator.OrderFlowSignalDecision decision,
        DecisionOutcome outcome,
        IReadOnlyList<HardRejectReason> reasons,
        long nowMs,
        decimal bestBidPrice,
        decimal bestAskPrice,
        OrderBookState book,
        ShadowTradingHelpers.TapeStatus tapeStatus)
    {
        var context = new StrategyDecisionBuildContext
        {
            Outcome = outcome,
            Direction = ParseDirection(decision.Direction),
            Score = decision.Signal?.Confidence ?? 0m,
            HardRejectReasons = reasons,
            Symbol = snapshot.Symbol,
            TimestampMs = snapshot.TimestampMs,
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
            BestBidPrice = bestBidPrice,
            BestBidSize = depthSnapshot.BestBidSize,
            BestAskPrice = bestAskPrice,
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
            IsBookValid = true,
            TapeRecent = tapeStatus.IsReady,
            BidsTopN = depthSnapshot.BidsTopN
                .Select(b => new FeatureDepthLevelSnapshot(b.Level, b.Price, b.Size))
                .ToList(),
            AsksTopN = depthSnapshot.AsksTopN
                .Select(a => new FeatureDepthLevelSnapshot(a.Level, a.Price, a.Size))
                .ToList(),
            BidCancelToAddRatio1s = depthDeltaSnapshot.Bid1s.CancelToAddRatio,
            AskCancelToAddRatio1s = depthDeltaSnapshot.Ask1s.CancelToAddRatio,
            BidCancelToAddRatio3s = depthDeltaSnapshot.Bid3s.CancelToAddRatio,
            AskCancelToAddRatio3s = depthDeltaSnapshot.Ask3s.CancelToAddRatio,
            BidCancelCount1s = depthDeltaSnapshot.Bid1s.CancelCount,
            BidAddCount1s = depthDeltaSnapshot.Bid1s.AddCount,
            AskCancelCount1s = depthDeltaSnapshot.Ask1s.CancelCount,
            AskAddCount1s = depthDeltaSnapshot.Ask1s.AddCount,
            BidTotalCanceledSize1s = depthDeltaSnapshot.Bid1s.TotalCanceledSize,
            AskTotalCanceledSize1s = depthDeltaSnapshot.Ask1s.TotalCanceledSize,
            BidTotalAddedSize1s = depthDeltaSnapshot.Bid1s.TotalAddedSize,
            AskTotalAddedSize1s = depthDeltaSnapshot.Ask1s.TotalAddedSize,
            CurrentVwap = tapeStats.CumulativeVwap ?? 0m,
            PriceVsVwap = (tapeStats.LastPrice ?? 0m) - (tapeStats.CumulativeVwap ?? 0m),
            VwapReclaimDetected = IsVwapReclaim(
                decision.Direction,
                tapeStats.LastPrice,
                tapeStats.CumulativeVwap,
                tapeStats.VwapPrice,
                tapeStats.Volume),
            VwapConfirmBonus = tapeStats.VwapReclaimDetected ? 0.5m : 0m
        };

        return StrategyDecisionResultBuilder.Build(context);
    }

    private static TradeDirection? ParseDirection(string? direction) =>
        string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : string.Equals(direction, "SELL", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Sell
                : null;

    public static bool ShouldRejectForSpoof(
        string? direction,
        DepthDeltaSnapshot deltaSnapshot,
        OrderFlowMetrics.MetricSnapshot snapshot,
        decimal tapeVolume)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return false;
        }

        var isBuy = string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase);
        var window1 = isBuy ? deltaSnapshot.Bid1s : deltaSnapshot.Ask1s;
        var window3 = isBuy ? deltaSnapshot.Bid3s : deltaSnapshot.Ask3s;

        var cancelDominant = window1.CancelCount > 0 &&
                             window1.CancelToAddRatio >= 2m &&
                             window1.TotalCanceledSize >= window1.TotalAddedSize &&
                             window3.CancelToAddRatio >= 2m &&
                             window3.CancelCount > 0;

        var noPrints = snapshot.TradesIn3Sec <= 1 && tapeVolume <= 0m;

        return cancelDominant && noPrints;
    }

    public static bool ShouldRejectForReplenishment(
        string? direction,
        DepthDeltaSnapshot deltaSnapshot,
        OrderFlowMetrics.MetricSnapshot metrics,
        TapeStats tapeStats)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return false;
        }

        const int MinAdds = 3;
        const decimal MinAddedSize = 10m;
        const int MaxTrades = 1;
        const decimal MaxTapeVolume = 0m;
        const decimal MaxCancelToAddRatio = 2m;

        var isBuy = string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase);
        var window1 = isBuy ? deltaSnapshot.Ask1s : deltaSnapshot.Bid1s;

        var addHeavy = window1.AddCount >= MinAdds && window1.TotalAddedSize >= MinAddedSize;
        var printsWeak = metrics.TradesIn3Sec <= MaxTrades && tapeStats.Volume <= MaxTapeVolume;
        var notSpoofLike = window1.CancelToAddRatio < MaxCancelToAddRatio;

        return addHeavy && printsWeak && notSpoofLike;
    }

    public static bool ShouldRejectForAbsorption(
        string? direction,
        OrderFlowMetrics.MetricSnapshot metrics,
        TapeStats tapeStats)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return false;
        }

        const int MinTrades = 2;
        const decimal MinTapeVolume = 1m;

        var hasTrades = metrics.TradesIn3Sec >= MinTrades;
        var hasVolume = tapeStats.Volume >= MinTapeVolume;

        return !(hasTrades && hasVolume);
    }

    public static bool IsVwapReclaim(
        string? direction,
        decimal? lastPrice,
        decimal? cumulativeVwap,
        decimal? windowVwap,
        decimal windowVolume)
    {
        if (string.IsNullOrWhiteSpace(direction) || lastPrice is null || cumulativeVwap is null)
        {
            return false;
        }

        const decimal MinWindowVolume = 1m;
        var price = lastPrice.Value;
        var vwap = cumulativeVwap.Value;
        var recentBelow = windowVwap.HasValue ? windowVwap.Value < vwap : false;
        var recentAbove = windowVwap.HasValue ? windowVwap.Value > vwap : false;
        var hasVolume = windowVolume >= MinWindowVolume;

        if (string.Equals(direction, "BUY", StringComparison.OrdinalIgnoreCase))
        {
            return price > vwap && recentBelow && hasVolume;
        }

        if (string.Equals(direction, "SELL", StringComparison.OrdinalIgnoreCase))
        {
            return price < vwap && recentAbove && hasVolume;
        }

        return false;
    }

    private StrategyDecisionResult? UpdateDecisionResult(
        StrategyDecisionResult? existing,
        DecisionOutcome outcome,
        IReadOnlyList<HardRejectReason>? hardRejectReasons)
    {
        if (existing == null)
        {
            return null;
        }

        return existing with
        {
            Outcome = outcome,
            HardRejectReasons = hardRejectReasons ?? existing.HardRejectReasons
        };
    }

    private StrategyDecisionResult BuildNotReadyDecisionResult(
        OrderBookState book,
        long nowMs,
        HardRejectReason reason,
        ShadowTradingHelpers.TapeStatus tapeStatus)
    {
        var lastTrade = book.RecentTrades.LastOrDefault();
        var lastTapeAge = lastTrade.TimestampMs > 0 ? nowMs - lastTrade.TimestampMs : (long?)null;

        var context = new StrategyDecisionBuildContext
        {
            Outcome = DecisionOutcome.NotReady,
            Direction = null,
            Score = 0m,
            HardRejectReasons = new[] { reason },
            Symbol = book.Symbol,
            TimestampMs = nowMs,
            Spread = book.Spread,
            BestBidPrice = book.BestBid,
            BestAskPrice = book.BestAsk,
            LastDepthUpdateAgeMs = book.LastDepthUpdateUtcMs > 0 ? nowMs - book.LastDepthUpdateUtcMs : null,
            LastTapeUpdateAgeMs = lastTapeAge,
            TapeRecent = tapeStatus.IsReady,
            IsBookValid = book.IsBookValid(out _, nowMs)
        };

        return StrategyDecisionResultBuilder.Build(context);
    }

    private static ShadowTradeJournalEntry.ObservedMetricsSnapshot BuildObservedMetrics(
        OrderFlowMetrics.MetricSnapshot snapshot,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats,
        DepthDeltaSnapshot depthDeltaSnapshot,
        OrderBookState book)
    {
        return new ShadowTradeJournalEntry.ObservedMetricsSnapshot
        {
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
            CumulativeVwap = tapeStats.CumulativeVwap,
            PriceVsVwap = tapeStats.LastPrice.HasValue && tapeStats.CumulativeVwap.HasValue
                ? tapeStats.LastPrice.Value - tapeStats.CumulativeVwap.Value
                : null,
            VwapReclaimDetected = tapeStats.VwapReclaimDetected,
            DepthDelta = BuildDepthDeltaMetrics(depthDeltaSnapshot),
            BidsTopN = depthSnapshot.BidsTopN,
            AsksTopN = depthSnapshot.AsksTopN
        };
    }

    private ShadowTradeJournalEntry.DecisionInputsSnapshot BuildDecisionInputs(
        OrderFlowMetrics.MetricSnapshot snapshot,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats,
        DepthDeltaSnapshot depthDeltaSnapshot,
        OrderFlowSignalValidator.OrderFlowSignalDecision decision,
        long nowMs,
        OrderBookState book)
    {
        return new ShadowTradeJournalEntry.DecisionInputsSnapshot
        {
            Score = decision.Signal is null ? null : (decimal?)decision.Signal.Confidence,
            VwapBonus = decision.Signal is null ? null : (tapeStats.VwapReclaimDetected ? 0.5m : 0m),
            RankScore = decision.Signal is null
                ? null
                : decision.Signal.Confidence + (tapeStats.VwapReclaimDetected ? 0.5m : 0m),
            TickerCooldownRemainingSec = _validator.GetCooldownRemainingSeconds(snapshot.Symbol, nowMs),
            AlertsLastHourCount = _validator.GetAlertCountInLastHour(nowMs),
            QueueImbalance = snapshot.QueueImbalance,
            BidWallAgeMs = snapshot.BidWallAgeMs,
            AskWallAgeMs = snapshot.AskWallAgeMs,
            BidAbsorptionRate = snapshot.BidAbsorptionRate,
            AskAbsorptionRate = snapshot.AskAbsorptionRate,
            SpoofScore = snapshot.SpoofScore,
            TapeAcceleration = snapshot.TapeAcceleration,
            TradesIn3Sec = snapshot.TradesIn3Sec,
            TapeVolume3Sec = tapeStats.Volume,
            Spread = snapshot.Spread,
            BestBidPrice = book.BestBid,
            BestAskPrice = book.BestAsk,
            DepthDelta = BuildDepthDeltaMetrics(depthDeltaSnapshot),
            VwapReclaimDetected = tapeStats.VwapReclaimDetected
        };
    }

    private static ShadowTradeJournalEntry.DepthDeltaMetrics BuildDepthDeltaMetrics(DepthDeltaSnapshot depthDeltaSnapshot)
    {
        return new ShadowTradeJournalEntry.DepthDeltaMetrics
        {
            BidCancelToAddRatio1s = depthDeltaSnapshot.Bid1s.CancelToAddRatio,
            AskCancelToAddRatio1s = depthDeltaSnapshot.Ask1s.CancelToAddRatio,
            BidCancelToAddRatio3s = depthDeltaSnapshot.Bid3s.CancelToAddRatio,
            AskCancelToAddRatio3s = depthDeltaSnapshot.Ask3s.CancelToAddRatio,
            BidCancelCount1s = depthDeltaSnapshot.Bid1s.CancelCount,
            BidAddCount1s = depthDeltaSnapshot.Bid1s.AddCount,
            AskCancelCount1s = depthDeltaSnapshot.Ask1s.CancelCount,
            AskAddCount1s = depthDeltaSnapshot.Ask1s.AddCount,
            BidTotalCanceledSize1s = depthDeltaSnapshot.Bid1s.TotalCanceledSize,
            AskTotalCanceledSize1s = depthDeltaSnapshot.Ask1s.TotalCanceledSize,
            BidTotalAddedSize1s = depthDeltaSnapshot.Bid1s.TotalAddedSize,
            AskTotalAddedSize1s = depthDeltaSnapshot.Ask1s.TotalAddedSize
        };
    }

    private static List<string> BuildDecisionTraceForAcceptance()
    {
        return new List<string>
        {
            "ValidatorPass",
            "SpoofCheckPass",
            "ReplenishmentCheckPass",
            "AbsorptionCheckPass",
            "BlueprintPass"
        };
    }

    private static List<string> BuildDecisionTraceForPending()
    {
        return new List<string>
        {
            "ValidatorPass",
            "SpoofCheckPass",
            "ReplenishmentCheckPass",
            "AbsorptionCheckPass",
            "BlueprintReady",
            "AwaitingScarcityRanking"
        };
    }

    private static List<string> BuildDecisionTraceForScarcityAccepted(List<string>? existing)
    {
        var trace = existing is null || existing.Count == 0 ? BuildDecisionTraceForAcceptance() : new List<string>(existing);
        trace.Add("ScarcityPass");
        return trace;
    }

    private static List<string> BuildDecisionTraceForRejection(string stage, string? reason)
    {
        var trace = new List<string>();
        switch (stage)
        {
            case "ValidatorReject":
                trace.Add($"{stage}:{reason ?? "Unknown"}");
                return trace;
            case "SpoofCheckFail":
                trace.Add("ValidatorPass");
                break;
            case "ReplenishmentCheckFail":
                trace.Add("ValidatorPass");
                trace.Add("SpoofCheckPass");
                break;
            case "AbsorptionCheckFail":
                trace.Add("ValidatorPass");
                trace.Add("SpoofCheckPass");
                trace.Add("ReplenishmentCheckPass");
                break;
            case "BlueprintFail":
                trace.Add("ValidatorPass");
                trace.Add("SpoofCheckPass");
                trace.Add("ReplenishmentCheckPass");
                trace.Add("AbsorptionCheckPass");
                break;
            case "ScarcityReject":
                trace = BuildDecisionTraceForAcceptance();
                break;
        }

        trace.Add($"{stage}:{reason ?? "Unknown"}");
        return trace;
    }

    private ShadowTradeJournalEntry.GateTraceSnapshot? BuildGateTrace(
        OrderBookState? book,
        long nowMs,
        ShadowTradingHelpers.TapeStatus tapeStatus,
        DepthSnapshot? depthSnapshot)
    {
        if (book is null)
        {
            return null;
        }

        var lastTrade = book.RecentTrades.LastOrDefault();
        var lastTradeMs = lastTrade.TimestampMs == 0 ? (long?)null : lastTrade.TimestampMs;
        var depthAgeMs = book.LastDepthUpdateUtcMs > 0 ? nowMs - book.LastDepthUpdateUtcMs : (long?)null;
        var lastDepthMs = book.LastDepthUpdateUtcMs > 0 ? book.LastDepthUpdateUtcMs : (long?)null;
        var depthRowsKnown = depthSnapshot is not null 
            ? Math.Max(depthSnapshot.BidsTopN.Count, depthSnapshot.AsksTopN.Count)
            : (int?)null;

        return new ShadowTradeJournalEntry.GateTraceSnapshot
        {
            SchemaVersion = 1,
            NowMs = nowMs,
            
            // Tape context
            LastTradeMs = lastTradeMs,
            TradesInWarmupWindow = tapeStatus.TradesInWarmupWindow,
            WarmedUp = tapeStatus.Kind == ShadowTradingHelpers.TapeStatusKind.Ready,
            StaleAgeMs = tapeStatus.AgeMs,
            
            // Depth context
            LastDepthMs = lastDepthMs,
            DepthAgeMs = depthAgeMs,
            DepthRowsKnown = depthRowsKnown,
            DepthSupported = _subscriptionManager.IsDepthEnabled(book.Symbol),
            
            // Config snapshot
            WarmupMinTrades = tapeStatus.WarmupMinTrades,
            WarmupWindowMs = tapeStatus.WarmupWindowMs,
            StaleWindowMs = _tapeGateConfig.StaleWindowMs,
            DepthStaleWindowMs = 2000 // From OrderBookState.StaleDepthThresholdMs const
        };
    }

    private static string? GetTapeRejectionReason(ShadowTradingHelpers.TapeStatus tapeStatus)
    {
        return tapeStatus.Kind switch
        {
            ShadowTradingHelpers.TapeStatusKind.MissingSubscription => "NotReady_TapeMissingSubscription",
            ShadowTradingHelpers.TapeStatusKind.NotWarmedUp => "NotReady_TapeNotWarmedUp",
            ShadowTradingHelpers.TapeStatusKind.Stale => "NotReady_TapeStale",
            _ => null
        };
    }


    private static List<string> BuildDataQualityFlags(
        OrderBookState book,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats,
        ShadowTradingHelpers.TapeStatus tapeStatus,
        long nowMs)
    {
        var flags = new List<string>();
        if (!book.IsBookValid(out var reason, nowMs))
        {
            flags.Add($"BookInvalid:{reason}");
        }

        switch (tapeStatus.Kind)
        {
            case ShadowTradingHelpers.TapeStatusKind.MissingSubscription:
                flags.Add("TapeMissingSubscription");
                break;
            case ShadowTradingHelpers.TapeStatusKind.NotWarmedUp:
                flags.Add("TapeNotWarmedUp");
                flags.Add($"TapeNotWarmedUp:tradesInWindow={tapeStatus.TradesInWarmupWindow}");
                flags.Add($"TapeNotWarmedUp:warmupMinTrades={tapeStatus.WarmupMinTrades}");
                flags.Add($"TapeNotWarmedUp:warmupWindowMs={tapeStatus.WarmupWindowMs}");
                if (tapeStatus.AgeMs.HasValue)
                {
                    flags.Add($"TapeLastAgeMs={tapeStatus.AgeMs.Value}");
                }
                break;
            case ShadowTradingHelpers.TapeStatusKind.Stale:
                flags.Add("TapeStale");
                if (tapeStatus.AgeMs.HasValue)
                {
                    flags.Add($"TapeStale:ageMs={tapeStatus.AgeMs.Value}");
                }
                break;
        }

        // PartialBook: less than expected configured depth levels were available
        // Expected depth is determined at snapshot creation time (typically 5 levels)
        if (depthSnapshot.BidsTopN.Count < depthSnapshot.ExpectedDepthLevels || 
            depthSnapshot.AsksTopN.Count < depthSnapshot.ExpectedDepthLevels)
        {
            flags.Add("PartialBook");
        }

        if (depthSnapshot.LastDepthUpdateAgeMs.HasValue && depthSnapshot.LastDepthUpdateAgeMs.Value > 2000)
        {
            flags.Add("StaleDepth");
        }

        return flags;
    }

    private static IReadOnlyList<HardRejectReason> BuildHardRejectReasons(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return Array.Empty<HardRejectReason>();
        }

        return new[] { MapReason(reasonCode) };
    }

    private static IReadOnlyList<HardRejectReason> BuildHardRejectReasonsFromScarcity(ScarcityDecision decision)
    {
        return new[] { MapReason(decision.ReasonCode) };
    }

    private static HardRejectReason MapReason(string reasonCode) =>
        reasonCode switch
        {
            "NotReady_BookInvalid" => HardRejectReason.NotReadyBookInvalid,
            "NotReady_TapeStale" => HardRejectReason.NotReadyTapeStale,
            "NotReady_TapeMissingSubscription" => HardRejectReason.NotReadyTapeMissingSubscription,
            "NotReady_TapeNotWarmedUp" => HardRejectReason.NotReadyTapeNotWarmedUp,
            "NotReady_NoDepth" => HardRejectReason.NotReadyNoDepth,
            "CooldownActive" => HardRejectReason.CooldownActive,
            "GlobalRateLimit" => HardRejectReason.GlobalRateLimit,
            "BlueprintUnavailable" => HardRejectReason.BlueprintUnavailable,
            "GlobalLimit" => HardRejectReason.ScarcityGlobalLimit,
            "SymbolLimit" => HardRejectReason.ScarcitySymbolLimit,
            "GlobalCooldown" => HardRejectReason.ScarcityGlobalCooldown,
            "SymbolCooldown" => HardRejectReason.ScarcitySymbolCooldown,
            "RejectedRankedOut" => HardRejectReason.ScarcityRankedOut,
            "SpoofSuspected" => HardRejectReason.SpoofSuspected,
            "ReplenishmentSuspected" => HardRejectReason.ReplenishmentSuspected,
            "AbsorptionInsufficient" => HardRejectReason.AbsorptionInsufficient,
            _ => HardRejectReason.Unknown
        };

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
                entry.DecisionOutcome = "Accepted";
                entry.RejectionReason = null;
                entry.DecisionTrace = BuildDecisionTraceForScarcityAccepted(entry.DecisionTrace);
                entry.DecisionResult = UpdateDecisionResult(entry.DecisionResult, DecisionOutcome.Accepted, null);

                if (_recordBlueprints && pending.Blueprint.Success)
                {
                    entry.Blueprint = new ShadowTradeJournalEntry.BlueprintPlan
                    {
                        Entry = pending.Blueprint.Entry,
                        Stop = pending.Blueprint.Stop,
                        Target = pending.Blueprint.Target
                    };
                }

                if (!string.IsNullOrWhiteSpace(entry.Symbol))
                {
                    _validator.RecordAcceptedSignal(entry.Symbol, pending.TimestampMsUtc);
                }
                _logger.LogInformation("[Shadow] Signal accepted for {Symbol} ({Direction}) Score={Score}",
                    entry.Symbol, entry.Direction, entry.DecisionInputs?.Score);
            }
            else
            {
                entry.DecisionOutcome = "Rejected";
                entry.RejectionReason = FormatRejectionReason(ranked.Decision);
                entry.DecisionTrace = BuildDecisionTraceForRejection("ScarcityReject", entry.RejectionReason);
                entry.DecisionResult = UpdateDecisionResult(
                    entry.DecisionResult,
                    DecisionOutcome.Rejected,
                    BuildHardRejectReasonsFromScarcity(ranked.Decision));

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

    private void TryLogGatingRejection(
        string symbol,
        string reason,
        StrategyDecisionResult? decisionResult = null,
        ShadowTradingHelpers.TapeStatus? tapeStatus = null)
    {
        if (!_rejectionLogger.ShouldLog(
                symbol,
                _subscriptionManager.IsFocusSymbol(symbol),
                reason,
                tapeStatus?.Kind))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (IsNotReadyRejection(reason) && !_gatingRejectionThrottle.ShouldLog(symbol, reason, now))
        {
            return;
        }
        var nowMs = now.ToUnixTimeMilliseconds();
        var book = _metrics.GetOrderBookSnapshot(symbol);
        var depthSnapshot = book is null ? null : BuildDepthSnapshot(book);
        TapeStats? tapeStats = book is null ? null : BuildTapeStats(book, nowMs, null);
        var resolvedTapeStatus = tapeStatus ?? (book is null
            ? default
            : ShadowTradingHelpers.GetTapeStatus(book, nowMs, _subscriptionManager.IsTapeEnabled(symbol), _tapeGateConfig));

        var entry = new ShadowTradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = Guid.NewGuid(),
            SessionId = _sessionId,
            Source = "IBKR",
            EntryType = "Rejection",
            MarketTimestampUtc = now,
            DecisionTimestampUtc = now,
            TradingMode = _tradingMode,
            Symbol = symbol,
            DecisionOutcome = "Rejected",
            RejectionReason = reason,
            DecisionTrace = new List<string> { $"GateReject:{reason}" },
            DataQualityFlags = book is null || depthSnapshot is null || tapeStats is null
                ? new List<string> { "MissingBookContext" }
                : BuildDataQualityFlags(book, depthSnapshot, tapeStats.Value, resolvedTapeStatus, nowMs),
            ObservedMetrics = book is null || depthSnapshot is null || tapeStats is null
                ? null
                : new ShadowTradeJournalEntry.ObservedMetricsSnapshot
                {
                    Spread = book.Spread,
                    BestBidPrice = book.BestBid,
                    BestAskPrice = book.BestAsk,
                    BestBidSize = depthSnapshot.BestBidSize,
                    BestAskSize = depthSnapshot.BestAskSize,
                    TotalBidSizeTopN = depthSnapshot.TotalBidSizeTopN,
                    TotalAskSizeTopN = depthSnapshot.TotalAskSizeTopN,
                    BidAskRatioTopN = depthSnapshot.BidAskRatioTopN,
                    LastDepthUpdateAgeMs = depthSnapshot.LastDepthUpdateAgeMs,
                    LastTapeUpdateAgeMs = tapeStats.Value.LastTapeAgeMs,
                    LastPrice = tapeStats.Value.LastPrice,
                    VwapPrice = tapeStats.Value.VwapPrice,
                    TapeVelocity3Sec = tapeStats.Value.Velocity,
                    TapeVolume3Sec = tapeStats.Value.Volume,
                    BidsTopN = depthSnapshot.BidsTopN,
                    AsksTopN = depthSnapshot.AsksTopN
                },
            // Gate rejection: no decision inputs computed since signal never reached scoring/ranking
            DecisionInputs = null,
            DecisionResult = decisionResult,
            GateTrace = _emitGateTrace ? BuildGateTrace(book, nowMs, resolvedTapeStatus, depthSnapshot) : null
        };

        EnqueueEntry(entry);
    }

    private static bool IsNotReadyRejection(string reason) =>
        reason.StartsWith("NotReady_", StringComparison.OrdinalIgnoreCase);

    private static string FormatRejectionReason(ScarcityDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.ReasonDetail))
        {
            return decision.ReasonCode;
        }

        return $"{decision.ReasonCode}:{decision.ReasonDetail}";
    }

    public readonly record struct TapeStats(
        decimal Velocity,
        decimal Volume,
        decimal? LastPrice,
        decimal? VwapPrice,
        long? LastTapeAgeMs,
        decimal? CumulativeVwap = null,
        bool VwapReclaimDetected = false);

    private sealed record PendingRankEntry(
        ShadowTradeJournalEntry Entry,
        BlueprintPlan Blueprint,
        long TimestampMsUtc,
        decimal VwapBonus);

    private sealed record BlueprintPlan(
        bool Success,
        decimal? Entry,
        decimal? Stop,
        decimal? Target,
        string? RejectionReason);

    private static TapeStats BuildTapeStats(OrderBookState book, long nowMs, string? direction)
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

        var cumulativeVwap = book.VwapTracker.CurrentVwap;
        var vwapReclaimDetected = IsVwapReclaim(
            direction,
            lastPrice,
            cumulativeVwap,
            vwap,
            volume);

        return new TapeStats(velocity, volume, lastPrice, vwap, lastTapeAgeMs, cumulativeVwap, vwapReclaimDetected);
    }

    private sealed class RejectionLogger
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
        private readonly Queue<DateTimeOffset> _timestamps = new();
        private readonly ConcurrentDictionary<string, GateRejectState> _lastGateReject = new(StringComparer.OrdinalIgnoreCase);
        private TimeSpan _minInterval = TimeSpan.Zero;
        private const int RateLimit = 5;

        public RejectionLogger()
        {
        }

        public RejectionLogger(TimeSpan minInterval)
        {
            _minInterval = minInterval;
        }

        public bool ShouldLog(
            string symbol,
            bool isFocus,
            string reason,
            ShadowTradingHelpers.TapeStatusKind? tapeStatusKind)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastGateReject.TryGetValue(symbol, out var state))
            {
                if (state.IsSame(reason, tapeStatusKind) && now - state.LastLoggedAt < _minInterval)
                {
                    return false;
                }
            }

            if (!isFocus)
            {
                while (_timestamps.Count > 0 && now - _timestamps.Peek() >= Window)
                {
                    _timestamps.Dequeue();
                }

                if (_timestamps.Count >= RateLimit)
                {
                    return false;
                }

                _timestamps.Enqueue(now);
            }

            _lastGateReject[symbol] = new GateRejectState(now, reason, tapeStatusKind);
            return true;
        }

        private readonly record struct GateRejectState(
            DateTimeOffset LastLoggedAt,
            string Reason,
            ShadowTradingHelpers.TapeStatusKind? TapeStatusKind)
        {
            public bool IsSame(string reason, ShadowTradingHelpers.TapeStatusKind? tapeStatusKind)
            {
                return string.Equals(Reason, reason, StringComparison.OrdinalIgnoreCase)
                       && TapeStatusKind == tapeStatusKind;
            }
        }
    }

    private sealed class GatingRejectionThrottle
    {
        private readonly TimeSpan _interval;
        private readonly Dictionary<string, GatingRejectionState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public GatingRejectionThrottle(TimeSpan interval)
        {
            _interval = interval;
        }

        public bool ShouldLog(string symbol, string reason, DateTimeOffset now)
        {
            if (_interval <= TimeSpan.Zero)
            {
                return true;
            }

            lock (_lock)
            {
                if (!_states.TryGetValue(symbol, out var state))
                {
                    _states[symbol] = new GatingRejectionState(reason, now);
                    return true;
                }

                if (!string.Equals(state.Reason, reason, StringComparison.OrdinalIgnoreCase) ||
                    now - state.Timestamp >= _interval)
                {
                    _states[symbol] = new GatingRejectionState(reason, now);
                    return true;
                }

                return false;
            }
        }

        private sealed record GatingRejectionState(string Reason, DateTimeOffset Timestamp);
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
        public int ExpectedDepthLevels { get; init; }
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
            LastDepthUpdateAgeMs = depthAge,
            ExpectedDepthLevels = depthLevels
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
