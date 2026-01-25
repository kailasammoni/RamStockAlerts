using RamStockAlerts.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace RamStockAlerts.Engine;

/// <summary>
/// Order flow signal validator using measured order-book behavior
/// 
/// Signals only when:
/// - QueueImbalance ≥ 2.8 (or ≤ 0.35 for sell)
/// - WallPersistence ≥ 1000ms
/// - AbsorptionRate > threshold
/// - SpoofScore < 0.3 (likely not spoofing)
/// - TapeAcceleration ≥ 2.0
/// 
/// Safety: Symbol cooldown 10 min, max 3 alerts/hour
/// </summary>
public class OrderFlowSignalValidator
{
    private readonly ILogger<OrderFlowSignalValidator> _logger;
    private readonly OrderFlowMetrics _metrics;
    private readonly HardGateConfig _hardGateConfig;
    
    // Cooldown tracking: symbol -> last signal time
    private readonly ConcurrentDictionary<string, long> _lastSignalMs = new();
    
    // Alert rate limiting
    private readonly Queue<long> _alertTimestamps = new();
    private readonly object _alertLock = new();
    
    private const long SYMBOL_COOLDOWN_MS = 10 * 60 * 1000;    // 10 minutes
    private const int MAX_ALERTS_PER_HOUR = 3;
    private const long HOUR_MS = 60 * 60 * 1000;
    
    public sealed class HardGateConfig
    {
        public decimal MaxSpoofScore { get; set; } = 0.3m;
        public decimal MinTapeAcceleration { get; set; } = 2.0m;
        public long MinWallPersistenceMs { get; set; } = 1000;
    }

    public sealed record HardGateResult(bool Passed, string? FailedGate, string? Details);

    public class OrderFlowSignal
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty; // "BUY" or "SELL"
        public long TimestampMs { get; set; }
        
        // Trigger metrics
        public decimal QueueImbalance { get; set; }
        public long WallPersistenceMs { get; set; }
        public decimal AbsorptionRate { get; set; }
        public decimal SpoofScore { get; set; }
        public decimal TapeAcceleration { get; set; }
        
        // Confidence score (0-100)
        public int Confidence { get; set; }
        
        public override string ToString()
        {
            return $"[{Direction}] {Symbol} @ {TimestampMs}: QI={QueueImbalance:F2} Wall={WallPersistenceMs}ms " +
                   $"Absorption={AbsorptionRate:F1} SpoofScore={SpoofScore:F2} TapeAccel={TapeAcceleration:F1}x " +
                   $"Confidence={Confidence}%";
        }
    }

    public sealed record OrderFlowSignalDecision(
        bool HasCandidate,
        bool Accepted,
        string? RejectionReason,
        OrderFlowSignal? Signal,
        OrderFlowMetrics.MetricSnapshot? Snapshot,
        string? Direction);
    
    public OrderFlowSignalValidator(
        ILogger<OrderFlowSignalValidator> logger,
        OrderFlowMetrics metrics,
        IConfiguration configuration)
    {
        _logger = logger;
        _metrics = metrics;
        var gateConfig = new HardGateConfig();
        configuration.GetSection("Signals:HardGates").Bind(gateConfig);
        _hardGateConfig = gateConfig;
    }
    
    /// <summary>
    /// Validate and generate order flow signal if criteria met
    /// </summary>
    public OrderFlowSignal? ValidateSignal(OrderBookState book, long currentTimeMs)
    {
        var decision = EvaluateDecision(book, currentTimeMs);
        if (!decision.HasCandidate || !decision.Accepted || decision.Signal == null)
        {
            return null;
        }

        RecordAcceptedSignal(decision.Signal.Symbol, currentTimeMs);
        _logger.LogWarning("[OrderFlow Signal] {Signal}", decision.Signal);
        return decision.Signal;
    }

    public OrderFlowSignalDecision EvaluateDecision(OrderBookState book, long currentTimeMs)
    {
        var snapshot = _metrics.GetLatestSnapshot(book.Symbol);
        if (snapshot == null)
        {
            return new OrderFlowSignalDecision(false, false, null, null, null, null);
        }

        var isBuyCandidate = _metrics.IsBuyLiquidityFailure(snapshot);
        var isSellCandidate = !isBuyCandidate && _metrics.IsSellLiquidityFailure(snapshot);

        if (!isBuyCandidate && !isSellCandidate)
        {
            return new OrderFlowSignalDecision(false, false, null, null, snapshot, null);
        }

        var direction = isBuyCandidate ? "BUY" : "SELL";
        var signal = BuildSignal(snapshot, currentTimeMs, isBuyCandidate);

        var hardGateResult = CheckHardGates(snapshot, isBuyCandidate);
        if (!hardGateResult.Passed)
        {
            _logger.LogInformation(
                "[HardGate] {Symbol} rejected: {Gate} - {Details}",
                snapshot.Symbol,
                hardGateResult.FailedGate,
                hardGateResult.Details);
            return new OrderFlowSignalDecision(
                true,
                false,
                $"HardGate:{hardGateResult.FailedGate}",
                signal,
                snapshot,
                direction);
        }

        if (IsInCooldown(snapshot.Symbol, currentTimeMs))
        {
            return new OrderFlowSignalDecision(true, false, "CooldownActive", signal, snapshot, direction);
        }

        if (!CanIssueAlert(currentTimeMs))
        {
            _logger.LogDebug("[OrderFlow] Rate limit reached. Max {Max} alerts per hour.", MAX_ALERTS_PER_HOUR);
            return new OrderFlowSignalDecision(true, false, "GlobalRateLimit", signal, snapshot, direction);
        }

        return new OrderFlowSignalDecision(true, true, null, signal, snapshot, direction);
    }
    
    private OrderFlowSignal GenerateBuySignal(OrderFlowMetrics.MetricSnapshot snapshot, long currentTimeMs)
    {
        var signal = BuildSignal(snapshot, currentTimeMs, isBuy: true);
        RecordAcceptedSignal(snapshot.Symbol, currentTimeMs);
        _logger.LogWarning("[OrderFlow Signal] {Signal}", signal);
        return signal;
    }
    
    private OrderFlowSignal GenerateSellSignal(OrderFlowMetrics.MetricSnapshot snapshot, long currentTimeMs)
    {
        var signal = BuildSignal(snapshot, currentTimeMs, isBuy: false);
        RecordAcceptedSignal(snapshot.Symbol, currentTimeMs);
        _logger.LogWarning("[OrderFlow Signal] {Signal}", signal);
        return signal;
    }

    private OrderFlowSignal BuildSignal(OrderFlowMetrics.MetricSnapshot snapshot, long currentTimeMs, bool isBuy)
    {
        var confidence = ComputeConfidence(snapshot, isBuy);

        return new OrderFlowSignal
        {
            Symbol = snapshot.Symbol,
            Direction = isBuy ? "BUY" : "SELL",
            TimestampMs = currentTimeMs,
            QueueImbalance = snapshot.QueueImbalance,
            WallPersistenceMs = isBuy ? snapshot.BidWallAgeMs : snapshot.AskWallAgeMs,
            AbsorptionRate = isBuy ? snapshot.BidAbsorptionRate : snapshot.AskAbsorptionRate,
            SpoofScore = snapshot.SpoofScore,
            TapeAcceleration = snapshot.TapeAcceleration,
            Confidence = confidence
        };
    }

    public HardGateResult CheckHardGates(OrderFlowMetrics.MetricSnapshot snapshot, bool isBuy)
    {
        var wallAge = isBuy ? snapshot.BidWallAgeMs : snapshot.AskWallAgeMs;

        if (snapshot.SpoofScore >= _hardGateConfig.MaxSpoofScore)
        {
            return new HardGateResult(
                false,
                "SpoofScore",
                $"SpoofScore={snapshot.SpoofScore:F2} >= {_hardGateConfig.MaxSpoofScore}");
        }

        if (snapshot.TapeAcceleration < _hardGateConfig.MinTapeAcceleration)
        {
            return new HardGateResult(
                false,
                "TapeAcceleration",
                $"TapeAccel={snapshot.TapeAcceleration:F1} < {_hardGateConfig.MinTapeAcceleration}");
        }

        if (wallAge < _hardGateConfig.MinWallPersistenceMs)
        {
            return new HardGateResult(
                false,
                "WallPersistence",
                $"WallAge={wallAge}ms < {_hardGateConfig.MinWallPersistenceMs}ms");
        }

        return new HardGateResult(true, null, null);
    }

    public void RecordAcceptedSignal(string symbol, long currentTimeMs)
    {
        _lastSignalMs[symbol] = currentTimeMs;
        RecordAlert(currentTimeMs);
    }
    
    private int ComputeConfidence(OrderFlowMetrics.MetricSnapshot snapshot, bool isBuy)
    {
        int confidence = 50; // Base
        
        // Queue imbalance strength
        var qiScore = isBuy
            ? Math.Min(100, (int)((snapshot.QueueImbalance - 2.8m) * 50)) // Scale 2.8+ to 50-100
            : Math.Min(100, (int)((0.35m - snapshot.QueueImbalance) * 50));
        
        confidence += Math.Max(0, qiScore);
        
        // Wall persistence (longer = stronger)
        var wallScore = Math.Min(30, (int)(snapshot.BidWallAgeMs / 100));
        confidence += wallScore;
        
        // Tape acceleration (higher = more conviction)
        var tapeScore = Math.Min(20, (int)((snapshot.TapeAcceleration - 1.0m) * 10));
        confidence += Math.Max(0, tapeScore);
        
        // Spoof penalty (low score = likely real)
        if (snapshot.SpoofScore < 0.3m)
            confidence += 10;
        else if (snapshot.SpoofScore > 0.7m)
            confidence -= 20;
        
        return Math.Min(100, confidence);
    }
    
    private bool IsInCooldown(string symbol, long currentTimeMs)
    {
        if (!_lastSignalMs.TryGetValue(symbol, out var lastSignalMs))
        {
            return false;
        }
        
        var elapsed = currentTimeMs - lastSignalMs;
        if (elapsed < SYMBOL_COOLDOWN_MS)
        {
            _logger.LogDebug("[OrderFlow] {Symbol} in cooldown. {Remaining}ms remaining",
                symbol, SYMBOL_COOLDOWN_MS - elapsed);
            return true;
        }
        
        return false;
    }
    
    private bool CanIssueAlert(long currentTimeMs)
    {
        lock (_alertLock)
        {
            var hourAgo = currentTimeMs - HOUR_MS;
            
            // Remove old timestamps outside the window
            while (_alertTimestamps.Count > 0 && _alertTimestamps.Peek() < hourAgo)
            {
                _alertTimestamps.Dequeue();
            }
            
            if (_alertTimestamps.Count >= MAX_ALERTS_PER_HOUR)
            {
                return false;
            }
            
            return true;
        }
    }
    
    private void RecordAlert(long currentTimeMs)
    {
        lock (_alertLock)
        {
            _alertTimestamps.Enqueue(currentTimeMs);
        }
    }
    
    public int GetAlertCountInLastHour(long currentTimeMs)
    {
        lock (_alertLock)
        {
            var hourAgo = currentTimeMs - HOUR_MS;
            
            while (_alertTimestamps.Count > 0 && _alertTimestamps.Peek() < hourAgo)
            {
                _alertTimestamps.Dequeue();
            }
            
            return _alertTimestamps.Count;
        }
    }

    public double GetCooldownRemainingSeconds(string symbol, long currentTimeMs)
    {
        if (!_lastSignalMs.TryGetValue(symbol, out var lastSignalMs))
        {
            return 0;
        }

        var remainingMs = SYMBOL_COOLDOWN_MS - (currentTimeMs - lastSignalMs);
        return remainingMs > 0 ? remainingMs / 1000.0 : 0;
    }
    
    public void ResetSymbolCooldown(string symbol)
    {
        _lastSignalMs.TryRemove(symbol, out _);
        _logger.LogInformation("[OrderFlow] Reset cooldown for {Symbol}", symbol);
    }
}
