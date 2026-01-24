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
    private const int SignalEvaluationThrottleMs = 250; // Don't evaluate more than once per 250ms per symbol
    private const int TapeStaleWarningThrottleSec = 30; // Log tape stale warning at most once per 30s per symbol
    
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
    private readonly ConcurrentDictionary<string, long> _lastEvaluationMs = new(StringComparer.OrdinalIgnoreCase); // Evaluation throttle
    private readonly ConcurrentDictionary<string, long> _lastTapeStaleWarnMs = new(StringComparer.OrdinalIgnoreCase); // Rate-limited warnings
    private readonly Dictionary<Guid, PendingRankEntry> _pendingRankedEntries = new();
    private readonly ShadowTradingHelpers.TapeGateConfig _tapeGateConfig;
    private readonly bool _emitGateTrace;
    private readonly ConcurrentDictionary<string, long> _lastInactiveSymbolLogMs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan InactiveSymbolLogThrottle = TimeSpan.FromMinutes(5);
    private readonly GateRejectionCounter _rejectionCounter = new();
    private readonly System.Threading.Timer _summaryTimer;
    
    // Phase 3.1: Post-Signal Quality Monitoring
    private readonly ConcurrentDictionary<string, AcceptedSignalTracker> _acceptedSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _postSignalMonitoringEnabled;
    private readonly double _tapeSlowdownThreshold; // 50% = 0.5
    private readonly double _spreadBlowoutThreshold; // 50% = 0.5
    
    // Phase 3.2: Tape Warm-up Watchlist
    private readonly ConcurrentDictionary<string, TapeWarmupWatchlistEntry> _tapeWarmupWatchlist = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _tapeWatchlistEnabled;
    private readonly long _tapeWatchlistRecheckIntervalMs; // 5000ms = 5 sec
    private readonly System.Threading.Timer _watchlistTimer;
    private DateTimeOffset _lastSnapshotProcessedUtc = DateTimeOffset.MinValue;
    public DateTimeOffset? LastSnapshotProcessedUtc => _lastSnapshotProcessedUtc == DateTimeOffset.MinValue ? null : _lastSnapshotProcessedUtc;

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
        var gatingRejectSuppressionSeconds = configuration.GetValue<double?>("ShadowTrading:GatingRejectSuppressionSeconds")
            ?? configuration.GetValue("ShadowTrading:GatingRejectDedupeSeconds", 10d);
        _gatingRejectionThrottle = new GatingRejectionThrottle(
            TimeSpan.FromSeconds(Math.Max(0d, gatingRejectSuppressionSeconds)));
        var gateRejectMinIntervalMs = configuration.GetValue("MarketData:GateRejectLogMinIntervalMs", 2000);
        _rejectionLogger = new RejectionLogger(TimeSpan.FromMilliseconds(Math.Max(0, gateRejectMinIntervalMs)));
        _emitGateTrace = configuration.GetValue("ShadowTradeJournal:EmitGateTrace", true);
        
        // Phase 3.1: Post-Signal Quality Monitoring configuration
        _postSignalMonitoringEnabled = configuration.GetValue("ShadowTrading:PostSignalMonitoringEnabled", true);
        _tapeSlowdownThreshold = configuration.GetValue("ShadowTrading:TapeSlowdownThreshold", 0.5); // 50%
        _spreadBlowoutThreshold = configuration.GetValue("ShadowTrading:SpreadBlowoutThreshold", 0.5); // 50%
        
        // Phase 3.2: Tape Warm-up Watchlist configuration
        _tapeWatchlistEnabled = configuration.GetValue("ShadowTrading:TapeWatchlistEnabled", true);
        _tapeWatchlistRecheckIntervalMs = configuration.GetValue("ShadowTrading:TapeWatchlistRecheckIntervalMs", 5000L); // 5 sec
        
        // Start periodic rejection summary timer (every 60s)
        _summaryTimer = new System.Threading.Timer(
            _ => LogRejectionSummaries(),
            null,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60));
        
        // Phase 3.2: Start tape watchlist re-check timer (every 5s default)
        _watchlistTimer = new System.Threading.Timer(
            _ => RecheckTapeWarmupWatchlist(),
            null,
            TimeSpan.FromMilliseconds(_tapeWatchlistRecheckIntervalMs),
            TimeSpan.FromMilliseconds(_tapeWatchlistRecheckIntervalMs));

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

        // ActiveUniverse gate: Skip symbols not in Active Universe
        // This prevents gate rejections and unnecessary processing for inactive symbols
        if (!_subscriptionManager.IsActiveSymbol(book.Symbol))
        {
            LogInactiveSymbolSkipThrottled(book.Symbol, nowMs);
            return;
        }

        _lastSnapshotProcessedUtc = DateTimeOffset.UtcNow;

        // Phase 3.1: Monitor post-signal quality for accepted signals
        MonitorPostSignalQuality(book, nowMs);

        // Evaluation throttle: Prevent signal spam by limiting evaluations per symbol
        if (_lastEvaluationMs.TryGetValue(book.Symbol, out var lastEval))
        {
            if (nowMs - lastEval < SignalEvaluationThrottleMs)
            {
                return; // Skip, too soon since last evaluation
            }
        }
        _lastEvaluationMs[book.Symbol] = nowMs;

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
            // Log detailed staleness info when tape gate blocks - but rate-limited to prevent spam
            if (tapeStatusReadyCheck.Kind == ShadowTradingHelpers.TapeStatusKind.Stale)
            {
                if (_lastTapeStaleWarnMs.TryGetValue(book.Symbol, out var lastWarnMs))
                {
                    if (nowMs - lastWarnMs < TapeStaleWarningThrottleSec * 1000)
                    {
                        // Skip this warning, too soon since last one
                    }
                    else
                    {
                        _lastTapeStaleWarnMs[book.Symbol] = nowMs;
                        LogTapeStaleWarning(book, nowMs, tapeStatusReadyCheck);
                    }
                }
                else
                {
                    _lastTapeStaleWarnMs[book.Symbol] = nowMs;
                    LogTapeStaleWarning(book, nowMs, tapeStatusReadyCheck);
                }
            }
            
            var decisionResult = BuildNotReadyDecisionResult(
                book,
                nowMs,
                MapReason(tapeRejectionReason),
                tapeStatusReadyCheck);
            TryLogGatingRejection(book.Symbol, tapeRejectionReason, decisionResult, tapeStatusReadyCheck);
            
            // Phase 3.2: Add to watchlist if TapeNotWarmedUp
            if (tapeRejectionReason == "NotReady_TapeNotWarmedUp")
            {
                AddToTapeWarmupWatchlist(book.Symbol, nowMs);
            }
            
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
        _logger.LogDebug(
            "[SignalEval] {Symbol} candidate={HasCandidate} accepted={Accepted} reason={Reason} cooldownRemaining={Cooldown:F1}s hourlyAlerts={Hourly}",
            book.Symbol,
            decision.HasCandidate,
            decision.Accepted,
            decision.RejectionReason ?? "None",
            _validator.GetCooldownRemainingSeconds(book.Symbol, nowMs),
            _validator.GetAlertCountInLastHour(nowMs));

        if (decision.Signal != null)
        {
            _logger.LogDebug("[SignalEval] Metrics for {Symbol}: {SignalDetails}", decision.Signal.Symbol, decision.Signal);
        }

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
            BidTradesIn3Sec = snapshot.BidTradesIn3Sec,
            AskTradesIn3Sec = snapshot.AskTradesIn3Sec,
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
            BidTradesIn3Sec = snapshot.BidTradesIn3Sec,
            AskTradesIn3Sec = snapshot.AskTradesIn3Sec,
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
            BidTradesIn3Sec = snapshot.BidTradesIn3Sec,
            AskTradesIn3Sec = snapshot.AskTradesIn3Sec,
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
                    
                    // Phase 3.1: Track accepted signal for post-signal quality monitoring
                    var baselineSpread = entry.ObservedMetrics?.Spread ?? 0m;
                    
                    // Side-aware baseline velocity
                    int baselineSideVelocity = 0;
                    int baselineOppositeVelocity = 0;
                    if (string.Equals(entry.Direction, "BUY", StringComparison.OrdinalIgnoreCase))
                    {
                        baselineSideVelocity = entry.ObservedMetrics?.AskTradesIn3Sec ?? 0;
                        baselineOppositeVelocity = entry.ObservedMetrics?.BidTradesIn3Sec ?? 0;
                    }
                    else if (string.Equals(entry.Direction, "SELL", StringComparison.OrdinalIgnoreCase))
                    {
                        baselineSideVelocity = entry.ObservedMetrics?.BidTradesIn3Sec ?? 0;
                        baselineOppositeVelocity = entry.ObservedMetrics?.AskTradesIn3Sec ?? 0;
                    }

                    TrackAcceptedSignal(entry.Symbol, entry.Direction ?? "Unknown", entry.DecisionId, baselineSpread, baselineSideVelocity, baselineOppositeVelocity, pending.TimestampMsUtc);
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
        
        // Always count rejections internally even if we don't log
        _rejectionCounter.Increment(symbol, reason);
        
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

        // Log structured tape gate rejection details for tape-related rejections
        LogGateRejectionDetails(symbol, reason, resolvedTapeStatus, now);

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

    /// <summary>
    /// Emits periodic summary of gate rejections per symbol with diagnostic info.
    /// Runs every 60s, resets counters after logging.
    /// </summary>
    private void LogRejectionSummaries()
    {
        try
        {
            var counts = _rejectionCounter.GetAndResetCounts();
            if (counts.Count == 0)
            {
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            foreach (var (symbol, reasonCounts) in counts)
            {
                var book = _metrics.GetOrderBookSnapshot(symbol);
                if (book == null)
                {
                    continue;
                }
                
                var tapeStatus = ShadowTradingHelpers.GetTapeStatus(
                    book, 
                    nowMs, 
                    _subscriptionManager.IsTapeEnabled(symbol), 
                    _tapeGateConfig);
                
                var notWarmedUpCount = reasonCounts.GetValueOrDefault("NotReady_TapeNotWarmedUp", 0);
                var staleCount = reasonCounts.GetValueOrDefault("NotReady_TapeStale", 0);
                var otherCount = reasonCounts.Where(kv => 
                    kv.Key != "NotReady_TapeNotWarmedUp" && 
                    kv.Key != "NotReady_TapeStale").Sum(kv => kv.Value);
                
                _logger.LogInformation(
                    "[GateRejectionSummary] symbol={Symbol} NotWarmedUpCount={NotWarmedUp} StaleCount={Stale} OtherCount={Other} " +
                    "lastTapeRecvAgeMs={TapeAge} tradesInWarmup={Trades} warmedUp={WarmedUp} lastDepthRecvAgeMs={DepthAge}",
                    symbol,
                    notWarmedUpCount,
                    staleCount,
                    otherCount,
                    tapeStatus.AgeMs ?? -1,
                    tapeStatus.TradesInWarmupWindow,
                    tapeStatus.Kind == ShadowTradingHelpers.TapeStatusKind.Ready,
                    book.LastDepthRecvMs.HasValue ? nowMs - book.LastDepthRecvMs.Value : -1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GateRejectionSummary] Failed to log rejection summaries");
        }
    }

    /// <summary>
    /// Logs structured details of gate rejections, with throttling to prevent spam.
    /// For tape-related rejections, logs: Kind, AgeMs, TradesInWarmupWindow, WarmupMinTrades, WarmupWindowMs.
    /// Throttled via _gatingRejectionThrottle per symbol+reason to allow visibility without spam.
    /// </summary>
    private void LogGateRejectionDetails(
        string symbol,
        string reason,
        ShadowTradingHelpers.TapeStatus tapeStatus,
        DateTimeOffset now)
    {
        // Only log tape-related rejections with structured details
        if (!reason.Contains("Tape", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.LogInformation(
            "[GateRejection] {Symbol} rejected by {Reason}: TapeStatus={{Kind={Kind}, AgeMs={AgeMs}, TradesInWarmupWindow={TradesInWarmupWindow}, WarmupMinTrades={WarmupMinTrades}, WarmupWindowMs={WarmupWindowMs}}}",
            symbol,
            reason,
            tapeStatus.Kind,
            tapeStatus.AgeMs ?? -1,
            tapeStatus.TradesInWarmupWindow,
            tapeStatus.WarmupMinTrades,
            tapeStatus.WarmupWindowMs);
    }

    private void LogInactiveSymbolSkipThrottled(string symbol, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        var throttleMs = (long)InactiveSymbolLogThrottle.TotalMilliseconds;

        if (_lastInactiveSymbolLogMs.TryGetValue(normalized, out var lastLogMs) && 
            nowMs - lastLogMs < throttleMs)
        {
            return; // Still within throttle window
        }

        _lastInactiveSymbolLogMs[normalized] = nowMs;
        _logger.LogDebug("[Shadow] Skipping snapshot for inactive symbol={Symbol}", symbol);
    }

    private void LogTapeStaleWarning(OrderBookState book, long nowMs, ShadowTradingHelpers.TapeStatus status)
    {
        // Use receipt time for staleness reporting, show both event and receipt times for diagnostics
        var lastTapeRecvMs = book.LastTapeRecvMs;
        var lastTrade = book.RecentTrades.LastOrDefault();
        var lastTapeEventMs = lastTrade.EventTimestampMs;
        var lastTapeRecvFromTradeMs = lastTrade.ReceiptTimestampMs;
        var skewMs = lastTapeRecvFromTradeMs - lastTapeEventMs;
        
        _logger.LogWarning(
            "[ShadowTrading GATE] Tape staleness blocking {Symbol}: nowMs={NowMs}, lastTapeRecvMs={LastRecvMs}, lastTapeRecvMs(lastTrade)={LastRecvTradeMs}, lastTapeEventMs={LastEventMs}, skewMs={SkewMs}, ageMs={AgeMs}, staleWindowMs={StaleWindowMs}, timeSource=ReceiptTime",
            book.Symbol, 
            nowMs, 
            lastTapeRecvMs,
            lastTapeRecvFromTradeMs,
            lastTapeEventMs, 
            skewMs,
            status.AgeMs, 
            _tapeGateConfig.StaleWindowMs);
    }

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

    private sealed class GateRejectionCounter
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _counts = new(StringComparer.OrdinalIgnoreCase);
        
        public void Increment(string symbol, string reason)
        {
            var symbolCounts = _counts.GetOrAdd(symbol, _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            symbolCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
        }
        
        public Dictionary<string, Dictionary<string, int>> GetAndResetCounts()
        {
            var snapshot = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbolPair in _counts)
            {
                var reasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var reasonPair in symbolPair.Value)
                {
                    reasonCounts[reasonPair.Key] = reasonPair.Value;
                }
                snapshot[symbolPair.Key] = reasonCounts;
            }
            _counts.Clear();
            return snapshot;
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
    
    // Phase 3.1: Post-Signal Quality Monitoring
    
    /// <summary>
    /// Monitors tape velocity and spread after a signal is accepted.
    /// Detects degradation (tape slowdown >50%, spread blowout >50%) and journals cancellation.
    /// </summary>
    public void MonitorPostSignalQuality(OrderBookState book, long nowMs)
    {
        if (!_postSignalMonitoringEnabled || !_acceptedSignals.TryGetValue(book.Symbol, out var tracker))
        {
            return;
        }

        // Already canceled - no further monitoring
        if (tracker.IsCanceled)
        {
            return;
        }

        // 1. Grace Period check: allow some time for the trade to breathe (3s)
        var monitoringAgeMs = nowMs - tracker.AcceptanceTimestampMs;
        if (monitoringAgeMs < 3000)
        {
            return;
        }

        var currentSpread = book.BestAsk - book.BestBid;
        var snapshot = _metrics.GetLatestSnapshot(book.Symbol);
        if (snapshot == null) return;

        // 2. Side-aware velocity check
        int currentSideVelocity;
        int currentOppositeVelocity;
        bool isBuy = string.Equals(tracker.Direction, "BUY", StringComparison.OrdinalIgnoreCase);

        if (isBuy)
        {
            currentSideVelocity = snapshot.AskTradesIn3Sec;
            currentOppositeVelocity = snapshot.BidTradesIn3Sec;
        }
        else
        {
            currentSideVelocity = snapshot.BidTradesIn3Sec;
            currentOppositeVelocity = snapshot.AskTradesIn3Sec;
        }

        // Slowdown check: Only cancel if side-velocity drops significantly AND opposite side isn't also dead
        decimal velocityFloor = tracker.BaselineSideVelocity * (1m - (decimal)_tapeSlowdownThreshold);
        if (tracker.BaselineSideVelocity > 2 && currentSideVelocity < (int)velocityFloor)
        {
            tracker.ConsecutiveSlowdownCount++;
            if (tracker.ConsecutiveSlowdownCount >= 2)
            {
                CancelSignal(tracker, "TapeSlowdown", (decimal)currentSideVelocity, (decimal)tracker.BaselineSideVelocity, nowMs);
                return;
            }
        }
        else
        {
            tracker.ConsecutiveSlowdownCount = 0;
        }

        // 3. Reversal check: if opposite side velocity dominates heavily (e.g. 5+ prints AND 3x side velocity)
        if (currentOppositeVelocity > 5 && currentOppositeVelocity > currentSideVelocity * 3)
        {
            CancelSignal(tracker, "TapeReversal", (decimal)currentOppositeVelocity, (decimal)currentSideVelocity, nowMs);
            return;
        }

        // 4. Spread blowout check
        if (tracker.BaselineSpread > 0m && currentSpread > tracker.BaselineSpread * (1m + (decimal)_spreadBlowoutThreshold))
        {
            CancelSignal(tracker, "SpreadBlowout", currentSpread, tracker.BaselineSpread, nowMs);
            return;
        }
    }

    private void CancelSignal(AcceptedSignalTracker tracker, string reason, decimal currentValue, decimal baselineValue, long nowMs)
    {
        tracker.IsCanceled = true;
        tracker.CancellationReason = reason;
        tracker.CancellationTimestampMs = nowMs;

        _logger.LogWarning(
            "[Shadow] Post-signal quality degradation: {Symbol} canceled due to {Reason}. Current={Current:F4}, Baseline={Baseline:F4}",
            tracker.Symbol, reason, currentValue, baselineValue);

        // Journal the cancellation
        var cancellationEntry = new ShadowTradeJournalEntry
        {
            SchemaVersion = 2,
            DecisionId = tracker.DecisionId,
            SessionId = _sessionId,
            Symbol = tracker.Symbol,
            Direction = tracker.Direction,
            DecisionOutcome = "Canceled",
            RejectionReason = reason,
            DecisionTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs),
            DecisionInputs = new ShadowTradeJournalEntry.DecisionInputsSnapshot
            {
                Score = 0m // Post-signal cancellation has no score
            },
            ObservedMetrics = new ShadowTradeJournalEntry.ObservedMetricsSnapshot
            {
                Spread = currentValue, // Current degraded value
                QueueImbalance = 0m,
                TapeAcceleration = 0m
            },
            DecisionTrace = new List<string>
            {
                $"PostSignalCancel:{reason}",
                $"Baseline={baselineValue:F4}",
                $"Current={currentValue:F4}",
                $"AcceptedAt={DateTimeOffset.FromUnixTimeMilliseconds(tracker.AcceptanceTimestampMs):O}"
            }
        };

        EnqueueEntry(cancellationEntry);
    }

    /// <summary>
    /// Tracks an accepted signal for post-signal quality monitoring.
    /// </summary>
    private void TrackAcceptedSignal(string symbol, string direction, Guid decisionId, decimal baselineSpread, int baselineSideVelocity, int baselineOppositeVelocity, long nowMs)
    {
        if (!_postSignalMonitoringEnabled)
        {
            return;
        }

        var tracker = new AcceptedSignalTracker
        {
            Symbol = symbol,
            Direction = direction,
            DecisionId = decisionId,
            BaselineSpread = baselineSpread,
            BaselineSideVelocity = baselineSideVelocity,
            BaselineOppositeVelocity = baselineOppositeVelocity,
            AcceptanceTimestampMs = nowMs,
            IsCanceled = false
        };

        _acceptedSignals[symbol] = tracker;
        
        _logger.LogDebug(
            "[Shadow] Tracking accepted signal: {Symbol} {Direction}. BaselineSpread={Spread:F4}, SideVel={SideVel}, OppVel={OppVel}",
            symbol, direction, baselineSpread, baselineSideVelocity, baselineOppositeVelocity);
    }

    /// <summary>
    /// Phase 3.2: Adds a symbol to the tape warmup watchlist with spam prevention.
    /// </summary>
    private void AddToTapeWarmupWatchlist(string symbol, long nowMs)
    {
        if (!_tapeWatchlistEnabled)
        {
            return;
        }

        // Spam prevention: if already exists and checked recently, skip
        if (_tapeWarmupWatchlist.TryGetValue(symbol, out var existing))
        {
            var timeSinceLastCheck = nowMs - existing.LastRecheckMs;
            if (timeSinceLastCheck < _tapeWatchlistRecheckIntervalMs)
            {
                // Too soon since last check, skip
                return;
            }
        }

        // Add or update entry
        var entry = new TapeWarmupWatchlistEntry
        {
            Symbol = symbol,
            FirstRejectionMs = existing?.FirstRejectionMs ?? nowMs,
            LastRecheckMs = nowMs
        };
        _tapeWarmupWatchlist[symbol] = entry;

        _logger.LogDebug(
            "[Shadow] Added {Symbol} to tape warmup watchlist. FirstRejection={FirstRejection}, LastRecheck={LastRecheck}",
            symbol, entry.FirstRejectionMs, entry.LastRecheckMs);
    }

    /// <summary>
    /// Phase 3.2: Periodically re-checks symbols in tape warmup watchlist.
    /// </summary>
    private void RecheckTapeWarmupWatchlist()
    {
        if (!_tapeWatchlistEnabled || _tapeWarmupWatchlist.IsEmpty)
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var symbols = _tapeWarmupWatchlist.Keys.ToArray();

        foreach (var symbol in symbols)
        {
            // Get latest book state
            var book = _metrics.GetOrderBookSnapshot(symbol);
            if (book == null)
            {
                // No book state, remove from watchlist
                _tapeWarmupWatchlist.TryRemove(symbol, out _);
                continue;
            }

            // Check if tape is now warmed up
            var tapeStatus = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, _tapeGateConfig);
            if (tapeStatus.Kind == ShadowTradingHelpers.TapeStatusKind.Ready)
            {
                // Tape is warmed up, trigger re-evaluation
                _logger.LogInformation(
                    "[Shadow] Tape warmup watchlist: {Symbol} tape is now warmed up. Triggering re-evaluation.",
                    symbol);

                // Remove from watchlist
                _tapeWarmupWatchlist.TryRemove(symbol, out _);

                // Trigger ProcessSnapshot to re-evaluate
                ProcessSnapshot(book, nowMs);
            }
            else
            {
                // Still not ready, update last recheck time
                if (_tapeWarmupWatchlist.TryGetValue(symbol, out var entry))
                {
                    entry.LastRecheckMs = nowMs;
                }
            }
        }
    }

    private sealed class AcceptedSignalTracker
    {
        public required string Symbol { get; init; }
        public required string Direction { get; init; }
        public required Guid DecisionId { get; init; }
        public required decimal BaselineSpread { get; init; }
        public required int BaselineSideVelocity { get; init; }
        public required int BaselineOppositeVelocity { get; init; }
        public required long AcceptanceTimestampMs { get; init; }
        public bool IsCanceled { get; set; }
        public string? CancellationReason { get; set; }
        public long CancellationTimestampMs { get; set; }
        public int ConsecutiveSlowdownCount { get; set; }
    }

    /// <summary>
    /// Phase 3.2: Tracks symbols rejected for TapeNotWarmedUp that need periodic re-checks
    /// </summary>
    private sealed class TapeWarmupWatchlistEntry
    {
        public required string Symbol { get; init; }
        public required long FirstRejectionMs { get; init; }
        public long LastRecheckMs { get; set; }
    }
}
