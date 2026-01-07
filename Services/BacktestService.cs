using System.Text.Json;
using RamStockAlerts.Data;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Backtest service for replaying historical events and validating signals.
/// </summary>
public class BacktestService
{
    private readonly IEventStore _eventStore;
    private readonly ILogger<BacktestService> _logger;

    public BacktestService(IEventStore eventStore, ILogger<BacktestService> logger)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Replay events from the event store within a time range.
    /// </summary>
    public async Task<BacktestResult> ReplayAsync(
        DateTime startTime, 
        DateTime endTime, 
        double speedMultiplier = 1.0,
        CancellationToken cancellationToken = default)
    {
        var result = new BacktestResult
        {
            StartTime = startTime,
            EndTime = endTime,
            SpeedMultiplier = speedMultiplier
        };

        var events = new List<(string EventType, string Data, DateTime RecordedAt)>();
        
        await foreach (var evt in _eventStore.ReplayAsync(startTime, cancellationToken))
        {
            if (evt.RecordedAt > endTime)
                break;

            events.Add(evt);
            result.TotalEvents++;

            // Process different event types
            if (evt.EventType == "TradeSignalGenerated")
            {
                result.SignalsGenerated++;
            }
            else if (evt.EventType == "AlertSent")
            {
                result.AlertsSent++;
            }
        }

        result.Events = events;
        result.CompletedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Backtest completed: {Events} events, {Signals} signals, {Alerts} alerts from {Start} to {End}",
            result.TotalEvents, result.SignalsGenerated, result.AlertsSent, startTime, endTime);

        return result;
    }

    /// <summary>
    /// Calculate backtest performance metrics including expectancy analysis.
    /// </summary>
    public BacktestMetrics CalculateMetrics(BacktestResult result, List<TradeSignal> signals)
    {
        var metrics = new BacktestMetrics();

        if (!signals.Any())
            return metrics;

        var completedSignals = signals.Where(s => s.PnL.HasValue).ToList();
        
        if (completedSignals.Any())
        {
            // Win/Loss counts
            var winners = completedSignals.Where(s => s.PnL > 0).ToList();
            var losers = completedSignals.Where(s => s.PnL <= 0).ToList();
            
            metrics.WinRate = (decimal)winners.Count / completedSignals.Count * 100;
            metrics.TotalSignals = completedSignals.Count;
            metrics.Winners = winners.Count;
            metrics.Losers = losers.Count;

            // PnL calculations
            var totalPnL = completedSignals.Sum(s => s.PnL ?? 0);
            metrics.TotalPnL = totalPnL;
            metrics.AveragePnL = totalPnL / completedSignals.Count;

            // Expectancy metrics
            if (winners.Any())
            {
                metrics.AvgWin = winners.Average(s => s.PnL ?? 0);
            }

            if (losers.Any())
            {
                metrics.AvgLoss = losers.Average(s => s.PnL ?? 0);
            }

            // Drawdown calculation (max peak-to-trough decline)
            decimal runningPnL = 0;
            decimal peak = 0;
            decimal maxDrawdown = 0;

            foreach (var signal in completedSignals.OrderBy(s => s.Timestamp))
            {
                runningPnL += signal.PnL ?? 0;
                peak = Math.Max(peak, runningPnL);
                var drawdown = peak - runningPnL;
                maxDrawdown = Math.Max(maxDrawdown, drawdown);
            }

            metrics.Drawdown = maxDrawdown;

            // Flag dataset if metrics below thresholds
            if (metrics.WinRate < 62m)
            {
                metrics.DatasetFlags.Add($"Low win rate: {metrics.WinRate:F2}% (target >= 62%)");
                _logger.LogWarning("Backtest dataset flagged: Win rate {WinRate:F2}% is below 62% threshold", metrics.WinRate);
            }

            if (metrics.AvgLoss > -0.3m)
            {
                metrics.DatasetFlags.Add($"Avg loss too small: {metrics.AvgLoss:F2}% (target <= -0.3%)");
                _logger.LogWarning("Backtest dataset flagged: Avg loss {AvgLoss:F2}% is greater than -0.3% threshold", metrics.AvgLoss);
            }
        }

        return metrics;
    }
}

public class BacktestResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double SpeedMultiplier { get; set; }
    public int TotalEvents { get; set; }
    public int SignalsGenerated { get; set; }
    public int AlertsSent { get; set; }
    public DateTime CompletedAt { get; set; }
    public List<(string EventType, string Data, DateTime RecordedAt)> Events { get; set; } = new();
}

public class BacktestMetrics
{
    public decimal WinRate { get; set; }
    public int TotalSignals { get; set; }
    public int Winners { get; set; }
    public int Losers { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AveragePnL { get; set; }
    
    /// <summary>
    /// Average win amount (percentage or dollar value).
    /// </summary>
    public decimal AvgWin { get; set; }
    
    /// <summary>
    /// Average loss amount (percentage or dollar value, typically negative).
    /// </summary>
    public decimal AvgLoss { get; set; }
    
    /// <summary>
    /// Maximum peak-to-trough decline in cumulative PnL.
    /// </summary>
    public decimal Drawdown { get; set; }
    
    /// <summary>
    /// Flags raised when dataset metrics fail validation thresholds.
    /// </summary>
    public List<string> DatasetFlags { get; set; } = new();
}
