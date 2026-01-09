using RamStockAlerts.Models;
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
    
    // Cooldown tracking: symbol -> last signal time
    private readonly ConcurrentDictionary<string, long> _lastSignalMs = new();
    
    // Alert rate limiting
    private readonly Queue<long> _alertTimestamps = new();
    private readonly object _alertLock = new();
    
    private const long SYMBOL_COOLDOWN_MS = 10 * 60 * 1000;    // 10 minutes
    private const int MAX_ALERTS_PER_HOUR = 3;
    private const long HOUR_MS = 60 * 60 * 1000;
    
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
    
    public OrderFlowSignalValidator(ILogger<OrderFlowSignalValidator> logger, OrderFlowMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;
    }
    
    /// <summary>
    /// Validate and generate order flow signal if criteria met
    /// </summary>
    public OrderFlowSignal? ValidateSignal(OrderBookState book, long currentTimeMs)
    {
        // Check if symbol is in cooldown
        if (IsInCooldown(book.Symbol, currentTimeMs))
        {
            return null;
        }
        
        // Check rate limit
        if (!CanIssueAlert(currentTimeMs))
        {
            _logger.LogDebug("[OrderFlow] Rate limit reached. Max {Max} alerts per hour.", MAX_ALERTS_PER_HOUR);
            return null;
        }
        
        // Get latest metrics
        var snapshot = _metrics.GetLatestSnapshot(book.Symbol);
        if (snapshot == null)
        {
            return null;
        }
        
        // Check buy signal
        if (_metrics.IsBuyLiquidityFailure(snapshot))
        {
            return GenerateBuySignal(snapshot, currentTimeMs);
        }
        
        // Check sell signal
        if (_metrics.IsSellLiquidityFailure(snapshot))
        {
            return GenerateSellSignal(snapshot, currentTimeMs);
        }
        
        return null;
    }
    
    private OrderFlowSignal GenerateBuySignal(OrderFlowMetrics.MetricSnapshot snapshot, long currentTimeMs)
    {
        // Compute confidence based on signal strength
        var confidence = ComputeConfidence(snapshot, isBuy: true);
        
        var signal = new OrderFlowSignal
        {
            Symbol = snapshot.Symbol,
            Direction = "BUY",
            TimestampMs = currentTimeMs,
            QueueImbalance = snapshot.QueueImbalance,
            WallPersistenceMs = snapshot.BidWallAgeMs,
            AbsorptionRate = snapshot.BidAbsorptionRate,
            SpoofScore = snapshot.SpoofScore,
            TapeAcceleration = snapshot.TapeAcceleration,
            Confidence = confidence
        };
        
        _logger.LogWarning("[OrderFlow Signal] {Signal}", signal);
        
        // Record cooldown
        _lastSignalMs[snapshot.Symbol] = currentTimeMs;
        RecordAlert(currentTimeMs);
        
        return signal;
    }
    
    private OrderFlowSignal GenerateSellSignal(OrderFlowMetrics.MetricSnapshot snapshot, long currentTimeMs)
    {
        var confidence = ComputeConfidence(snapshot, isBuy: false);
        
        var signal = new OrderFlowSignal
        {
            Symbol = snapshot.Symbol,
            Direction = "SELL",
            TimestampMs = currentTimeMs,
            QueueImbalance = snapshot.QueueImbalance,
            WallPersistenceMs = snapshot.AskWallAgeMs,
            AbsorptionRate = snapshot.AskAbsorptionRate,
            SpoofScore = snapshot.SpoofScore,
            TapeAcceleration = snapshot.TapeAcceleration,
            Confidence = confidence
        };
        
        _logger.LogWarning("[OrderFlow Signal] {Signal}", signal);
        
        _lastSignalMs[snapshot.Symbol] = currentTimeMs;
        RecordAlert(currentTimeMs);
        
        return signal;
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
    
    public void ResetSymbolCooldown(string symbol)
    {
        _lastSignalMs.TryRemove(symbol, out _);
        _logger.LogInformation("[OrderFlow] Reset cooldown for {Symbol}", symbol);
    }
}
